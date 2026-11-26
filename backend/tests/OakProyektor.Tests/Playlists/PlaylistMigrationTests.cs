using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Data;
using OakProyektor.Data.Migrations;
using OakProyektor.Tests.Data;

namespace OakProyektor.Tests.Playlists;

/// <summary>
/// Migración 11: las listas pasan a ser editables (nota, revisión, fechas válidas). La tabla PlaylistItems se RECONSTRUYE: aquí se comprueba, sobre una base en la versión 10 con
/// listas y elementos de todos los tipos (incluidos faltantes y un elemento repetido), que NO se pierde ni se altera ninguno. Todo con datos sintéticos.
/// </summary>
public sealed class PlaylistMigrationTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private static readonly IReadOnlyList<Migration> Embedded = Migration.LoadEmbedded(typeof(DatabaseInitializer).Assembly);

    private async Task<SqliteConnection> OpenVersion10Async()
    {
        var c = await _db.OpenAsync();
        await new MigrationRunner([.. Embedded.Take(10)], _db.Options, NullLogger<MigrationRunner>.Instance).RunAsync(c, CancellationToken.None);
        Assert.Equal(10L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
        return c;
    }

    private sealed record Seeded(long SundayId, long EmptyId, long BadDateId, long BlankDateId, long SongKept, long SongDeleted, long BibleItemId, long[] ItemIds);

    /// <summary>Un domingo con 6 elementos (canción, la MISMA canción otra vez, canción que se borrará, pasaje con Biblia instalada, pasaje con Biblia que no existe, presentación) y listas con fechas raras.</summary>
    private static async Task<Seeded> SeedAsync(SqliteConnection c)
    {
        async Task<long> Insert(string sql, object? p = null) => await c.ExecuteScalarAsync<long>(sql + "; SELECT last_insert_rowid();", p);
        var kept = await Insert("INSERT INTO Songs (Title) VALUES ('Canción sintética A')");
        var deleted = await Insert("INSERT INTO Songs (Title) VALUES ('Canción sintética B')");
        await c.ExecuteAsync("INSERT INTO Bibles (Abbreviation, Name, Language) VALUES ('TST', 'Biblia sintética', 'es')");
        var presentation = await Insert("INSERT INTO Presentations (Title, SourceType) VALUES ('Presentación sintética', 'CUSTOM')");

        var sunday = await Insert("INSERT INTO Playlists (Name, ServiceDate) VALUES ('Domingo sintético', '2026-11-22')");
        var empty = await Insert("INSERT INTO Playlists (Name, ServiceDate) VALUES ('Vacía', NULL)");
        var bad = await Insert("INSERT INTO Playlists (Name, ServiceDate) VALUES ('Fecha rara', 'el domingo que viene')");
        var blank = await Insert("INSERT INTO Playlists (Name, ServiceDate) VALUES ('Fecha vacía', '')");

        const string item = "INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId, PresentationId, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation) VALUES (@p, @i, @t, @title, @s, @pr, @b, @c, @v, @e, @a)";
        var ids = new List<long>
        {
            await Insert(item, new { p = sunday, i = 0, t = "SONG", title = "Canción sintética A", s = (long?)kept, pr = (long?)null, b = (int?)null, c = (int?)null, v = (int?)null, e = (int?)null, a = (string?)null }),
            await Insert(item, new { p = sunday, i = 1, t = "SONG", title = "Canción sintética A", s = (long?)kept, pr = (long?)null, b = (int?)null, c = (int?)null, v = (int?)null, e = (int?)null, a = (string?)null }),   // la misma canción dos veces
            await Insert(item, new { p = sunday, i = 2, t = "SONG", title = "Canción sintética B", s = (long?)deleted, pr = (long?)null, b = (int?)null, c = (int?)null, v = (int?)null, e = (int?)null, a = (string?)null }),
            await Insert(item, new { p = sunday, i = 3, t = "PASSAGE", title = "Libro 43 3:16-17", s = (long?)null, pr = (long?)null, b = (int?)43, c = (int?)3, v = (int?)16, e = (int?)17, a = "TST" }),
            await Insert(item, new { p = sunday, i = 4, t = "PASSAGE", title = "Libro 19 23:1", s = (long?)null, pr = (long?)null, b = (int?)19, c = (int?)23, v = (int?)1, e = (int?)null, a = "NOEXISTE" }),
            await Insert(item, new { p = sunday, i = 5, t = "PRESENTATION", title = "Presentación sintética", s = (long?)null, pr = (long?)presentation, b = (int?)null, c = (int?)null, v = (int?)null, e = (int?)null, a = (string?)null }),
        };
        await c.ExecuteAsync("DELETE FROM Songs WHERE Id = @id", new { id = deleted });   // ON DELETE SET NULL: ese elemento queda «faltante» ANTES de migrar
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT IsMissing FROM PlaylistItems WHERE Id = @id", new { id = ids[2] }));
        return new Seeded(sunday, empty, bad, blank, kept, deleted, ids[3], [.. ids]);
    }

    private static async Task<List<string>> ItemSnapshotAsync(SqliteConnection c) =>
        [.. (await c.QueryAsync("SELECT Id, PlaylistId, SequenceIndex, ItemType, Title, SongId, PresentationId, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation, IsMissing FROM PlaylistItems ORDER BY Id"))
            .Select(r => string.Join("|", ((IDictionary<string, object>)r).Select(kv => $"{kv.Key}={kv.Value}")))];

    [Fact]
    public async Task No_element_is_lost_or_changed_when_the_items_table_is_rebuilt()
    {
        List<string> before;
        Seeded seeded;
        await using (var c = await OpenVersion10Async())
        {
            seeded = await SeedAsync(c);
            before = await ItemSnapshotAsync(c);
        }
        Assert.Equal(6, before.Count);

        var result = await _db.Initializer.InitializeAsync(CancellationToken.None);

        Assert.Equal((10, 12), (result.FromVersion, result.ToVersion));
        Assert.Equal([11, 12], result.Applied);
        Assert.NotNull(result.BackupPath);            // copia previa a migrar
        await using var d = await _db.OpenAsync();
        Assert.Equal(before, await ItemSnapshotAsync(d));      // los 6, idénticos: mismos ids, orden, tipos, títulos, referencias y el flag de faltante

        // Los faltantes siguen faltantes; el pasaje de una Biblia que no existe, el repetido y la presentación siguen donde estaban.
        var rows = (await d.QueryAsync<(long Id, string ItemType, long IsMissing, long? SongId)>("SELECT Id, ItemType, IsMissing, SongId FROM PlaylistItems ORDER BY SequenceIndex")).ToList();
        Assert.Equal([0L, 0L, 1L, 0L, 0L, 0L], rows.Select(r => r.IsMissing));
        Assert.Equal([seeded.SongKept, seeded.SongKept], rows.Take(2).Select(r => r.SongId!.Value));
        Assert.Null(rows[2].SongId);
        Assert.Equal("NOEXISTE", await d.ExecuteScalarAsync<string>("SELECT BibleAbbreviation FROM PlaylistItems WHERE SequenceIndex = 4"));

        // Columnas nuevas con sus valores por defecto.
        Assert.Equal(0L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM PlaylistItems WHERE Note IS NOT NULL OR ClearOnActivate <> 0"));

        Assert.Empty(await d.QueryAsync("PRAGMA foreign_key_check"));
        Assert.Equal(1L, await d.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));
        Assert.Equal(12L, await d.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal("ok", await d.ExecuteScalarAsync<string>("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task Lists_gain_a_revision_timestamps_and_notes_and_an_invalid_date_becomes_a_template_keeping_the_original_text()
    {
        Seeded seeded;
        await using (var c = await OpenVersion10Async()) seeded = await SeedAsync(c);

        await _db.Initializer.InitializeAsync(CancellationToken.None);

        await using var d = await _db.OpenAsync();
        var lists = (await d.QueryAsync<(long Id, string Name, string? ServiceDate, string Notes, long Revision, string? CreatedAt, string? UpdatedAt)>(
            "SELECT Id, Name, ServiceDate, Notes, Revision, CreatedAt, UpdatedAt FROM Playlists ORDER BY Id")).ToDictionary(l => l.Id);
        Assert.Equal(4, lists.Count);                                                  // ninguna lista se pierde
        Assert.All(lists.Values, l => Assert.Equal(1L, l.Revision));
        Assert.All(lists.Values, l => { Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", l.CreatedAt!); Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", l.UpdatedAt!); });

        Assert.Equal(("2026-11-22", ""), (lists[seeded.SundayId].ServiceDate, lists[seeded.SundayId].Notes));        // una fecha válida no se toca
        Assert.Equal((null, ""), (lists[seeded.EmptyId].ServiceDate, lists[seeded.EmptyId].Notes));
        Assert.Equal((null, "Fecha original: el domingo que viene"), (lists[seeded.BadDateId].ServiceDate, lists[seeded.BadDateId].Notes));   // plantilla, con el texto a salvo
        Assert.Equal((null, ""), (lists[seeded.BlankDateId].ServiceDate, lists[seeded.BlankDateId].Notes));
    }

    [Fact]
    public async Task The_new_schema_accepts_notes_and_keeps_every_guarantee_of_the_previous_one()
    {
        long sunday;
        await using (var c = await OpenVersion10Async()) sunday = (await SeedAsync(c)).SundayId;
        await _db.Initializer.InitializeAsync(CancellationToken.None);
        await using var d = await _db.OpenAsync();

        // Una nota: sin referencias, con título; ClearOnActivate apagado por defecto.
        await d.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, Note) VALUES (@p, 6, 'NOTE', 'Ofrendas', 'Pasan los diáconos')", new { p = sunday });
        var note = await d.QuerySingleAsync<(string ItemType, long IsMissing, long Clear)>("SELECT ItemType, IsMissing, ClearOnActivate FROM PlaylistItems WHERE ItemType = 'NOTE'");
        Assert.Equal(("NOTE", 0L, 0L), note);                      // una nota nunca es «faltante»

        // Formas inválidas siguen rechazadas por la base.
        async Task Rejects(string sql, object? p = null) => await Assert.ThrowsAsync<SqliteException>(async () => await d.ExecuteAsync(sql, p));
        await Rejects("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title) VALUES (@p, 7, 'NOTE', '   ')", new { p = sunday });                                  // nota sin título
        await Rejects("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (@p, 7, 'NOTE', 'x', 1)", new { p = sunday });                        // nota con canción
        await Rejects("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, Note) VALUES (@p, 7, 'SONG', 'x', 'nota')", new { p = sunday });                       // nota en una canción
        await Rejects("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title) VALUES (@p, 7, 'SONG', 'sin referencia')", new { p = sunday });                       // trigger: nace «faltante»
        await Rejects("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title) VALUES (@p, 7, 'COSA', 'x')", new { p = sunday });                                    // tipo desconocido
        await Rejects("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES (@p, 7, 'PASSAGE', 'x', 67, 1, 1, 'TST')", new { p = sunday });
        await Rejects("UPDATE PlaylistItems SET SongId = NULL WHERE SequenceIndex = 0");                                                                                              // desvincular a mano
        await Rejects("UPDATE PlaylistItems SET ClearOnActivate = 2 WHERE ItemType = 'NOTE'");

        // La fecha de una lista se valida al insertar y al actualizar.
        await Rejects("INSERT INTO Playlists (Name, ServiceDate) VALUES ('mala', '2026-02-30')");
        await Rejects("UPDATE Playlists SET ServiceDate = 'mañana' WHERE Id = @p", new { p = sunday });
        await d.ExecuteAsync("INSERT INTO Playlists (Name, ServiceDate) VALUES ('buena', '2026-12-06'), ('plantilla', NULL)");
        await d.ExecuteAsync("UPDATE Playlists SET ServiceDate = NULL WHERE Id = @p", new { p = sunday });

        // Los índices y los triggers de la tabla reconstruida existen.
        var objects = (await d.QueryAsync<string>("SELECT name FROM sqlite_master WHERE tbl_name IN ('PlaylistItems', 'Playlists') AND type IN ('index', 'trigger')")).ToHashSet();
        foreach (var expected in new[] { "IX_PlaylistItems_PlaylistId_SequenceIndex", "IX_PlaylistItems_SongId", "IX_PlaylistItems_PresentationId", "PlaylistItems_bi", "PlaylistItems_bu", "Playlists_date_bi", "Playlists_date_bu" })
            Assert.Contains(expected, objects);

        // Borrar una lista se lo lleva todo en cascada; borrar una canción que sigue en una lista deja el elemento «faltante» con su título.
        await d.ExecuteAsync("DELETE FROM Songs WHERE Id = (SELECT SongId FROM PlaylistItems WHERE SequenceIndex = 0)");
        Assert.Equal((1L, "Canción sintética A"), await d.QuerySingleAsync<(long, string)>("SELECT IsMissing, Title FROM PlaylistItems WHERE SequenceIndex = 0 AND PlaylistId = @p", new { p = sunday }));
        await d.ExecuteAsync("DELETE FROM Playlists WHERE Id = @p", new { p = sunday });
        Assert.Equal(0L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = @p", new { p = sunday }));
    }

    [Fact]
    public async Task A_database_with_no_lists_migrates_cleanly_and_the_migration_is_not_repeated()
    {
        await using (var c = await OpenVersion10Async()) { /* base vacía en v10 */ }
        var first = await _db.Initializer.InitializeAsync(CancellationToken.None);
        var second = await _db.Initializer.InitializeAsync(CancellationToken.None);

        Assert.Equal([11, 12], first.Applied);
        Assert.Empty(second.Applied);
        await using var d = await _db.OpenAsync();
        Assert.Equal(0L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM PlaylistItems"));
        Assert.Empty(await d.QueryAsync("PRAGMA foreign_key_check"));
    }
}
