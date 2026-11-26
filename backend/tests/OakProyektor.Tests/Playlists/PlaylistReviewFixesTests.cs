using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Handlers;
using OakProyektor.Core.Library;
using OakProyektor.Core.Playlists;
using OakProyektor.Data;
using OakProyektor.Data.Playlists;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.App;
using OakProyektor.Tests.Data;
using OakProyektor.Tests.Ipc;
using Xunit.Abstractions;

namespace OakProyektor.Tests.Playlists;

/// <summary>
/// Correcciones de la revisión de las listas: pasajes resueltos con UNA consulta, borrar con revisión, «editar sin cambios» no sube la revisión ni avisa, y la carga del
/// bloque playlistDisplay de settings.json. Datos SINTÉTICOS (texto bíblico inventado).
/// </summary>
public sealed class PlaylistReviewFixesTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly ITestOutputHelper _out;
    private PlaylistRepository _repo = null!;

    public PlaylistReviewFixesTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _repo = new PlaylistRepository(_db.Connections, new AlwaysReady(), null, new PlaylistRepositoryOptions { WriteTimeoutSeconds = 1 });
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private static async Task<long> InstallAsync(SqliteConnection c, string abbreviation, int book, int chapter, params int[] verses)
    {
        var bibleId = await c.ExecuteScalarAsync<long?>("SELECT Id FROM Bibles WHERE Abbreviation = @a", new { a = abbreviation })
            ?? await c.ExecuteScalarAsync<long>("INSERT INTO Bibles (Abbreviation, Name, Language) VALUES (@a, @n, 'es'); SELECT last_insert_rowid();", new { a = abbreviation, n = $"Biblia {abbreviation}" });
        var bookId = await c.ExecuteScalarAsync<long?>("SELECT Id FROM Books WHERE BibleId = @b AND BookNumber = @n", new { b = bibleId, n = book })
            ?? await c.ExecuteScalarAsync<long>("INSERT INTO Books (BibleId, BookNumber, Name, Testament) VALUES (@b, @n, @name, 'NT'); SELECT last_insert_rowid();", new { b = bibleId, n = book, name = $"Libro {book}" });
        foreach (var v in verses)
            await c.ExecuteAsync("INSERT INTO Verses (BookId, Chapter, VerseNumber, Text) VALUES (@k, @c, @v, @t) ON CONFLICT(BookId, Chapter, VerseNumber) DO NOTHING", new { k = bookId, c = chapter, v, t = $"texto sintético {v}" });
        return bibleId;
    }

    private static PlaylistItemInput Passage(int book, int chapter, int start, int? end, string bible) =>
        new(PlaylistItemTypes.Passage, null, book, chapter, start, end, bible, null, null, false);

    // ───────────────────────────── 1. una sola consulta ─────────────────────────────

    [Fact]
    public async Task Resolving_the_passages_of_a_list_takes_ONE_query_whatever_the_number_of_passages()
    {
        await using (var c = await _db.OpenAsync()) { await InstallAsync(c, "TST", 43, 3, 1, 2, 3); await InstallAsync(c, "DEF", 43, 3, 1, 2, 3); }
        var id = (await _repo.CreateAsync("Cien pasajes", null, null, default)).PlaylistId;
        for (var i = 0; i < 100; i++) await _repo.AddItemAsync(id, Passage(43, 3, 1 + i % 3, null, i % 4 == 0 ? "NOINSTALADA" : "TST"), null, default);

        var queries = 0;
        await using var conn = await _db.OpenAsync();
        var results = await PassageResolver.ResolvePlaylistAsync(conn, id, "DEF", CancellationToken.None, () => queries++);

        Assert.Equal(100, results.Count);
        Assert.Equal(1, queries);   // antes: hasta 6 por pasaje (Biblia preferida, libro, versículos y lo mismo con la por defecto)
    }

    /// <summary>Todas las combinaciones que distinguía la resolución pasaje a pasaje dan EXACTAMENTE lo mismo resueltas en bloque.</summary>
    [Fact]
    public async Task Resolving_in_one_query_gives_exactly_the_same_result_as_resolving_each_passage_alone()
    {
        await using (var c = await _db.OpenAsync())
        {
            await InstallAsync(c, "TST", 43, 3, 1, 2, 3, 5);     // falta el 4: un rango con hueco sigue resolviendo
            await InstallAsync(c, "TST", 19, 23, 1);
            await InstallAsync(c, "DEF", 43, 3, 1, 2, 3, 4, 5, 6);
            await InstallAsync(c, "DEF", 1, 1, 1);
            await InstallAsync(c, "tst", 43, 3, 1);              // otra Biblia que solo difiere en mayúsculas: la comparación es tal cual
        }
        var id = (await _repo.CreateAsync("Combinaciones", null, null, default)).PlaylistId;
        var cases = new (int Book, int Chapter, int Start, int? End, string Bible)[]
        {
            (43, 3, 1, null, "TST"), (43, 3, 1, 3, "TST"), (43, 3, 4, null, "TST"), (43, 3, 3, 5, "TST"), (43, 3, 6, 8, "TST"),   // resuelto, rango, hueco, rango con hueco, fuera
            (43, 4, 1, null, "TST"), (19, 23, 1, null, "TST"), (1, 1, 1, null, "TST"),                                          // capítulo inexistente, otro libro, libro que la preferida no tiene
            (43, 3, 6, null, "TST"), (1, 1, 1, null, "NOINSTALADA"), (43, 3, 1, null, "NOINSTALADA"), (2, 1, 1, null, "NOINSTALADA"),   // se ofrece la por defecto si la tiene
            (43, 3, 1, null, "DEF"), (43, 3, 1, null, "tst"), (43, 3, 2, null, "tst"),
        };
        foreach (var k in cases) await _repo.AddItemAsync(id, Passage(k.Book, k.Chapter, k.Start, k.End, k.Bible), null, default);

        foreach (var defaultBible in new[] { "DEF", null, "TST", "NOINSTALADA" })
        {
            await using var conn = await _db.OpenAsync();
            var batch = await PassageResolver.ResolvePlaylistAsync(conn, id, defaultBible, CancellationToken.None);
            Assert.Equal(cases.Length, batch.Count);
            for (var i = 0; i < cases.Length; i++)
            {
                var k = cases[i];
                var alone = await PassageResolver.ResolveAsync(conn, new PassageReference(k.Book, k.Chapter, k.Start, k.End, k.Bible), defaultBible, CancellationToken.None);
                Assert.True(alone == batch[i].Resolution, $"caso {i} ({k}) con la por defecto {defaultBible ?? "(ninguna)"}: {batch[i].Resolution} en vez de {alone}");
            }
        }
    }

    [Fact]
    public async Task A_list_without_passages_or_that_does_not_exist_resolves_to_nothing_without_failing()
    {
        var id = (await _repo.CreateAsync("Sin pasajes", null, null, default)).PlaylistId;
        await _repo.AddItemAsync(id, new PlaylistItemInput(PlaylistItemTypes.Note, null, null, null, null, null, null, "Nota", null, false), null, default);
        await using var conn = await _db.OpenAsync();
        Assert.Empty(await PassageResolver.ResolvePlaylistAsync(conn, id, "DEF", CancellationToken.None));
        Assert.Empty(await PassageResolver.ResolvePlaylistAsync(conn, 9999, "DEF", CancellationToken.None));
    }

    /// <summary>Medición (no una prueba de aceptación): `playlist.get` de una lista de 100 pasajes. Se ejecuta con OAK_BENCH=1; informa en la salida del test.</summary>
    [BenchFact]
    public async Task Benchmark_playlist_get_with_100_passages()
    {
        await using (var c = await _db.OpenAsync())
        {
            for (var b = 1; b <= 3; b++) await InstallAsync(c, $"B{b}", 43, 3, Enumerable.Range(1, 36).ToArray());
            await InstallAsync(c, "DEF", 43, 3, Enumerable.Range(1, 36).ToArray());
        }
        var id = (await _repo.CreateAsync("Cien pasajes", null, null, default)).PlaylistId;
        for (var i = 0; i < 100; i++) await _repo.AddItemAsync(id, Passage(43, 3, 1 + i % 30, 3 + i % 30, i % 5 == 0 ? "NOINSTALADA" : $"B{1 + i % 3}"), null, default);

        for (var i = 0; i < 5; i++) await _repo.GetDetailAsync(id, "DEF", default);   // calentar
        var times = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            var sw = Stopwatch.StartNew();
            var detail = await _repo.GetDetailAsync(id, "DEF", default);
            sw.Stop();
            Assert.Equal(100, detail!.Items.Count);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        var queries = 0;
        await using (var conn = await _db.OpenAsync()) await PassageResolver.ResolvePlaylistAsync(conn, id, "DEF", CancellationToken.None, () => queries++);
        _out.WriteLine($"playlist.get, 100 pasajes (20 con la Biblia preferida ausente y la por defecto ofrecida): mediana {times[times.Count / 2]:F2} ms, p90 {times[(int)(times.Count * 0.9)]:F2} ms, mínimo {times[0]:F2} ms; consultas de la resolución: {queries}");
    }

    // ───────────────────────────── 2. borrar con revisión ─────────────────────────────

    [Fact]
    public async Task Deleting_a_list_with_a_STALE_revision_fails_with_conflict_and_deletes_nothing()
    {
        var id = (await _repo.CreateAsync("Domingo", "2026-11-29", null, default)).PlaylistId;
        await _repo.AddItemAsync(id, new PlaylistItemInput(PlaylistItemTypes.Note, null, null, null, null, null, null, "Ofrendas", null, false), null, default);   // revisión 2

        var ex = await Assert.ThrowsAsync<PlaylistException>(() => _repo.DeleteAsync(id, 1, default));

        Assert.Equal(PlaylistErrorCodes.Conflict, ex.Code);
        var still = await _repo.GetDetailAsync(id, null, default);
        Assert.NotNull(still);
        Assert.Single(still!.Items);
        await _repo.DeleteAsync(id, 2, default);   // con la revisión vigente sí
        Assert.Null(await _repo.GetDetailAsync(id, null, default));
    }

    // ───────────────────────────── 3. editar sin cambios ─────────────────────────────

    [Fact]
    public async Task Updating_an_item_WITHOUT_changes_does_not_bump_the_revision()
    {
        var id = (await _repo.CreateAsync("Domingo", null, null, default)).PlaylistId;
        var note = (await _repo.AddItemAsync(id, new PlaylistItemInput(PlaylistItemTypes.Note, null, null, null, null, null, null, "Ofrendas", "Pasan los diáconos", true), null, default)).ItemId!.Value;
        var passage = (await _repo.AddItemAsync(id, Passage(43, 3, 1, null, "TST"), null, default)).ItemId!.Value;
        var rev = (await _repo.GetDetailAsync(id, null, default))!.Revision;

        foreach (var (itemId, update) in new[]
        {
            (note, new PlaylistItemUpdate(null, null, null, null)),                                  // todo nulo
            (note, new PlaylistItemUpdate("Ofrendas", "Pasan los diáconos", true, null)),            // valores idénticos
            (note, new PlaylistItemUpdate("  Ofrendas ", " Pasan los diáconos ", null, null)),       // idénticos tras normalizar
            (passage, new PlaylistItemUpdate(null, null, null, null)),
            (passage, new PlaylistItemUpdate(null, null, null, " TST ")),                            // la misma Biblia
        })
        {
            var r = await _repo.UpdateItemAsync(id, itemId, update, rev, default);
            Assert.False(r.Changed, $"{update} no cambia nada");
            Assert.Equal(rev, r.Revision);
            Assert.Equal(rev, (await _repo.GetDetailAsync(id, null, default))!.Revision);
        }

        var changed = await _repo.UpdateItemAsync(id, note, new PlaylistItemUpdate(null, "", null, null), rev, default);   // borrar el texto SÍ es un cambio
        Assert.True(changed.Changed);
        Assert.Equal(rev + 1, changed.Revision);
        // Las validaciones siguen antes que la comprobación de «sin cambios».
        await Assert.ThrowsAsync<PlaylistException>(() => _repo.UpdateItemAsync(id, passage, new PlaylistItemUpdate("x", null, null, null), rev + 1, default));
        await Assert.ThrowsAsync<PlaylistException>(() => _repo.UpdateItemAsync(id, note, new PlaylistItemUpdate(null, null, null, null), rev, default));   // con revisión caducada sigue siendo conflicto
    }

    [Fact]
    public async Task A_no_op_updateItem_over_IPC_publishes_NO_playlist_changed_and_a_stale_delete_comes_back_as_conflict()
    {
        var outbox = new Outbox(NullLogger<Outbox>.Instance);
        var dispatcher = new IpcDispatcher([new PlaylistUpdateItemHandler(_repo, outbox), new PlaylistDeleteHandler(_repo, outbox)], new ListLogger<IpcDispatcher>());
        var id = (await _repo.CreateAsync("Domingo", null, null, default)).PlaylistId;
        var note = (await _repo.AddItemAsync(id, new PlaylistItemInput(PlaylistItemTypes.Note, null, null, null, null, null, null, "Ofrendas", null, false), null, default)).ItemId!.Value;
        string Msg(string type, object payload, string cid) => $$"""{"v":{{IpcCatalog.ProtocolVersion}},"id":"{{cid}}","kind":"command","type":"{{type}}","payload":{{JsonSerializer.Serialize(payload, IpcSerializer.Options)}}}""";

        Assert.Null(await dispatcher.DispatchAsync(Msg(IpcTypes.PlaylistUpdateItem, new PlaylistUpdateItemPayload(id, note, "Ofrendas", null, false, null, 2), "noop"), CancellationToken.None));
        Assert.Empty(await PublishedAsync(outbox));   // sin cambios: ni revisión nueva ni evento

        var stale = await dispatcher.DispatchAsync(Msg(IpcTypes.PlaylistDelete, new PlaylistDeletePayload(id, 1), "del"), CancellationToken.None);
        Assert.Equal(PlaylistErrorCodes.Conflict, stale!.GetPayload<CommandFailedPayload>().Error.Code);
        Assert.NotNull(await _repo.GetDetailAsync(id, null, default));
        Assert.Null(await dispatcher.DispatchAsync(Msg(IpcTypes.PlaylistDelete, new PlaylistDeletePayload(id, 2), "del2"), CancellationToken.None));
        var deleted = Assert.Single(await PublishedAsync(outbox)).GetPayload<PlaylistChangedPayload>();
        Assert.Equal(("del2", "deleted"), (deleted.CommandId, deleted.Kind));
    }

    private static async Task<List<IpcEnvelope>> PublishedAsync(Outbox outbox)
    {
        var list = new List<IpcEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try { await foreach (var (_, json) in outbox.ReadAllAsync(cts.Token)) { Assert.True(IpcSerializer.TryParse(json, out var e, out var err), err); list.Add(e!); } }
        catch (OperationCanceledException) { /* vacía */ }
        return list;
    }
}

