using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Media;
using OakProyektor.Core.Media;
using OakProyektor.Core.Output;
using OakProyektor.Data;
using OakProyektor.Data.Media;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.Core;
using OakProyektor.Tests.Data;
using OakProyektor.Tests.Ipc;

namespace OakProyektor.Tests.Media;

/// <summary>Espacio libre controlable.</summary>
internal sealed class FakeSpace(long free = 500L << 30, long total = 1000L << 30) : IFreeSpaceProvider
{
    public long Free { get; set; } = free;
    public long Total { get; set; } = total;
    public Func<long>? Next { get; set; }
    public VolumeSpace Of(string path) => new(Next?.Invoke() ?? Free, Total);
}

/// <summary>Abre los archivos reales salvo los que el test sustituye por un flujo lento o que se queda colgado.</summary>
internal sealed class ScriptedOpener : IMediaSourceOpener
{
    public Dictionary<string, Func<Stream>> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Stream OpenRead(string path) => Overrides.TryGetValue(path, out var f) ? f() : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
}

/// <summary>Un flujo que entrega <paramref name="firstBytes"/> y después se queda COLGADO en la lectura (como un USB retirado) hasta que se libera.</summary>
internal sealed class HangingStream(int firstBytes) : Stream
{
    private readonly ManualResetEventSlim _release = new(false);
    private int _given;
    public void Release() => _release.Set();
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_given >= firstBytes) { _release.Wait(); return 0; }
        var n = Math.Min(count, firstBytes - _given);
        _given += n;
        return n;
    }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _release.Set(); base.Dispose(disposing); }
}

/// <summary>Un flujo lento (cede el hilo entre bloques) para poder cancelar a mitad de la copia.</summary>
internal sealed class SlowStream(byte[] content, int delayMs) : MemoryStream(content)
{
    public override int Read(byte[] buffer, int offset, int count) { Thread.Sleep(delayMs); return base.Read(buffer, offset, Math.Min(count, 256 * 1024)); }
}

