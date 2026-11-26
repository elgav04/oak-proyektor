using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Handlers;
using OakProyektor.App.Media;
using OakProyektor.Core.Media;
using OakProyektor.Core.Output;
using OakProyektor.Data;
using OakProyektor.Data.Media;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.App;
using OakProyektor.Tests.Core;
using OakProyektor.Tests.Data;
using OakProyektor.Tests.Ipc;

namespace OakProyektor.Tests.Media;

/// <summary>Los mensajes media.* de extremo a extremo (despachador + handlers reales + BD real + carpeta temporal): respuestas, errores con código, eventos solo con ids y carriles.</summary>
public sealed class MediaIpcTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oak-media-ipc", Guid.NewGuid().ToString("N"));
    private readonly Outbox _outbox = new(NullLogger<Outbox>.Instance);
    private readonly FakeOutputController _output = new();
    private readonly FakeMediaFileSystem _fs = new();
    private readonly ListLogger<IpcDispatcher> _dispatcherLog = new();
    private MediaRepository _repo = null!;
    private MediaStorage _storage = null!;
    private IpcDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _storage = new MediaStorage(Path.Combine(_dir, "media"));
        _repo = new MediaRepository(_db.Connections, new AlwaysReady(), null, new MediaRepositoryOptions { WriteTimeoutSeconds = 1 });
        var importer = new MediaImportService(_repo, _storage, new PhysicalMediaFileSystem(), new FakeSpace(), new FileSourceOpener(), _outbox, NullLogger<MediaImportService>.Instance);
        var maintenance = new MediaLibraryMaintenance(_repo, _storage, NullLogger<MediaLibraryMaintenance>.Instance);
        IIpcHandler[] handlers =
        [
            new MediaListHandler(_repo, _storage, new FakeSpace(free: 123456)), new MediaGetHandler(_repo, _storage), new MediaImportHandler(importer), new MediaCancelImportHandler(importer),
            new MediaRenameHandler(_repo, _outbox), new MediaMoveHandler(_repo, _outbox), new MediaDeleteHandler(_repo, _storage, _output, _outbox, NullLogger<MediaDeleteHandler>.Instance),
            new MediaCreateFolderHandler(_repo, _outbox), new MediaRenameFolderHandler(_repo, _outbox), new MediaDeleteFolderHandler(_repo, _outbox),
            new MediaReportProbeHandler(_repo, _outbox), new MediaSetThumbnailHandler(_repo, _storage, _outbox), new MediaVerifyLibraryHandler(maintenance, _outbox),
            new MediaSetBackgroundHandler(_repo, _storage, _fs, _output),
        ];
        _dispatcher = new IpcDispatcher(handlers, _dispatcherLog);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporal */ }
        return Task.CompletedTask;
    }

    private static string Msg(string kind, string type, object payload, string id) =>
        $$"""{"v":{{IpcCatalog.ProtocolVersion}},"id":"{{id}}","kind":"{{kind}}","type":"{{type}}","payload":{{JsonSerializer.Serialize(payload, IpcSerializer.Options)}}}""";

    private async Task<T> QueryAsync<T>(string type, object payload) where T : class =>
        ((JsonElement)(await _dispatcher.DispatchAsync(Msg(IpcKind.Query, type, payload, "q1"), CancellationToken.None))!.GetPayload<IpcReply>().Result!).Deserialize<T>(IpcSerializer.Options)!;

    private async Task<string?> CommandErrorAsync(string type, object payload, string id = "c1")
    {
        var reply = await _dispatcher.DispatchAsync(Msg(IpcKind.Command, type, payload, id), CancellationToken.None);
        return reply is null ? null : reply.GetPayload<CommandFailedPayload>().Error.Code;
    }

    private async Task<List<IpcEnvelope>> PublishedAsync()
    {
        var list = new List<IpcEnvelope>();
        using var cts = new CancellationTokenSource(250);
        try { await foreach (var (_, json) in _outbox.ReadAllAsync(cts.Token)) { Assert.True(IpcSerializer.TryParse(json, out var e, out var err), err); list.Add(e!); } }
        catch (OperationCanceledException) { /* vacía */ }
        return list;
    }

    private async Task<MediaItem> SeedAsync(string name = "Fondo de prueba", MediaItemKind kind = MediaItemKind.Video, string ext = ".mp4", bool createFile = true, string? sha = null)
    {
        sha ??= Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        var item = await _repo.InsertAsync(new NewMediaItem(kind, name, null, name + ext, ext, 100, sha, 5000, 1920, 1080, "MP4", "H.264", "AAC", true, null), default);
        if (createFile)
        {
            var path = _storage.FilePathFor(item);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }
        return item;
    }

    private static string Jpeg64(int w, int h) => Convert.ToBase64String(SyntheticMedia.Jpeg(w, h));

    // ───────────────────────────── lecturas ─────────────────────────────

    [Fact]
    public async Task The_list_carries_folders_totals_free_space_and_file_urls_that_are_escaped_local_copies()
    {
        await _repo.CreateFolderAsync("Anuncios", default);
        var item = await SeedAsync("Fondo con % y # y ñ");
        var list = await QueryAsync<MediaListPayload>(IpcTypes.MediaList, new MediaListRequest(null, false, null, null, null));

        Assert.Equal(("Anuncios", 1, 123456L, 100L), (list.Folders.Single().Name, list.TotalItems, list.FreeBytes, list.TotalBytes));
        var dto = list.Items.Single();
        Assert.Equal("Fondo con % y # y ñ", dto.Name);
        Assert.Equal(MediaPathValidator.ToFileUrl(_storage.FilePathFor(item)), dto.FileUrl);
        Assert.StartsWith("file:///", dto.FileUrl);
        Assert.DoesNotContain("Fondo", dto.FileUrl);            // la URL lleva el hash, nunca el nombre del operador
        Assert.Null(dto.ThumbnailUrl);
        Assert.Equal(MediaStatus.Pending, dto.Status);

        var one = await QueryAsync<MediaItemDto>(IpcTypes.MediaGet, new MediaGetRequest(item.Id));
        Assert.Equal(item.Id, one.Id);
        var missing = await _dispatcher.DispatchAsync(Msg(IpcKind.Query, IpcTypes.MediaGet, new MediaGetRequest(999), "q2"), CancellationToken.None);
        Assert.False(missing!.GetPayload<IpcReply>().Ok);
        Assert.Equal(MediaErrorCodes.NotFound, missing.ReplyError().Code);
    }

    // ───────────────────────────── miniaturas ─────────────────────────────

    [Fact]
    public async Task A_valid_thumbnail_is_stored_and_served_with_a_revision_and_hostile_ones_are_refused_with_a_clear_code()
    {
        var item = await SeedAsync();
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaSetThumbnail, new MediaSetThumbnailPayload(item.Id, Jpeg64(320, 180))));
        var dto = (await QueryAsync<MediaListPayload>(IpcTypes.MediaList, new MediaListRequest(null, false, null, null, null))).Items.Single();
        Assert.NotNull(dto.ThumbnailUrl);
        Assert.Matches(@"\.jpg\?r=\d+$", dto.ThumbnailUrl!);
        Assert.True(File.Exists(_storage.ThumbPathFor(item.Id)));
        Assert.Empty(Directory.GetFiles(_storage.ThumbsDir, "*.tmp"));

        foreach (var (label, b64) in new[]
        {
            ("vacía", ""), ("no es base64", "%%%no%%%"), ("no es JPEG", Convert.ToBase64String(new byte[100])), ("PNG", Convert.ToBase64String(SyntheticMedia.Png(320, 180))),
            ("demasiado ancha", Jpeg64(641, 180)), ("demasiado alta", Jpeg64(320, 361)), ("enorme", Convert.ToBase64String(new byte[MediaLimits.MaxThumbnailBytes + 10])),
        })
            Assert.Equal(MediaErrorCodes.InvalidThumbnail, await CommandErrorAsync(IpcTypes.MediaSetThumbnail, new MediaSetThumbnailPayload(item.Id, b64)));
        Assert.Equal(MediaErrorCodes.NotFound, await CommandErrorAsync(IpcTypes.MediaSetThumbnail, new MediaSetThumbnailPayload(999, Jpeg64(320, 180))));
    }

    // ───────────────────────────── verificación del panel ─────────────────────────────

    [Fact]
    public async Task The_panels_verification_marks_ok_with_the_real_duration_or_unsupported_with_a_code_and_sanitizes_nonsense_values()
    {
        var a = await SeedAsync("a");
        var b = await SeedAsync("b");
        var c = await SeedAsync("c");
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaReportProbe, new MediaReportProbePayload(a.Id, true, 12_000, 1280, 720, null)));
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaReportProbe, new MediaReportProbePayload(b.Id, false, null, null, null, "unsupported")));
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaReportProbe, new MediaReportProbePayload(c.Id, true, -5, 999_999, 0, null)));

        var ga = (await _repo.GetAsync(a.Id, default))!; var gb = (await _repo.GetAsync(b.Id, default))!; var gc = (await _repo.GetAsync(c.Id, default))!;
        Assert.Equal((MediaStatus.Ok, 12_000L, 1280, 720), (ga.Status, ga.DurationMs, ga.Width, ga.Height));
        Assert.Equal((MediaStatus.Unsupported, MediaErrorCodes.VideoCodecUnsupported), (gb.Status, gb.StatusCode));
        Assert.Equal((MediaStatus.Ok, 5000L, 1920, 1080), (gc.Status, gc.DurationMs, gc.Width, gc.Height));   // los valores absurdos se ignoran: queda lo que dijo la sonda
        Assert.Equal(MediaErrorCodes.NotFound, await CommandErrorAsync(IpcTypes.MediaReportProbe, new MediaReportProbePayload(999, true, 1, 1, 1, null)));
    }

    // ───────────────────────────── servir un fondo ─────────────────────────────

    [Fact]
    public async Task A_background_is_served_from_the_local_copy_by_id_after_the_usual_validation_and_audio_missing_or_unsupported_items_are_refused()
    {
        var video = await SeedAsync("video");
        var image = await SeedAsync("imagen", MediaItemKind.Image, ".png");
        var audio = await SeedAsync("pista", MediaItemKind.Audio, ".mp3");
        var gone = await SeedAsync("falta", createFile: false);
        var bad = await SeedAsync("roto");
        await _repo.ApplyProbeAsync(bad.Id, false, null, null, null, MediaErrorCodes.VideoCodecUnsupported, default);
        await _repo.SetStatusAsync([gone.Id], MediaStatus.Missing, MediaErrorCodes.MissingFile, default);

        Assert.Null(await CommandErrorAsync(IpcTypes.MediaSetBackground, new MediaSetBackgroundPayload(video.Id, true, 300)));
        var set = Assert.Single(_output.Backgrounds);
        Assert.Equal((BackgroundKind.Video, true, 300), (set.Kind, set.Muted, set.TransitionMs));
        Assert.Equal(MediaPathValidator.ToFileUrl(_storage.FilePathFor(video)), set.MediaUrl);   // solo la URL validada de la COPIA local
        Assert.Equal("video", set.FileName);

        Assert.Null(await CommandErrorAsync(IpcTypes.MediaSetBackground, new MediaSetBackgroundPayload(image.Id, true, 0)));
        Assert.Equal(BackgroundKind.Image, _output.Backgrounds[1].Kind);

        Assert.Equal(MediaErrorCodes.NotBackgroundable, await CommandErrorAsync(IpcTypes.MediaSetBackground, new MediaSetBackgroundPayload(audio.Id, true, 0)));
        Assert.Equal(MediaErrorCodes.MissingFile, await CommandErrorAsync(IpcTypes.MediaSetBackground, new MediaSetBackgroundPayload(gone.Id, true, 0)));
        Assert.Equal(MediaErrorCodes.VideoCodecUnsupported, await CommandErrorAsync(IpcTypes.MediaSetBackground, new MediaSetBackgroundPayload(bad.Id, true, 0)));
        Assert.Equal(MediaErrorCodes.NotFound, await CommandErrorAsync(IpcTypes.MediaSetBackground, new MediaSetBackgroundPayload(999, true, 0)));
        Assert.Equal("output.invalidContent", await CommandErrorAsync(IpcTypes.MediaSetBackground, new MediaSetBackgroundPayload(video.Id, true, 99_999)));
        Assert.Equal(2, _output.Backgrounds.Count);   // los rechazados no llegaron a la salida
    }

    [Fact]
    public async Task A_file_that_vanished_from_disk_is_refused_by_the_validator_even_if_the_row_still_says_pending()
    {
        var ghost = await SeedAsync("fantasma", createFile: false);
        _fs.Files[Path.GetFullPath(_storage.FilePathFor(ghost))] = new FileProbe(false, 0, null);   // el sistema de archivos dice que ya no está
        Assert.Equal(MediaErrorCodes.MissingFile, await CommandErrorAsync(IpcTypes.MediaSetBackground, new MediaSetBackgroundPayload(ghost.Id, true, 0)));
        Assert.Empty(_output.Backgrounds);
    }

    // ───────────────────────────── borrar ─────────────────────────────

    [Fact]
    public async Task Deleting_removes_the_row_the_file_and_the_thumbnail_and_the_event_has_only_ids()
    {
        var item = await SeedAsync("para borrar");
        await CommandErrorAsync(IpcTypes.MediaSetThumbnail, new MediaSetThumbnailPayload(item.Id, Jpeg64(320, 180)));
        await PublishedAsync();
        var file = _storage.FilePathFor(item);
        Assert.True(File.Exists(file));

        Assert.Null(await CommandErrorAsync(IpcTypes.MediaDelete, new MediaDeletePayload(item.Id), "del-1"));
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(_storage.ThumbPathFor(item.Id)));
        Assert.Null(await _repo.GetAsync(item.Id, default));
        var changed = Assert.Single(await PublishedAsync(), e => e.Type == IpcTypes.MediaChanged).GetPayload<MediaChangedPayload>();
        Assert.Equal(("del-1", "deleted", (long?)item.Id), (changed.CommandId, changed.Kind, changed.MediaId));
        Assert.DoesNotContain("borrar", JsonSerializer.Serialize(changed, IpcSerializer.Options));
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaDelete, new MediaDeletePayload(item.Id)));   // idempotente
    }

    [Fact]
    public async Task The_background_that_is_on_the_output_cannot_be_deleted_and_nothing_changes()
    {
        var item = await SeedAsync("fondo en uso");
        var url = MediaPathValidator.ToFileUrl(_storage.FilePathFor(item));
        _output.State = OutputState.Initial.WithBackground(new BackgroundLayer(BackgroundKind.Video, "#000000", url, "fondo en uso", true, 0, null));

        Assert.Equal("media.inUse", await CommandErrorAsync(IpcTypes.MediaDelete, new MediaDeletePayload(item.Id)));
        Assert.NotNull(await _repo.GetAsync(item.Id, default));
        Assert.True(File.Exists(_storage.FilePathFor(item)));

        _output.State = OutputState.Initial;   // al cambiar el fondo ya se puede
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaDelete, new MediaDeletePayload(item.Id)));
    }

    // ───────────────────────────── carpetas, renombrar, mover, verificar ─────────────────────────────

    [Fact]
    public async Task Folders_rename_and_move_work_and_report_their_errors_with_codes()
    {
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaCreateFolder, new MediaCreateFolderPayload("Anuncios")));
        Assert.Equal(MediaErrorCodes.FolderDuplicate, await CommandErrorAsync(IpcTypes.MediaCreateFolder, new MediaCreateFolderPayload("anuncios")));
        var folder = (await QueryAsync<MediaListPayload>(IpcTypes.MediaList, new MediaListRequest(null, false, null, null, null))).Folders.Single();
        var item = await SeedAsync("m");

        Assert.Null(await CommandErrorAsync(IpcTypes.MediaMove, new MediaMovePayload(item.Id, folder.Id)));
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaRename, new MediaRenamePayload(item.Id, "  Otro  nombre ")));
        Assert.Equal(MediaErrorCodes.FolderNotFound, await CommandErrorAsync(IpcTypes.MediaMove, new MediaMovePayload(item.Id, 999)));
        Assert.Equal(MediaErrorCodes.FolderNotFound, await CommandErrorAsync(IpcTypes.MediaRenameFolder, new MediaRenameFolderPayload(999, "x")));
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaRenameFolder, new MediaRenameFolderPayload(folder.Id, "Videos")));
        var after = await QueryAsync<MediaListPayload>(IpcTypes.MediaList, new MediaListRequest(folder.Id, false, null, null, null));
        Assert.Equal(("Videos", "Otro nombre"), (after.Folders.Single().Name, after.Items.Single().Name));
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaDeleteFolder, new MediaDeleteFolderPayload(folder.Id)));
        Assert.Single((await QueryAsync<MediaListPayload>(IpcTypes.MediaList, new MediaListRequest(null, true, null, null, null))).Items);   // el elemento sigue, sin carpeta
    }

    [Fact]
    public async Task Verify_library_publishes_the_missing_count_and_flags_the_missing_file()
    {
        await SeedAsync("esta");
        await SeedAsync("falta", createFile: false);
        await PublishedAsync();
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaVerifyLibrary, new { }, "ver-1"));
        var changed = Assert.Single(await PublishedAsync(), e => e.Type == IpcTypes.MediaChanged).GetPayload<MediaChangedPayload>();
        Assert.Equal(("ver-1", "verified", 1), (changed.CommandId, changed.Kind, changed.Count));
        Assert.Contains((await QueryAsync<MediaListPayload>(IpcTypes.MediaList, new MediaListRequest(null, false, null, null, null))).Items, i => i.Status == MediaStatus.Missing && i.FileUrl is null);
    }

    // ───────────────────────────── carriles ─────────────────────────────

    [Fact]
    public void Everything_that_touches_disk_or_the_database_goes_through_the_slow_lane_and_reads_and_cancelling_stay_live()
    {
        IpcLane Lane(string kind, string type) => IpcPriority.LaneOf(new IpcEnvelope(IpcCatalog.ProtocolVersion, "x", kind, type, default));
        foreach (var type in new[] { IpcTypes.MediaImport, IpcTypes.MediaRename, IpcTypes.MediaMove, IpcTypes.MediaDelete, IpcTypes.MediaCreateFolder, IpcTypes.MediaRenameFolder, IpcTypes.MediaDeleteFolder,
                                     IpcTypes.MediaReportProbe, IpcTypes.MediaSetThumbnail, IpcTypes.MediaVerifyLibrary, IpcTypes.MediaSetBackground })
            Assert.Equal(IpcLane.Slow, Lane(IpcKind.Command, type));
        Assert.Equal(IpcLane.Live, Lane(IpcKind.Query, IpcTypes.MediaList));
        Assert.Equal(IpcLane.Live, Lane(IpcKind.Query, IpcTypes.MediaGet));
        Assert.Equal(IpcLane.Live, Lane(IpcKind.Command, IpcTypes.MediaCancelImport));
    }

    [Fact]
    public async Task Import_returns_immediately_and_an_invalid_source_is_reported_by_the_finished_event_not_as_a_command_failure()
    {
        var reply = await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.MediaImport, new MediaImportPayload(@"C:\no\existe\SECRETO.mp4", null, null), "imp-9"), CancellationToken.None);
        Assert.Null(reply);
        var end = DateTime.UtcNow.AddSeconds(5);
        IpcEnvelope? finished = null;
        while (finished is null && DateTime.UtcNow < end) finished = (await PublishedAsync()).FirstOrDefault(e => e.Type == IpcTypes.MediaImportFinished);
        var p = finished!.GetPayload<MediaImportFinishedPayload>();
        Assert.Equal(("imp-9", false, MediaErrorCodes.SourceInvalid), (p.ImportId, p.Ok, p.ErrorCode));
        Assert.DoesNotContain("SECRETO", JsonSerializer.Serialize(p, IpcSerializer.Options));
        Assert.DoesNotContain(_dispatcherLog.Entries, e => e.Message.Contains("SECRETO"));
    }
}
