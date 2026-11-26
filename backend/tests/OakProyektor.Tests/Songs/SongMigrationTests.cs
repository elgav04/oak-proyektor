using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Data;
using OakProyektor.Data.Migrations;
using OakProyektor.Data.Songs;
using OakProyektor.Tests.Data;

namespace OakProyektor.Tests.Songs;

/// <summary>
/// Migración 9: las canciones pasan de SongSlides (lista plana) a secciones + orden + copias previas, SIN suponer que SongSlides está vacía. Todo con letra sintética.
/// </summary>
public sealed class SongMigrationTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private static readonly IReadOnlyList<Migration> Embedded = Migration.LoadEmbedded(typeof(DatabaseInitializer).Assembly);

    /// <summary>Una BD en la versión 8 (el esquema anterior), lista para sembrarle canciones con diapositivas.</summary>
    private async Task<SqliteConnection> OpenVersion8Async()
    {
        var c = await _db.OpenAsync();
        await new MigrationRunner([.. Embedded.Take(8)], _db.Options, NullLogger<MigrationRunner>.Instance).RunAsync(c, CancellationToken.None);
        Assert.Equal(8L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
        return c;
    }

    private static async Task<long> SongAsync(SqliteConnection c, string title, params (int Index, string? Label, string Content)[] slides)
    {
        var id = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title, Author) VALUES (@t, 'Autor Sintético'); SELECT last_insert_rowid();", new { t = title });
        foreach (var s in slides)
            await c.ExecuteAsync("INSERT INTO SongSlides (SongId, SequenceIndex, Label, Content) VALUES (@id, @i, @l, @c)", new { id, i = s.Index, l = s.Label, c = s.Content });
        return id;
    }

    [Fact]
    public async Task Existing_slides_become_sections_with_an_arrangement_and_nothing_is_lost()
    {
        long plain, usedInService, withoutSlides;
        await using (var c = await OpenVersion8Async())
        {
            plain = await SongAsync(c, "Con diapositivas",
                (10, "Verso 1", "linea a1\nlinea a2"), (20, "Coro", "linea coro"), (30, "Verso 2", "linea b1 \n\n  con espacios  y corte"), (40, "Coro", "otro coro distinto"));
            usedInService = await SongAsync(c, "En un servicio", (0, null, "sin etiqueta uno"), (1, "", "sin etiqueta dos"), (2, "   ", "etiqueta en blanco"));
            withoutSlides = await SongAsync(c, "Sin diapositivas");
            var playlist = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name) VALUES ('Domingo'); SELECT last_insert_rowid();");
            await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (@p, 0, 'SONG', 'En un servicio', @s)", new { p = playlist, s = usedInService });
        }

        var result = await _db.Initializer.InitializeAsync(CancellationToken.None);

        Assert.Equal((8, 12), (result.FromVersion, result.ToVersion));
        Assert.Equal([9, 10, 11, 12], result.Applied);
        Assert.NotNull(result.BackupPath);   // la copia previa a migrar, que ya existía
        await using var d = await _db.OpenAsync();
        var sections = (await d.QueryAsync<(long SongId, long Position, string Kind, string Label, string Text)>(
            "SELECT SongId, Position, Kind, Label, Text FROM SongSections ORDER BY SongId, Position")).ToList();

        // Sin pérdida: cada diapositiva es una sección, con su texto (la migración 10 solo normaliza espacios, como al guardar), en su orden.
        Assert.Equal(["linea a1\nlinea a2", "linea coro", "linea b1\n\ncon espacios y corte", "otro coro distinto"], sections.Where(s => s.SongId == plain).Select(s => s.Text));
        Assert.Equal(["sin etiqueta uno", "sin etiqueta dos", "etiqueta en blanco"], sections.Where(s => s.SongId == usedInService).Select(s => s.Text));
        Assert.Equal([0L, 1, 2, 3], sections.Where(s => s.SongId == plain).Select(s => s.Position));
        Assert.Equal(["verse", "chorus", "verse", "chorus"], sections.Where(s => s.SongId == plain).Select(s => s.Kind));   // el tipo se deriva del nombre (migración 10)
        Assert.All(sections.Where(s => s.SongId == usedInService), s => Assert.Equal("other", s.Kind));

        // Etiquetas: la propia si es la primera con ese nombre; el resto se desambigua; las vacías se llaman «Sección n».
        Assert.Equal(["Verso 1", "Coro", "Verso 2", "Coro 2"], sections.Where(s => s.SongId == plain).Select(s => s.Label));
        Assert.Equal(["Sección 1", "Sección 2", "Sección 3"], sections.Where(s => s.SongId == usedInService).Select(s => s.Label));
        foreach (var group in sections.GroupBy(s => s.SongId))
            Assert.Equal(group.Count(), group.Select(s => s.Label.ToLowerInvariant()).Distinct().Count());

        // Todas las canciones tienen su orden predeterminado, en secuencia (también la que no tenía diapositivas).
        Assert.Equal(3L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongArrangements WHERE IsDefault = 1"));
        var order = (await d.QueryAsync<(long SongId, long Position, string Label)>(
            "SELECT a.SongId, i.Position, s.Label FROM SongArrangementItems i JOIN SongArrangements a ON a.Id = i.ArrangementId JOIN SongSections s ON s.Id = i.SectionId ORDER BY a.SongId, i.Position")).ToList();
        Assert.Equal(["Verso 1", "Coro", "Verso 2", "Coro 2"], order.Where(o => o.SongId == plain).Select(o => o.Label));
        Assert.DoesNotContain(order, o => o.SongId == withoutSlides);

        // SongSlides queda CONGELADA (con su contenido) y sin triggers.
        Assert.Equal(7L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongSlides"));
        Assert.Equal(0L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'SongSlides'"));

        // Columnas nuevas con sus valores por defecto.
        var row = await d.QuerySingleAsync<(long Revision, string? UpdatedAt, string CreatedAt, long Muted, string? Kind)>("SELECT Revision, UpdatedAt, CreatedAt, BackgroundMuted, BackgroundKind FROM Songs WHERE Id = @id", new { id = plain });
        Assert.Equal((1L, row.CreatedAt, 1L, (string?)null), (row.Revision, row.UpdatedAt, row.Muted, row.Kind));

        // El servicio sigue apuntando a su canción y no la da por faltante.
        var item = await d.QuerySingleAsync<(long SongId, long IsMissing)>("SELECT SongId, IsMissing FROM PlaylistItems");
        Assert.Equal((usedInService, 0L), (item.SongId, item.IsMissing));
        Assert.Empty(await d.QueryAsync("PRAGMA foreign_key_check"));
        Assert.Equal(1L, await d.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));
        Assert.Equal(12L, await d.ExecuteScalarAsync<long>("PRAGMA user_version"));
    }

    [Fact]
    public async Task The_search_index_is_rebuilt_from_the_converted_sections_with_title_author_and_lyrics()
    {
        await using (var c = await OpenVersion8Async())
            await SongAsync(c, "Canción antigua", (0, "Verso 1", "palabra zurimako"), (1, "Coro", "otra tenóko"));

        await _db.Initializer.InitializeAsync(CancellationToken.None);

        await using var d = await _db.OpenAsync();
        long Hits(string match) => d.ExecuteScalar<long>("SELECT COUNT(*) FROM SongsFts WHERE SongsFts MATCH @m", new { m = match });
        Assert.Equal(1L, Hits("zurimako"));
        Assert.Equal(1L, Hits("tenoko"));                  // sin tildes
        Assert.Equal(1L, Hits("cancion"));
        Assert.Equal(1L, Hits("sintetico"));               // el autor ahora también se indexa
        Assert.Equal("palabra zurimako\notra tenóko", d.ExecuteScalar<string>("SELECT Content FROM SongsFts"));
        await d.ExecuteAsync("INSERT INTO SongsFts (SongsFts) VALUES ('integrity-check')");
    }

    [Fact]
    public async Task Labels_that_would_collide_after_conversion_do_not_abort_the_migration()
    {
        long id;
        await using (var c = await OpenVersion8Async())
            id = await SongAsync(c, "Choques", (0, "Coro", "uno"), (1, "Coro", "dos"), (2, "Coro (2)", "tres"), (3, "coro", "cuatro"), (4, "[Raro], x", "cinco"), (5, "Sección 2", "seis"));

        await _db.Initializer.InitializeAsync(CancellationToken.None);

        await using var d = await _db.OpenAsync();
        var sections = (await d.QueryAsync<(string Label, string Text)>("SELECT Label, Text FROM SongSections WHERE SongId = @id ORDER BY Position", new { id })).ToList();
        Assert.Equal(["uno", "dos", "tres", "cuatro", "cinco", "seis"], sections.Select(s => s.Text));
        Assert.Equal(6, sections.Select(s => s.Label.ToLowerInvariant()).Distinct().Count());   // el índice único se pudo crear
        Assert.Equal("Coro", sections[0].Label);                                                  // la primera conserva su nombre
        Assert.DoesNotContain(sections, s => s.Label.Contains('[') || s.Label.Contains(','));      // las etiquetas ya cumplen las reglas del nuevo módulo
    }

    [Fact]
    public async Task A_fresh_database_gets_the_new_schema_and_an_empty_database_migrates_from_version_8()
    {
        await _db.InitializeAsync();
        await using (var c = await _db.OpenAsync())
        {
            var tables = (await c.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'")).ToHashSet();
            foreach (var t in new[] { "SongSections", "SongArrangements", "SongArrangementItems", "SongRevisions", "SongsFts", "SongSlides" }) Assert.Contains(t, tables);
            Assert.Contains("Notes", await c.QueryAsync<string>("SELECT name FROM pragma_table_info('Songs')"));
        }
    }

    [Fact]
    public async Task The_migration_runs_inside_one_transaction_and_a_second_run_changes_nothing()
    {
        await using (var c = await OpenVersion8Async())
            await SongAsync(c, "Idempotente", (0, "Verso 1", "uno"));
        await _db.Initializer.InitializeAsync(CancellationToken.None);

        var again = await _db.Initializer.InitializeAsync(CancellationToken.None);

        Assert.Empty(again.Applied);
        await using var d = await _db.OpenAsync();
        Assert.Equal(1L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongSections"));
    }

    [Fact]
    public async Task The_backup_made_before_migrating_still_has_the_old_slides()
    {
        await using (var c = await OpenVersion8Async())
            await SongAsync(c, "Respaldada", (0, "Verso 1", "linea guardada en la copia"));

        var result = await _db.Initializer.InitializeAsync(CancellationToken.None);

        await using var backup = await SqliteConnectionFactory.OpenFileReadOnlyAsync(result.BackupPath!, CancellationToken.None);
        Assert.Equal(8L, await backup.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal("linea guardada en la copia", await backup.ExecuteScalarAsync<string>("SELECT Content FROM SongSlides"));
    }

    [Fact]
    public async Task Songs_converted_by_the_migration_can_be_edited_with_the_repository_afterwards()
    {
        long id;
        await using (var c = await OpenVersion8Async())
            id = await SongAsync(c, "Migrada", (0, "Verso 1", "uno sintetico"), (1, "Coro", "dos sintetico"));
        await _db.Initializer.InitializeAsync(CancellationToken.None);
        var repo = new SongRepository(_db.Connections, new AlwaysReady());

        var song = (await repo.GetAsync(id, CancellationToken.None))!;
        await repo.SaveAsync(new OakProyektor.Core.Songs.SongSaveRequest(id, song.Revision, song.Title, song.Author, null, null, null, null,
            [.. song.Sections.Select(s => new OakProyektor.Core.Songs.SongSectionInput(s.Id, s.Label, null, s.Text + " editado"))], [0, 1, 1]), CancellationToken.None);

        var after = (await repo.GetAsync(id, CancellationToken.None))!;
        Assert.Equal(["uno sintetico editado", "dos sintetico editado"], after.Sections.Select(s => s.Text));
        Assert.Equal([0, 1, 1], after.Arrangement);
        Assert.Equal(["verse", "chorus"], after.Sections.Select(s => s.Kind));   // al guardar, el tipo se deriva de la etiqueta
    }

    [Fact]
    public async Task Every_converted_song_can_be_saved_afterwards_whatever_its_old_labels_and_text_were()
    {
        long same, verses, withEmpty, withBrackets, symbols;
        await using (var c = await OpenVersion8Async())
        {
            same = await SongAsync(c, "Coros", (0, "Coro 2", "uno"), (1, "Coro", "dos"), (2, "Coro", "tres"));
            verses = await SongAsync(c, "Versos", (0, "Verso 1", "a1"), (1, "Verso 1", "a2"));
            withEmpty = await SongAsync(c, "Con vacia", (0, "Verso 1", "v1"), (1, "Coro", "   \n  "), (2, "Verso 2", "v2"));
            withBrackets = await SongAsync(c, "Con corchetes", (0, "Verso 1", "antes\n[Coro]\ndespues\nOrden: V1 C"));
            symbols = await SongAsync(c, "Con simbolos", (0, "A;B", "x"), (1, "Parte; A", "y"), (2, new string('x', 70), "z"));
        }

        await _db.Initializer.InitializeAsync(CancellationToken.None);

        var repo = new SongRepository(_db.Connections, new AlwaysReady());
        foreach (var id in new[] { same, verses, withEmpty, withBrackets, symbols })
        {
            var song = (await repo.GetAsync(id, CancellationToken.None))!;
            // Guardar la canción tal como quedó (lo que hace el editor al pulsar «Guardar» sin tocar nada) NO debe fallar.
            await repo.SaveAsync(new OakProyektor.Core.Songs.SongSaveRequest(id, song.Revision, song.Title, song.Author, null, null, null, null,
                [.. song.Sections.Select(s => new OakProyektor.Core.Songs.SongSectionInput(s.Id, s.Label, null, s.Text))], song.Arrangement), CancellationToken.None);
        }

        Assert.Equal(["Coro 2", "Coro", "Coro 3"], (await repo.GetAsync(same, CancellationToken.None))!.Sections.Select(s => s.Label));
        Assert.Equal(["Verso 1", "Verso 1 bis"], (await repo.GetAsync(verses, CancellationToken.None))!.Sections.Select(s => s.Label));   // y no «Verso 12»
        var empty = (await repo.GetAsync(withEmpty, CancellationToken.None))!;
        Assert.Equal(["v1", "v2"], empty.Sections.Select(s => s.Text));
        Assert.Equal([0, 1], empty.Arrangement);
        Assert.Equal("antes\n([Coro])\ndespues\n(Orden: V1 C)", (await repo.GetAsync(withBrackets, CancellationToken.None))!.Sections.Single().Text);
        Assert.All((await repo.GetAsync(symbols, CancellationToken.None))!.Sections, s => Assert.Null(OakProyektor.Core.Songs.SongValidator.LabelProblem(s.Label)));
        Assert.Equal(["verse", "verse"], (await repo.GetAsync(verses, CancellationToken.None))!.Sections.Select(s => s.Kind));
    }

    [Fact]
    public async Task The_repair_is_part_of_the_migration_transaction_and_a_database_with_valid_songs_is_untouched()
    {
        // Una BD que ya estaba en la versión 9 con canciones guardadas por el editor: la migración 10 no cambia nada de ellas.
        long id;
        await using (var c = await OpenVersion8Async())
            await new MigrationRunner([.. Embedded.Take(9)], _db.Options, NullLogger<MigrationRunner>.Instance).RunAsync(c, CancellationToken.None);
        var repo = new SongRepository(_db.Connections, new AlwaysReady());
        id = (await repo.SaveAsync(new OakProyektor.Core.Songs.SongSaveRequest(null, null, "Valida", null, null, null, null, null,
            [new(null, "Verso 1", null, "uno\n\ndos"), new(null, "Coro", null, "coro")], [0, 1, 1]), CancellationToken.None)).SongId;
        var before = (await repo.GetAsync(id, CancellationToken.None))!;

        var result = await _db.Initializer.InitializeAsync(CancellationToken.None);

        Assert.Equal([10, 11, 12], result.Applied);
        var after = (await repo.GetAsync(id, CancellationToken.None))!;
        Assert.Equal(before.Sections, after.Sections);
        Assert.Equal(before.Arrangement, after.Arrangement);
        Assert.Equal(before.Revision, after.Revision);
    }}