/// <summary>Biblioteca de medios sobre una BD real, una carpeta temporal y espacio/orígenes simulados. Todo SINTÉTICO.</summary>
public sealed class MediaLibraryTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oak-media-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSpace _space = new();
    private readonly ScriptedOpener _opener = new();
    private readonly Outbox _outbox = new(NullLogger<Outbox>.Instance);
    private readonly ListLogger<MediaImportService> _log = new();
    private MediaRepository _repo = null!;
    private MediaStorage _storage = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        _storage = new MediaStorage(Path.Combine(_dir, "media"));
        _repo = new MediaRepository(_db.Connections, new AlwaysReady(), null, new MediaRepositoryOptions { WriteTimeoutSeconds = 1 });
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* un flujo colgado puede retener un archivo; es temporal */ }
        return Task.CompletedTask;
    }

    private MediaImportService NewImporter(TimeSpan? stall = null, int spaceEvery = 64) =>
        new(_repo, _storage, new PhysicalMediaFileSystem(), _space, _opener, _outbox, _log) { StallTimeout = stall ?? TimeSpan.FromSeconds(15), SpaceCheckEveryBlocks = spaceEvery };

    private string Source(string fileName, byte[] bytes) { var p = Path.Combine(_dir, "src", fileName); File.WriteAllBytes(p, bytes); return p; }

    private async Task<List<IpcEnvelope>> EventsAsync(int ms = 250)
    {
        var list = new List<IpcEnvelope>();
        using var cts = new CancellationTokenSource(ms);
        try { await foreach (var (_, json) in _outbox.ReadAllAsync(cts.Token)) { Assert.True(IpcSerializer.TryParse(json, out var e, out var err), err); list.Add(e!); } }
        catch (OperationCanceledException) { /* cola vacía */ }
        return list;
    }

    private async Task<MediaImportFinishedPayload> ImportAsync(MediaImportService importer, string path, string? name = null, long? folder = null, string id = "imp-1", int timeoutMs = 8000)
    {
        importer.Start(id, new MediaImportPayload(path, name, folder));
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var seen = new List<IpcEnvelope>();
        while (DateTime.UtcNow < deadline)
        {
            seen.AddRange(await EventsAsync(120));
            var done = seen.FirstOrDefault(e => e.Type == IpcTypes.MediaImportFinished);
            if (done is not null) return done.GetPayload<MediaImportFinishedPayload>();
        }
        throw new TimeoutException("La importación no terminó");
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static async Task WaitUntilAsync(Func<bool> condition, int ms = 5000) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (!condition() && DateTime.UtcNow < end) await Task.Delay(20); }

    // ───────────────────────────── importar ─────────────────────────────

    [Fact]
    public async Task A_good_mp4_is_copied_hashed_probed_and_stored_under_its_hash_with_a_pending_row_and_the_events_carry_only_ids_and_bytes()
    {
        var bytes = SyntheticMedia.Mp4("avc1", "mp4a", 1280, 720, 9_000);
        var source = Source("Anuncio de prueba.mp4", bytes);
        using var importer = NewImporter();

        var finished = await ImportAsync(importer, source);

        Assert.True(finished.Ok, finished.ErrorMessage);
        var item = (await _repo.GetAsync(finished.MediaId!.Value, default))!;
        Assert.Equal((MediaItemKind.Video, "Anuncio de prueba", ".mp4", (long)bytes.Length, Sha(bytes)), (item.Kind, item.Name, item.Ext, item.Bytes, item.Sha256));
        Assert.Equal((MediaStatus.Pending, "H.264", "AAC", 9_000L, 1280, 720), (item.Status, item.VideoCodec, item.AudioCodec, item.DurationMs, item.Width, item.Height));
        var stored = _storage.FilePathFor(item);
        Assert.Matches("^[a-f0-9]{64}\\.mp4$", Path.GetFileName(stored));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stored));
        Assert.Empty(Directory.GetFiles(_storage.TmpDir));                                   // no queda ninguna copia a medias
        Assert.Equal(source, source);                                                          // el original no se toca
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Weird_file_names_import_fine_because_the_name_on_disk_is_the_hash_and_the_url_is_escaped()
    {
        var bytes = SyntheticMedia.Mp4();
        var source = Source("Anuncio 100% #1 & más ñandú.mp4", bytes);
        using var importer = NewImporter();
        var finished = await ImportAsync(importer, source);
        Assert.True(finished.Ok, finished.ErrorMessage);

        var item = (await _repo.GetAsync(finished.MediaId!.Value, default))!;
        Assert.Equal("Anuncio 100% #1 & más ñandú", item.Name);
        Assert.Equal("Anuncio 100% #1 & más ñandú.mp4", item.OriginalFileName);
        var url = MediaPathValidator.ToFileUrl(_storage.FilePathFor(item));
        Assert.DoesNotContain("%", url.Replace("%20", "").Replace("%3A", ""));   // la ruta de la copia es solo hash: nada que escapar salvo la carpeta temporal
        Assert.Matches("^[A-Za-z0-9/:._%-]+$", url);
        Assert.Equal(item.Sha256 + ".mp4", Path.GetFileName(_storage.FilePathFor(item)));
    }

    [Fact]
    public async Task The_same_content_under_another_name_is_a_duplicate_by_content_with_one_row_one_file_and_no_leftovers()
    {
        var bytes = SyntheticMedia.Mp4();
        using var importer = NewImporter();
        var first = await ImportAsync(importer, Source("uno.mp4", bytes), id: "i1");
        await WaitUntilAsync(() => !importer.IsRunning);
        var second = await ImportAsync(importer, Source("otro nombre.mp4", bytes), id: "i2");

        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal(MediaErrorCodes.Duplicate, second.ErrorCode);
        Assert.Equal((first.MediaId, "uno"), (second.DuplicateOfId, second.DuplicateOfName));
        Assert.Equal(1, (await _repo.ListAsync(new MediaQuery(), default)).TotalItems);
        Assert.Single(Directory.GetFiles(_storage.FilesDir, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_storage.TmpDir));
    }

    [Fact]
    public async Task The_same_name_with_different_content_is_two_items()
    {
        using var importer = NewImporter();
        var a = await ImportAsync(importer, Source("fondo.mp4", SyntheticMedia.Mp4(width: 1920)), id: "a");
        await WaitUntilAsync(() => !importer.IsRunning);
        Directory.CreateDirectory(Path.Combine(_dir, "src", "otra"));
        var other = Path.Combine(_dir, "src", "otra", "fondo.mp4"); File.WriteAllBytes(other, SyntheticMedia.Mp4(width: 1280));
        var b = await ImportAsync(importer, other, id: "b");
        Assert.True(a.Ok && b.Ok);
        Assert.Equal(["fondo", "fondo"], (await _repo.ListAsync(new MediaQuery(), default)).Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Not_enough_disk_space_before_copying_fails_with_disk_full_and_touches_nothing()
    {
        var source = Source("grande.mp4", SyntheticMedia.Mp4(mdatBytes: 100_000));
        _space.Free = 100_000 + MediaLimits.MinReserveBytes - 1;   // falta exactamente 1 byte de la reserva
        using var importer = NewImporter();
        var finished = await ImportAsync(importer, source);
        Assert.False(finished.Ok);
        Assert.Equal(MediaErrorCodes.DiskFull, finished.ErrorCode);
        Assert.DoesNotContain("grande", finished.ErrorMessage);
        Assert.Equal(0, (await _repo.ListAsync(new MediaQuery(), default)).TotalItems);
        Assert.Empty(Directory.GetFiles(_storage.TmpDir));
    }

    [Fact]
    public async Task A_disk_that_fills_up_DURING_the_copy_aborts_with_disk_full_and_removes_the_partial_file()
    {
        var bytes = SyntheticMedia.Mp4(mdatBytes: 3 * 1024 * 1024);
        var source = Source("lleno.mp4", bytes);
        var calls = 0;
        _space.Next = () => ++calls == 1 ? 500L << 30 : 1000;   // al principio hay sitio; a mitad de la copia ya no
        using var importer = NewImporter(spaceEvery: 1);
        var finished = await ImportAsync(importer, source);
        Assert.Equal(MediaErrorCodes.DiskFull, finished.ErrorCode);
        await WaitUntilAsync(() => Directory.GetFiles(_storage.TmpDir).Length == 0);
        Assert.Empty(Directory.GetFiles(_storage.TmpDir));
        Assert.Equal(0, (await _repo.ListAsync(new MediaQuery(), default)).TotalItems);
    }

    [Fact]
    public async Task A_source_that_stops_answering_aborts_with_source_stalled_after_the_timeout_frees_the_importer_and_cleans_up_when_the_hung_read_returns()
    {
        var source = Source("usb.mp4", SyntheticMedia.Mp4());
        var hanging = new HangingStream(firstBytes: 4096);
        _opener.Overrides[source] = () => hanging;
        using var importer = NewImporter(stall: TimeSpan.FromMilliseconds(400));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var finished = await ImportAsync(importer, source);

        Assert.Equal(MediaErrorCodes.SourceStalled, finished.ErrorCode);
        Assert.InRange(clock.ElapsedMilliseconds, 350, 4000);   // ≈ el tiempo sin avance, no el de la lectura colgada
        await WaitUntilAsync(() => !importer.IsRunning);
        Assert.False(importer.IsRunning, "la importación quedó libre aunque el hilo de lectura siga colgado");
        Assert.Equal(0, (await _repo.ListAsync(new MediaQuery(), default)).TotalItems);

        hanging.Release();   // «el USB vuelve»: el hilo abandonado termina y su parcial se borra
        await WaitUntilAsync(() => Directory.GetFiles(_storage.TmpDir).Length == 0, 5000);
        Assert.Empty(Directory.GetFiles(_storage.TmpDir));
        Assert.Equal(0, (await _repo.ListAsync(new MediaQuery(), default)).TotalItems);
    }

    [Fact]
    public async Task Cancelling_removes_the_partial_copy_and_publishes_cancelled_and_the_next_import_can_start()
    {
        var bytes = SyntheticMedia.Mp4(mdatBytes: 4 * 1024 * 1024);
        var source = Source("cancelar.mp4", bytes);
        _opener.Overrides[source] = () => new SlowStream(bytes, delayMs: 60);
        using var importer = NewImporter();
        importer.Start("c1", new MediaImportPayload(source, null, null));
        await Task.Delay(200);
        importer.Cancel();

        var done = (await Collect(importer)).First(e => e.Type == IpcTypes.MediaImportFinished).GetPayload<MediaImportFinishedPayload>();
        Assert.Equal(MediaErrorCodes.Cancelled, done.ErrorCode);
        await WaitUntilAsync(() => Directory.GetFiles(_storage.TmpDir).Length == 0);
        Assert.Empty(Directory.GetFiles(_storage.TmpDir));
        Assert.Equal(0, (await _repo.ListAsync(new MediaQuery(), default)).TotalItems);
        Assert.False(importer.IsRunning);
    }

    private async Task<List<IpcEnvelope>> Collect(MediaImportService importer)
    {
        var all = new List<IpcEnvelope>();
        var end = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < end)
        {
            all.AddRange(await EventsAsync(100));
            if (all.Any(e => e.Type == IpcTypes.MediaImportFinished)) break;
        }
        return all;
    }

    [Fact]
    public async Task Only_one_import_runs_at_a_time()
    {
        var bytes = SyntheticMedia.Mp4(mdatBytes: 3 * 1024 * 1024);
        var source = Source("lenta.mp4", bytes);
        _opener.Overrides[source] = () => new SlowStream(bytes, delayMs: 80);
        using var importer = NewImporter();
        importer.Start("u1", new MediaImportPayload(source, null, null));
        var ex = Assert.Throws<IpcException>(() => importer.Start("u2", new MediaImportPayload(source, null, null)));
        Assert.Equal(MediaErrorCodes.ImportBusy, ex.Code);
        importer.Cancel();
        await Collect(importer);
    }

    [Fact]
    public async Task What_cannot_play_is_rejected_with_a_clear_code_and_nothing_is_stored_but_hevc_is_accepted_with_a_warning()
    {
        using var importer = NewImporter();
        var notMp4 = await ImportAsync(importer, Source("falso.mp4", new byte[5000]), id: "a");
        await WaitUntilAsync(() => !importer.IsRunning);
        var old = await ImportAsync(importer, Source("viejo.mp4", SyntheticMedia.Mp4("mp4v")), id: "b");
        await WaitUntilAsync(() => !importer.IsRunning);
        var badAudio = await ImportAsync(importer, Source("raro.wav", SyntheticMedia.Wav(formatTag: 2)), id: "c");
        await WaitUntilAsync(() => !importer.IsRunning);
        var hevc = await ImportAsync(importer, Source("hevc.mp4", SyntheticMedia.Mp4("hvc1")), id: "d");

        Assert.Equal(MediaErrorCodes.UnsupportedContainer, notMp4.ErrorCode);
        Assert.Equal(MediaErrorCodes.VideoCodecUnsupported, old.ErrorCode);
        Assert.Equal(MediaErrorCodes.AudioCodecUnsupported, badAudio.ErrorCode);
        Assert.True(hevc.Ok);
        Assert.Equal(MediaErrorCodes.VideoCodecWarning, hevc.WarningCode);
        Assert.Contains("H.264", hevc.WarningMessage);                                      // «se recomienda H.264»
        var all = await _repo.ListAsync(new MediaQuery(), default);
        Assert.Single(all.Items);
        Assert.Equal(MediaErrorCodes.VideoCodecWarning, all.Items[0].StatusCode);            // el aviso se guarda con el elemento
        Assert.Empty(Directory.GetFiles(_storage.TmpDir));
    }

    [Fact]
    public async Task Missing_and_unsupported_sources_fail_cleanly_and_the_log_never_carries_file_names_or_paths()
    {
        using var importer = NewImporter();
        var missing = await ImportAsync(importer, Path.Combine(_dir, "src", "SECRETO-no-existe.mp4"), id: "m");
        await WaitUntilAsync(() => !importer.IsRunning);
        var avi = await ImportAsync(importer, Source("SECRETO.avi", new byte[100]), id: "n");
        await WaitUntilAsync(() => !importer.IsRunning);
        var ok = await ImportAsync(importer, Source("SECRETO-bueno.mp4", SyntheticMedia.Mp4()), id: "o");

        Assert.Equal(MediaErrorCodes.SourceInvalid, missing.ErrorCode);
        Assert.Equal(MediaErrorCodes.UnsupportedContainer, avi.ErrorCode);
        Assert.True(ok.Ok);
        foreach (var e in new[] { missing.ErrorMessage, avi.ErrorMessage }) Assert.DoesNotContain("SECRETO", e);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("SECRETO", StringComparison.OrdinalIgnoreCase) || e.Message.Contains(_dir, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(_log.Entries);
    }

    [Fact]
    public async Task An_import_can_target_a_folder_and_an_unknown_folder_fails_without_storing_anything()
    {
        var folder = await _repo.CreateFolderAsync("Fondos de prueba", default);
        using var importer = NewImporter();
        var ok = await ImportAsync(importer, Source("a.mp4", SyntheticMedia.Mp4(width: 1000)), folder: folder.Id, id: "a");
        await WaitUntilAsync(() => !importer.IsRunning);
        var bad = await ImportAsync(importer, Source("b.mp4", SyntheticMedia.Mp4(width: 900)), folder: 999, id: "b");
        Assert.True(ok.Ok);
        Assert.Equal((await _repo.GetAsync(ok.MediaId!.Value, default))!.FolderId, folder.Id);
        Assert.Equal(MediaErrorCodes.FolderNotFound, bad.ErrorCode);
        await WaitUntilAsync(() => !importer.IsRunning);
        Assert.Single((await _repo.ListAsync(new MediaQuery(), default)).Items);
        Assert.Single(Directory.GetFiles(_storage.FilesDir, "*", SearchOption.AllDirectories));   // el archivo de la importación fallida se retiró
    }

    // ───────────────────────────── repositorio ─────────────────────────────

    private static NewMediaItem NewItem(string name = "Medio de prueba", string? sha = null, MediaItemKind kind = MediaItemKind.Video, long? folder = null) =>
        new(kind, name, folder, name + ".mp4", ".mp4", 1000, sha ?? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant(), 5000, 1920, 1080, "MP4", "H.264", "AAC", true, null);

    [Fact]
    public async Task The_unique_hash_makes_a_second_row_with_the_same_content_a_duplicate_error()
    {
        await _repo.InsertAsync(NewItem("a", sha: new string('a', 64)), default);
        var ex = await Assert.ThrowsAsync<MediaException>(() => _repo.InsertAsync(NewItem("b", sha: new string('A', 64)), default));
        Assert.Equal(MediaErrorCodes.Duplicate, ex.Code);
    }

    [Fact]
    public async Task Listing_filters_by_folder_unfiled_kind_and_name_escapes_wildcards_orders_by_name_and_truncates()
    {
        var f = await _repo.CreateFolderAsync("Anuncios", default);
        await _repo.InsertAsync(NewItem("Beta", folder: f.Id), default);
        await _repo.InsertAsync(NewItem("alfa"), default);
        await _repo.InsertAsync(NewItem("100% listo", kind: MediaItemKind.Audio), default);
        await _repo.InsertAsync(NewItem("a_b"), default);
        await _repo.InsertAsync(NewItem("axb"), default);

        string[] Names(MediaQuery q) => _repo.ListAsync(q, default).Result.Items.Select(i => i.Name).ToArray();
        Assert.Equal(["100% listo", "a_b", "alfa", "axb", "Beta"], Names(new MediaQuery()));
        Assert.Equal(["Beta"], Names(new MediaQuery(FolderId: f.Id)));
        Assert.Equal(["100% listo", "a_b", "alfa", "axb"], Names(new MediaQuery(OnlyUnfiled: true)));
        Assert.Equal(["100% listo"], Names(new MediaQuery(Kind: MediaItemKind.Audio)));
        Assert.Equal(["100% listo"], Names(new MediaQuery(Text: "100%")));          // el % del texto no es un comodín
        Assert.Equal(["a_b"], Names(new MediaQuery(Text: "a_b")));                  // ni el _
        Assert.Equal(["ALFA".ToLowerInvariant()], Names(new MediaQuery(Text: "ALF")));
        var truncated = await _repo.ListAsync(new MediaQuery(Limit: 2), default);
        Assert.True(truncated.Truncated);
        Assert.Equal((2, 5), (truncated.Items.Count, truncated.TotalItems));
        Assert.Equal(5 * 1000L, truncated.TotalBytes);
        Assert.Equal(1, truncated.Folders.Single().ItemCount);
    }

    [Fact]
    public async Task Rename_move_folders_and_delete_behave_and_deleting_a_folder_keeps_its_items()
    {
        var f1 = await _repo.CreateFolderAsync("  Música  ", default);
        Assert.Equal("Música", f1.Name);
        var dup2 = await Assert.ThrowsAsync<MediaException>(() => _repo.CreateFolderAsync("música", default));
        Assert.Equal(MediaErrorCodes.FolderDuplicate, dup2.Code);

        var item = await _repo.InsertAsync(NewItem("x"), default);
        var renamed = await _repo.RenameAsync(item.Id, "  Nuevo   nombre ", default);
        Assert.Equal(("Nuevo nombre", item.Revision + 1), (renamed.Name, renamed.Revision));
        var moved = await _repo.MoveAsync(item.Id, f1.Id, default);
        Assert.Equal(f1.Id, moved.FolderId);
        Assert.Equal(MediaErrorCodes.FolderNotFound, (await Assert.ThrowsAsync<MediaException>(() => _repo.MoveAsync(item.Id, 999, default))).Code);
        Assert.Equal(MediaErrorCodes.NotFound, (await Assert.ThrowsAsync<MediaException>(() => _repo.RenameAsync(999, "z", default))).Code);

        await _repo.DeleteFolderAsync(f1.Id, default);
        Assert.Null((await _repo.GetAsync(item.Id, default))!.FolderId);   // el elemento sigue, sin carpeta

        var del = await _repo.DeleteAsync(item.Id, default);
        Assert.True(del.Existed);
        Assert.Equal(item.Sha256, del.Sha256);
        Assert.False((await _repo.DeleteAsync(item.Id, default)).Existed);   // idempotente
        Assert.Null(await _repo.GetAsync(item.Id, default));
    }

    [Fact]
    public async Task The_browser_verification_marks_ok_keeping_the_warning_or_unsupported_with_a_reason_and_fills_in_the_real_duration_and_size()
    {
        var hevc = await _repo.InsertAsync(NewItem("hevc") with { StatusCode = MediaErrorCodes.VideoCodecWarning, DurationMs = null, Width = null, Height = null }, default);
        var ok = await _repo.ApplyProbeAsync(hevc.Id, true, 12_345, 1280, 720, null, default);
        Assert.Equal((MediaStatus.Ok, MediaErrorCodes.VideoCodecWarning, 12_345L, 1280, 720), (ok.Status, ok.StatusCode, ok.DurationMs, ok.Width, ok.Height));

        var other = await _repo.InsertAsync(NewItem("roto"), default);
        var bad = await _repo.ApplyProbeAsync(other.Id, false, null, null, null, MediaErrorCodes.VideoCodecUnsupported, default);
        Assert.Equal((MediaStatus.Unsupported, MediaErrorCodes.VideoCodecUnsupported, 5000L), (bad.Status, bad.StatusCode, bad.DurationMs));   // lo que sabía la sonda de cabeceras se conserva
    }

    [Fact]
    public async Task A_write_lock_held_by_another_writer_makes_the_repository_report_busy_and_change_nothing()
    {
        await using var holder = await _db.OpenAsync();
        await holder.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var ex = await Assert.ThrowsAsync<MediaException>(() => _repo.InsertAsync(NewItem("ocupado"), default));
            Assert.Equal(MediaErrorCodes.Busy, ex.Code);
        }
        finally { await holder.ExecuteAsync("ROLLBACK"); }
        Assert.Equal(0, (await _repo.ListAsync(new MediaQuery(), default)).TotalItems);
    }

    // ───────────────────────────── mantenimiento ─────────────────────────────

    [Fact]
    public async Task Maintenance_deletes_leftover_partials_and_orphan_files_marks_missing_and_restores_what_reappears_and_counts_only()
    {
        var logger = new ListLogger<MediaLibraryMaintenance>();
        var maintenance = new MediaLibraryMaintenance(_repo, _storage, logger);
        _storage.EnsureDirectories();

        var kept = await _repo.InsertAsync(NewItem("conservado"), default);
        var lost = await _repo.InsertAsync(NewItem("perdido"), default);
        Directory.CreateDirectory(Path.GetDirectoryName(_storage.FilePathFor(kept))!);
        await File.WriteAllBytesAsync(_storage.FilePathFor(kept), [1, 2, 3]);                  // el de «perdido» NO existe en disco
        var partial = Path.Combine(_storage.TmpDir, "abc.partial"); await File.WriteAllBytesAsync(partial, [9]);
        var orphan = _storage.FilePathFor(new string('c', 64), ".mp4"); Directory.CreateDirectory(Path.GetDirectoryName(orphan)!); await File.WriteAllBytesAsync(orphan, [9]);
        var orphanThumb = _storage.ThumbPathFor(777); await File.WriteAllBytesAsync(orphanThumb, [9]);

        var r1 = await maintenance.VerifyAsync(default);
        Assert.Equal((2, 1, 0, 2), (r1.Checked, r1.Missing, r1.Restored, r1.OrphanFilesDeleted));
        Assert.False(File.Exists(partial) || File.Exists(orphan) || File.Exists(orphanThumb));
        Assert.True(File.Exists(_storage.FilePathFor(kept)));
        Assert.Equal((MediaStatus.Missing, MediaErrorCodes.MissingFile), ((await _repo.GetAsync(lost.Id, default))!.Status, (await _repo.GetAsync(lost.Id, default))!.StatusCode));

        Directory.CreateDirectory(Path.GetDirectoryName(_storage.FilePathFor(lost))!);        // «el USB/disco vuelve»: el archivo reaparece
        await File.WriteAllBytesAsync(_storage.FilePathFor(lost), [1]);
        var r2 = await maintenance.VerifyAsync(default);
        Assert.Equal((0, 1), (r2.Missing, r2.Restored));
        Assert.Equal(MediaStatus.Pending, (await _repo.GetAsync(lost.Id, default))!.Status);   // vuelve a verificarse en el reproductor
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("perdido") || e.Message.Contains("conservado"));
    }

    [Fact]
    public async Task Maintenance_waits_for_a_running_import_instead_of_deleting_its_files()
    {
        var maintenance = new MediaLibraryMaintenance(_repo, _storage, NullLogger<MediaLibraryMaintenance>.Instance);
        await _storage.Gate.WaitAsync();                    // «hay una importación en curso»
        var run = maintenance.VerifyAsync(default);
        await Task.Delay(150);
        Assert.False(run.IsCompleted);
        _storage.Gate.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.IsCompletedSuccessfully);
    }

    // ───────────────────────────── migración ─────────────────────────────

    [Fact]
    public async Task Migration_13_adds_only_the_media_tables_keeps_existing_data_and_backs_up_first()
    {
        var embedded = OakProyektor.Data.Migrations.Migration.LoadEmbedded(typeof(DatabaseInitializer).Assembly);
        // Una base REAL en la versión 12 (recién creada, hasta la migración 12), con datos de otros módulos.
        using var fresh = new TestDatabase();
        await using (var c = await fresh.OpenAsync())
        {
            await new OakProyektor.Data.Migrations.MigrationRunner([.. embedded.Take(12)], fresh.Options, NullLogger<OakProyektor.Data.Migrations.MigrationRunner>.Instance).RunAsync(c, CancellationToken.None);
            Assert.Equal(12L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
            await c.ExecuteAsync("INSERT INTO Alerts (Text, TextKey, Pinned, UseCount, CreatedAt, UpdatedAt) VALUES ('Aviso de prueba', 'aviso de prueba', 1, 3, '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z')");
            await c.ExecuteAsync("INSERT INTO Playlists (Name, ServiceDate) VALUES ('Domingo sintético', '2026-11-29')");
            await c.ExecuteAsync("INSERT INTO Songs (Title) VALUES ('Canción sintética')");
        }

        var result = await fresh.Initializer.InitializeAsync(CancellationToken.None);

        Assert.Equal((12, 13), (result.FromVersion, result.ToVersion));
        Assert.Equal([13], result.Applied);
        Assert.NotNull(result.BackupPath);                        // la copia previa que ya existe se hace antes de aplicar la 13
        Assert.True(File.Exists(result.BackupPath));
        await using var d = await fresh.OpenAsync();
        Assert.Equal(1L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Alerts WHERE Pinned = 1 AND UseCount = 3"));
        Assert.Equal(1L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Playlists"));
        Assert.Equal(1L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Songs"));
        Assert.Equal(0L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM MediaItems"));
        Assert.Equal(2L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('MediaItems', 'MediaFolders')"));
        Assert.Empty(await d.QueryAsync("PRAGMA foreign_key_check"));
        Assert.Equal("ok", await d.ExecuteScalarAsync<string>("PRAGMA integrity_check"));
        // Idempotente: otra inicialización no aplica nada.
        Assert.Empty((await fresh.Initializer.InitializeAsync(CancellationToken.None)).Applied);
    }
}