/// <summary>El bloque playlistDisplay de settings.json: carga por bloque, como los de la Biblia y las canciones.</summary>
public sealed class PlaylistSettingsLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", "plsettings-" + Guid.NewGuid().ToString("N"));
    public PlaylistSettingsLoadTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private SettingsStore Store() => new(new ApplicationPaths(_dir, Path.Combine(_dir, "logs"), _dir), NullLogger<SettingsStore>.Instance);

    [Theory]
    [InlineData("stay", PlaylistOnItemEnd.Stay)]
    [InlineData("next", PlaylistOnItemEnd.Next)]
    [InlineData("clear", PlaylistOnItemEnd.Clear)]
    public async Task A_valid_block_is_read_back_without_warnings(string value, PlaylistOnItemEnd expected)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), $$"""{ "autoRestoreOutput": false, "playlistDisplay": { "onItemEnd": "{{value}}" } }""");
        var store = Store();
        await store.LoadAsync(CancellationToken.None);
        Assert.Equal(expected, store.Current.Playlists.OnItemEnd);
        Assert.False(store.Current.AutoRestoreOutput);
        Assert.Empty(store.Warnings);
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json.bad")));
    }

    [Fact]
    public async Task What_is_written_is_read_back_with_the_value_as_text()
    {
        await Store().UpdateAsync(s => s with { PlaylistDisplay = new PlaylistDisplaySettings(PlaylistOnItemEnd.Next) }, CancellationToken.None);
        Assert.Contains("\"next\"", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));
        var b = Store();
        await b.LoadAsync(CancellationToken.None);
        Assert.Equal(PlaylistOnItemEnd.Next, b.Current.Playlists.OnItemEnd);
    }

    [Fact]
    public async Task A_settings_file_from_before_lists_uses_stay_without_warnings()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), """{ "autoRestoreOutput": false }""");
        var store = Store();
        await store.LoadAsync(CancellationToken.None);
        Assert.Equal(PlaylistOnItemEnd.Stay, store.Current.Playlists.OnItemEnd);
        Assert.Empty(store.Warnings);
    }

    [Theory]
    [InlineData("""{ "autoRestoreOutput": false, "playlistDisplay": { "onItemEnd": "explotar" } }""")]   // valor desconocido
    [InlineData("""{ "autoRestoreOutput": false, "playlistDisplay": { "onItemEnd": 7 } }""")]            // número en vez de texto
    [InlineData("""{ "autoRestoreOutput": false, "playlistDisplay": { "onItemEnd": null } }""")]
    [InlineData("""{ "autoRestoreOutput": false, "playlistDisplay": "texto" }""")]                       // bloque ilegible
    [InlineData("""{ "autoRestoreOutput": false, "playlistDisplay": [1, 2] }""")]
    public async Task An_invalid_or_unreadable_block_goes_back_to_stay_with_a_warning_and_never_breaks_the_rest(string json)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), json);
        var store = Store();

        await store.LoadAsync(CancellationToken.None);

        Assert.Equal(PlaylistOnItemEnd.Stay, store.Current.Playlists.OnItemEnd);
        Assert.False(store.Current.AutoRestoreOutput);                                   // el resto sobrevive
        Assert.Contains(store.Warnings, w => w.Contains("playlistDisplay"));
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.bad")));               // copia de lo que había
    }
}

/// <summary>Una medición: solo se ejecuta con la variable de entorno OAK_BENCH=1.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OAK_BENCH") != "1") Skip = "Medición: define OAK_BENCH=1 para ejecutarla.";
    }
}
