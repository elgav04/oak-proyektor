using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using OakProyektor.Core.Scripture;
using OakProyektor.Data.Scripture;

namespace OakProyektor.Tests.Scripture;

// Protecciones de las reimportaciones (hallazgos 1, 2, 3, 4, 9, 10, 11, 13 de docs/REVISION_IMPORTADOR.md y la copia previa). Datos SINTÉTICOS.
public sealed class BibleImporterProtectionTests : IAsyncLifetime
{
    private BibleEnv _env = null!;

    public async Task InitializeAsync() => _env = await BibleEnv.CreateAsync();
    public Task DisposeAsync() { _env.Dispose(); return Task.CompletedTask; }

    private async Task<long> CountAsync(string sql, object? args = null)
    {
        await using var c = await _env.OpenAsync();
        return await c.ExecuteScalarAsync<long>(sql, args);
    }

    private async Task<string?> TextAsync(int book, int chapter, int verse, string bible = "SIN")
    {
        await using var c = await _env.OpenAsync();
        return await c.ExecuteScalarAsync<string?>(
            "SELECT v.Text FROM Verses v JOIN Books b ON b.Id = v.BookId JOIN Bibles bi ON bi.Id = b.BibleId WHERE bi.Abbreviation = @bible AND b.BookNumber = @book AND v.Chapter = @chapter AND v.VerseNumber = @verse",
            new { bible, book, chapter, verse });
    }

    // Génesis 1 (150 versículos), Génesis 2 (3) y Judas 1 (3): 156 versículos, así que el tope del 1 % es el mínimo de 10.
    private static List<SyntheticBible.V> Base()
    {
        var list = new List<SyntheticBible.V>();
        list.AddRange(SyntheticBible.Chapter(1, 1, 150));
        list.AddRange(SyntheticBible.Chapter(1, 2, 3));
        list.AddRange(SyntheticBible.Chapter(65, 1, 3));
        return list;
    }

    private static List<SyntheticBible.V> Edit(List<SyntheticBible.V> verses, Func<SyntheticBible.V, bool> which, string suffix = " corregido") =>
        [.. verses.Select(v => which(v) ? v with { Text = v.Text + suffix } : v)];

    private IEnumerable<string> BackupFiles() =>
        Directory.Exists(_env.BackupsDirectory) ? Directory.EnumerateFiles(_env.BackupsDirectory, BibleImporter.BackupPrefix + "*.db") : [];

    // ───────────────────────────── H1: sobrescritura silenciosa ─────────────────────────────

    [Fact]
    public async Task An_identical_reimport_changes_nothing_reports_zero_and_makes_no_backup()
    {
        var first = await _env.ImportAsync(Base());
        var again = await _env.ImportAsync(Base());

        Assert.Equal((0, 0, 156, 0), (again.Inserted, again.Updated, again.Unchanged, again.Deleted));
        Assert.Equal(156, again.PreviousVerses);
        Assert.Null(again.BackupPath);
        Assert.Empty(BackupFiles());
        Assert.Equal(first.BibleId, again.BibleId);
    }

    [Fact]
    public async Task A_small_correction_passes_without_any_flag_and_the_report_counts_it()
    {
        await _env.ImportAsync(Base());

        var report = await _env.ImportAsync(Edit(Base(), v => v is { Book: 1, Chapter: 1, Verse: <= 3 }));   // 3 versículos corregidos

        Assert.Equal((0, 3, 153, 0), (report.Inserted, report.Updated, report.Unchanged, report.Deleted));
        Assert.EndsWith(" corregido", (await TextAsync(1, 1, 2))!);
        Assert.True(report.FtsIntegrityOk);
    }

    [Fact]
    public async Task Another_translation_with_the_same_abbreviation_is_refused_and_nothing_is_touched()
    {
        await _env.ImportAsync(Base());
        var other = Base().Select(v => v with { Text = $"Otra traduccion inventada {v.Chapter}x{v.Verse}" }).ToList();

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(other));

        Assert.Equal(BibleErrorCodes.TooManyChanges, ex.Code);
        Assert.Contains("156", ex.Message);
        Assert.Contains("sobrescribiría", ex.Message);
        Assert.Equal(SyntheticBible.TextOf(1, 1, 1), await TextAsync(1, 1, 1));              // intacto
        Assert.Equal(156, await CountAsync("SELECT COUNT(*) FROM Verses"));
        Assert.Empty(BackupFiles());                                                           // un rechazo no hace copia: no hay nada que proteger
    }

    [Fact]
    public async Task The_same_other_translation_goes_through_with_the_explicit_flag_and_a_backup_is_made_first()
    {
        await _env.ImportAsync(Base());
        var other = Base().Select(v => v with { Text = $"Otra traduccion inventada {v.Chapter}x{v.Verse}" }).ToList();

        var report = await _env.ImportAsync(other, allowLargeChange: true);

        Assert.Equal(156, report.Updated);
        Assert.Equal("Otra traduccion inventada 1x1", await TextAsync(1, 1, 1));
        Assert.NotNull(report.BackupPath);
        Assert.True(File.Exists(report.BackupPath));
        Assert.Contains(report.Warnings, w => w.Contains("Copia de seguridad previa"));
    }

    [Fact]
    public async Task The_threshold_is_one_percent_with_a_minimum_of_ten_texts()
    {
        var verses = new List<SyntheticBible.V>();
        for (var chapter = 1; chapter <= 3; chapter++) verses.AddRange(SyntheticBible.Chapter(1, chapter, 100));   // 300 versículos: 1 % = 3, el mínimo manda (10)
        await _env.ImportAsync(verses);

        var ten = Edit(verses, v => v.Chapter == 1 && v.Verse <= 10, " diez");
        var report = await _env.ImportAsync(ten);
        Assert.Equal(10, report.Updated);                                                      // justo en el tope: pasa

        var eleven = Edit(ten, v => v.Chapter == 2 && v.Verse <= 11, " once");
        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(eleven));     // uno más: se rechaza
        Assert.Equal(BibleErrorCodes.TooManyChanges, ex.Code);
        Assert.Equal(SyntheticBible.TextOf(1, 2, 1), await TextAsync(1, 2, 1));
    }

    [Fact]
    public async Task Replacing_every_text_of_a_tiny_bible_is_refused_too_the_minimum_of_ten_never_exceeds_half()
    {
        var tiny = SyntheticBible.Chapter(1, 1, 8).ToList();
        await _env.ImportAsync(tiny);

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(tiny.Select(v => v with { Text = "Otra traduccion " + v.Verse }).ToList()));
        Assert.Equal(BibleErrorCodes.TooManyChanges, ex.Code);

        var half = await _env.ImportAsync(Edit(tiny, v => v.Verse <= 4));                      // la mitad (4 de 8) sí es una corrección
        Assert.Equal(4, half.Updated);
        var more = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(Edit(tiny, v => v.Verse <= 5, " otra")));   // 5 de 8 ya pasa de la mitad
        Assert.Equal(BibleErrorCodes.TooManyChanges, more.Code);
    }

    [Fact]
    public async Task Turning_a_text_that_has_content_into_an_empty_one_is_refused_even_if_it_is_only_one_verse()
    {
        await _env.ImportAsync(Base());
        var blanked = Base().Select(v => v is { Book: 1, Chapter: 1, Verse: 5 } ? v with { Text = "   " } : v).ToList();

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(blanked));

        Assert.Equal(BibleErrorCodes.TooManyChanges, ex.Code);
        Assert.Contains("VACÍOS", ex.Message);
        Assert.Equal(SyntheticBible.TextOf(1, 1, 5), await TextAsync(1, 1, 5));
        // con el indicador explícito sí se permite
        var report = await _env.ImportAsync(blanked, allowLargeChange: true);
        Assert.Equal(1, report.Updated);
        Assert.Equal("", await TextAsync(1, 1, 5));
    }

    [Fact]
    public async Task Changing_the_name_of_an_installed_bible_is_refused_unless_explicitly_allowed()
    {
        await _env.ImportAsync(Base(), name: "Nombre original");

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(Base(), name: "Otro nombre"));

        Assert.Equal(BibleErrorCodes.TooManyChanges, ex.Code);
        Assert.Contains("nombre", ex.Message);
        Assert.Equal("Nombre original", await _env.Query.ListBiblesAsync(CancellationToken.None).ContinueWith(t => t.Result.Single().Name));
        Assert.Equal("Nombre original", (await _env.ImportAsync(Base(), name: "  Nombre original  ")).CreatedBible ? "" : "Nombre original");   // recortar espacios no es cambiar el nombre

        await _env.ImportAsync(Base(), name: "Otro nombre", allowLargeChange: true);
        Assert.Equal("Otro nombre", (await _env.Query.ListBiblesAsync(CancellationToken.None)).Single().Name);
    }

    [Fact]
    public async Task A_first_import_never_triggers_the_change_protections_even_with_a_new_name()
    {
        var report = await _env.ImportAsync(Base(), name: "Cualquier nombre");
        Assert.True(report.CreatedBible);
        Assert.Equal(0, report.PreviousVerses);
        Assert.Null(report.BackupPath);
    }

    // ───────────────────────────── H2: libros y capítulos que desaparecen ─────────────────────────────

    [Fact]
    public async Task A_whole_book_disappearing_is_refused_even_when_it_is_only_three_verses()
    {
        await _env.ImportAsync(Base());
        var withoutJudas = Base().Where(v => v.Book != 65).ToList();                         // 3 versículos < el tope de 10

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(withoutJudas));

        Assert.Equal(BibleErrorCodes.BooksWouldDisappear, ex.Code);
        Assert.Contains("JUD", ex.Message);                                                    // dice QUÉ libro
        Assert.Equal(156, await CountAsync("SELECT COUNT(*) FROM Verses"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Books WHERE BookNumber = 65"));
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task A_whole_chapter_disappearing_is_refused_and_names_the_chapter()
    {
        await _env.ImportAsync(Base());
        var withoutChapter2 = Base().Where(v => !(v.Book == 1 && v.Chapter == 2)).ToList();   // 3 versículos del capítulo 2

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(withoutChapter2));

        Assert.Equal(BibleErrorCodes.BooksWouldDisappear, ex.Code);
        Assert.Contains("GEN 2", ex.Message);
        Assert.Equal(156, await CountAsync("SELECT COUNT(*) FROM Verses"));
    }

    [Fact]
    public async Task The_existing_deletion_flag_allows_losing_a_book_or_chapter_and_the_report_says_so()
    {
        await _env.ImportAsync(Base());

        var report = await _env.ImportAsync(Base().Where(v => v.Book != 65 && !(v.Book == 1 && v.Chapter == 2)).ToList(), allowLargeDeletion: true);

        Assert.Equal(6, report.Deleted);
        Assert.Equal(1, report.BooksDeleted);
        Assert.NotNull(report.BackupPath);                                                     // borrar algo hace copia previa
        Assert.Equal(150, await CountAsync("SELECT COUNT(*) FROM Verses"));
    }

    [Fact]
    public async Task Losing_a_few_verses_inside_a_chapter_that_still_exists_keeps_passing_without_flags()
    {
        await _env.ImportAsync(Base());

        var report = await _env.ImportAsync(Base().Where(v => !(v.Book == 1 && v.Chapter == 1 && v.Verse > 145)).ToList());   // 5 versículos menos del capítulo 1

        Assert.Equal(5, report.Deleted);
        Assert.Equal(151, await CountAsync("SELECT COUNT(*) FROM Verses"));
    }

    // ───────────────────────────── H3: perfil de numeración ─────────────────────────────

    [Fact]
    public async Task Without_a_numbering_option_the_stored_profile_is_kept_and_the_report_says_so()
    {
        var small = SyntheticBible.Small();
        var merged = await _env.ImportAsync(small, profile: "merge-ps47-3jn");
        Assert.False(merged.NumberingProfileKept);                                             // se indicó

        var again = await _env.ImportAsync(small);                                             // sin --numbering

        Assert.Equal("merge-ps47-3jn", again.NumberingProfile);                                // el que se aplicó
        Assert.True(again.NumberingProfileKept);
        Assert.Equal((0, 0, small.Count - 2, 0), (again.Inserted, again.Updated, again.Unchanged, again.Deleted));
        Assert.Equal(["PSA 47:9-10", "3JN 1:14-15"], again.MergesApplied);
        Assert.Contains(again.Warnings, w => w.Contains("No se indicó perfil") && w.Contains("merge-ps47-3jn"));
        Assert.Equal("merge-ps47-3jn", await _env.Query.ListBiblesAsync(CancellationToken.None).ContinueWith(t => t.Result.Single().NumberingProfile));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BookNumber = 19 AND v.Chapter = 47 AND v.VerseNumber = 10"));
    }

    [Fact]
    public async Task Asking_for_none_explicitly_does_change_the_profile()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small, profile: "merge-ps47-3jn");

        var back = await _env.ImportAsync(small, profile: "none");

        Assert.Equal("none", back.NumberingProfile);
        Assert.False(back.NumberingProfileKept);
        Assert.Equal(2, back.Inserted);                                                         // 47:10 y 3 Jn 15 vuelven
        Assert.Null((await _env.Query.ListBiblesAsync(CancellationToken.None)).Single().NumberingProfile);
    }

    [Fact]
    public async Task A_new_bible_without_a_profile_gets_none_and_a_stored_none_is_kept()
    {
        var first = await _env.ImportAsync(SyntheticBible.Small());
        Assert.Equal("none", first.NumberingProfile);
        Assert.False(first.NumberingProfileKept);                                              // nueva: no hay nada que conservar

        var again = await _env.ImportAsync(SyntheticBible.Small());
        Assert.Equal("none", again.NumberingProfile);
        Assert.True(again.NumberingProfileKept);
    }

    [Fact]
    public async Task A_stored_profile_that_no_longer_exists_in_code_falls_back_to_none_with_a_warning()
    {
        await _env.ImportAsync(SyntheticBible.Small());
        await using (var c = await _env.OpenAsync()) await c.ExecuteAsync("UPDATE Bibles SET NumberingProfile = 'perfil-viejo'");

        var report = await _env.ImportAsync(SyntheticBible.Small());

        Assert.Equal("none", report.NumberingProfile);
        Assert.Contains(report.Warnings, w => w.Contains("perfil-viejo") && w.Contains("ya no existe"));
    }

    // ───────────────────────────── H4: duplicados ─────────────────────────────

    private sealed record Ch(string Usfm, params (int Verse, string Text)[] Verses);
    private sealed record Bk(string Usfm, params Ch[] Chapters);

    private string WriteFile(string name, params Bk[] books)
    {
        Directory.CreateDirectory(_env.FilesDirectory);
        var path = Path.Combine(_env.FilesDirectory, name);
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream);
        w.WriteStartObject();
        w.WriteStartArray("books");
        foreach (var book in books)
        {
            w.WriteStartObject();
            w.WriteString("usfm", book.Usfm);
            w.WriteStartArray("chapters");
            foreach (var chapter in book.Chapters)
            {
                w.WriteStartObject();
                w.WriteString("usfm", chapter.Usfm);
                w.WriteStartArray("items");
                foreach (var (verse, text) in chapter.Verses)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "verse");
                    w.WriteStartArray("verse_numbers"); w.WriteNumberValue(verse); w.WriteEndArray();
                    w.WriteStartArray("lines"); w.WriteStringValue(text); w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        return path;
    }

    [Fact]
    public async Task A_book_repeated_in_the_file_keeps_the_first_chapters_does_not_double_the_text_and_warns_when_the_repeat_differs()
    {
        var path = WriteFile("dup.json",
            new Bk("GEN", new Ch("GEN.1", (1, "Texto uno"), (2, "Texto dos")), new Ch("GEN.2", (1, "Texto tres"))),
            new Bk("GEN", new Ch("GEN.1", (1, "OTRO texto uno"), (2, "OTRO texto dos")), new Ch("GEN.2", (1, "Texto tres"))));   // GEN.1 distinto, GEN.2 idéntico

        var report = await _env.ImportFileAsync(path);

        Assert.Equal(2, report.DuplicateChaptersDiscarded);
        Assert.Equal(1, report.DuplicateChaptersDifferent);
        Assert.Equal(0, report.JoinedVerses);                                                  // no se «unieron» los repetidos
        Assert.Equal("Texto uno", await TextAsync(1, 1, 1));                                   // la primera aparición, NO «Texto uno OTRO texto uno»
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM Verses"));
        var warning = Assert.Single(report.Warnings, w => w.Contains("DISTINTO"));
        Assert.Contains("GEN.1", warning);
        Assert.DoesNotContain("GEN.2", warning);
        Assert.DoesNotContain("OTRO", string.Join(' ', report.Warnings));                      // el aviso lleva referencias, nunca texto
    }

    [Fact]
    public async Task Identical_repeated_chapters_are_counted_without_a_scary_warning()
    {
        var chapter = new Ch("GEN.1", (1, "Texto uno"), (2, "Texto dos"));
        var report = await _env.ImportFileAsync(WriteFile("dup-iguales.json", new Bk("GEN", chapter, chapter), new Bk("GEN", chapter)));

        Assert.Equal(2, report.DuplicateChaptersDiscarded);
        Assert.Equal(0, report.DuplicateChaptersDifferent);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("DISTINTO"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM Verses"));
    }

    [Fact]
    public async Task The_order_of_the_verses_inside_a_repeated_chapter_does_not_make_it_look_different()
    {
        var report = await _env.ImportFileAsync(WriteFile("dup-orden.json",
            new Bk("GEN", new Ch("GEN.1", (1, "uno"), (2, "dos")), new Ch("GEN.1", (2, "dos"), (1, "uno")))));

        Assert.Equal(1, report.DuplicateChaptersDiscarded);
        Assert.Equal(0, report.DuplicateChaptersDifferent);
    }

    // ───────────────────────────── H9: codificación ─────────────────────────────

    private string WriteBytes(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_env.FilesDirectory);
        var path = Path.Combine(_env.FilesDirectory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private const string AccentedJson = """{"books":[{"usfm":"GEN","chapters":[{"usfm":"GEN.1","items":[{"type":"verse","verse_numbers":[1],"lines":["Texto sintético con acentos: áéíóú ñ"]}]}]}]}""";

    [Fact]
    public async Task A_windows_1252_file_gives_a_clear_message_and_writes_nothing()
    {
        var path = WriteBytes("latin1.json", Encoding.Latin1.GetBytes(AccentedJson));         // «é», «ñ»… como un solo byte: no es UTF-8 válido

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportFileAsync(path));

        Assert.Equal(BibleErrorCodes.SourceInvalid, ex.Code);
        Assert.Contains("no está en UTF-8", ex.Message);
        Assert.Contains("Windows-1252", ex.Message);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Bibles"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Verses"));
    }

    [Fact]
    public async Task A_utf16_file_gets_the_same_clear_message()
    {
        var path = WriteBytes("utf16.json", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(AccentedJson)]);

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportFileAsync(path));

        Assert.Equal(BibleErrorCodes.SourceInvalid, ex.Code);
        Assert.Contains("UTF-8", ex.Message);
    }

    [Fact]
    public async Task A_file_cut_in_the_middle_of_a_multibyte_character_is_reported_as_an_encoding_problem()
    {
        var bytes = new UTF8Encoding(false).GetBytes(AccentedJson);
        var cut = Array.IndexOf(bytes, (byte)0xC3, 100) + 1;                                   // justo tras el primer byte de «á»
        var path = WriteBytes("cortado.json", bytes[..cut]);

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportFileAsync(path));

        Assert.Equal(BibleErrorCodes.SourceInvalid, ex.Code);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Bibles"));
    }

    [Fact]
    public async Task Valid_utf8_with_and_without_a_byte_order_mark_still_imports()
    {
        var plain = WriteBytes("utf8.json", new UTF8Encoding(false).GetBytes(AccentedJson));
        var bom = WriteBytes("utf8-bom.json", [.. new UTF8Encoding(true).GetPreamble(), .. new UTF8Encoding(false).GetBytes(AccentedJson)]);

        var a = await _env.ImportFileAsync(plain, "AAA");
        var b = await _env.ImportFileAsync(bom, "BBB");

        Assert.Equal(1, a.Inserted);
        Assert.Equal(1, b.Inserted);
        Assert.Equal("Texto sintético con acentos: áéíóú ñ", await TextAsync(1, 1, 1, "BBB"));
    }

    [Fact]
    public async Task A_bad_encoding_never_reaches_the_database_when_the_bible_already_exists()
    {
        await _env.ImportAsync(Base());
        var before = await CountAsync("SELECT COUNT(*) FROM Verses");
        var path = WriteBytes("latin1.json", Encoding.Latin1.GetBytes(AccentedJson));

        await Assert.ThrowsAsync<BibleException>(() => _env.ImportFileAsync(path));

        Assert.Equal(before, await CountAsync("SELECT COUNT(*) FROM Verses"));
        Assert.Empty(BackupFiles());
    }

    // ───────────────────────────── H10: canon ─────────────────────────────

    [Fact]
    public async Task Verses_the_reference_parser_could_never_query_are_discarded_with_a_count_and_references()
    {
        var verses = new List<SyntheticBible.V>(SyntheticBible.Chapter(1, 1, 5))
        {
            SyntheticBible.Verse(1, 51, 1),       // Génesis tiene 50 capítulos
            SyntheticBible.Verse(1, 99, 3),
            SyntheticBible.Verse(19, 119, 176),   // el último versículo posible: se conserva
            SyntheticBible.Verse(19, 119, 177),   // ningún capítulo pasa de 176
            SyntheticBible.Verse(19, 1, 500),
            SyntheticBible.Verse(1, 1, 0),        // versículo cero
            SyntheticBible.Verse(1, 1, -4),       // negativo
        };

        var report = await _env.ImportAsync(verses);

        Assert.Equal(6, report.DiscardedVerses);                                                  // 4 fuera del canon + 2 con numeración inválida
        Assert.Equal(6, report.SourceVerses);                                                  // 5 de Génesis 1 + el 119:176
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BookNumber = 19"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Verses WHERE Chapter > 50 AND BookId IN (SELECT Id FROM Books WHERE BookNumber = 1)"));
        var outOfCanon = Assert.Single(report.Warnings, w => w.Contains("fuera del canon"));
        Assert.StartsWith("4 versículo(s)", outOfCanon);
        Assert.Contains("GEN 51:1", outOfCanon);
        Assert.Contains("PSA 119:177", outOfCanon);
        var invalid = Assert.Single(report.Warnings, w => w.Contains("numeración inválida"));
        Assert.StartsWith("2 versículo(s)", invalid);
        Assert.DoesNotContain("Sintetico", string.Join(' ', report.Warnings));                  // referencias, nunca texto
    }

    [Fact]
    public async Task A_file_inside_the_canon_reports_zero_discarded()
    {
        var report = await _env.ImportAsync(SyntheticBible.Small());
        Assert.Equal(0, report.DiscardedVerses);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("fuera del canon"));
    }

    // ───────────────────────────── H11: transacción inmediata ─────────────────────────────

    private sealed class LockProbe(string databasePath) : IProgress<ImportProgress>
    {
        public string? Outcome { get; private set; }

        public void Report(ImportProgress value)
        {
            if (Outcome is not null || value.Phase != "saving" || value.Done == 0) return;
            // Otra conexión intenta escribir MIENTRAS la importación tiene su transacción abierta: debe chocar con el bloqueo de escritura.
            using var other = new SqliteConnection($"Data Source={databasePath};Pooling=False;Default Timeout=1");
            other.Open();
            try
            {
                using var begin = other.CreateCommand();
                begin.CommandText = "BEGIN IMMEDIATE";
                begin.ExecuteNonQuery();
                using var rollback = other.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                rollback.ExecuteNonQuery();
                Outcome = "pudo escribir";
            }
            catch (SqliteException ex) { Outcome = "bloqueada " + ex.SqliteErrorCode; }
        }
    }

    [Fact]
    public async Task While_the_import_transaction_is_open_no_other_connection_can_write()
    {
        var verses = new List<SyntheticBible.V>();
        for (var chapter = 1; chapter <= 50; chapter++) verses.AddRange(SyntheticBible.Chapter(1, chapter, 14));   // 700 versículos: el avance de «guardando» se informa cada 500
        var probe = new LockProbe(Path.Combine(_env.DataDirectory, "oak-proyektor.db"));

        await _env.ImportAsync(verses, progress: probe);

        Assert.Equal("bloqueada 5", probe.Outcome);                                            // SQLITE_BUSY
        Assert.Equal(700, await CountAsync("SELECT COUNT(*) FROM Verses"));
    }

    [Fact]
    public async Task The_write_lock_is_released_after_the_import_so_the_next_writer_is_not_blocked()
    {
        await _env.ImportAsync(Base());
        await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(Base().Where(v => v.Book != 65).ToList()));   // un rechazo también debe soltar el bloqueo

        await using var c = await _env.OpenAsync();
        await c.ExecuteAsync("INSERT INTO CitationHistory (BibleAbbreviation, BookNumber, Chapter, Label) VALUES ('SIN', 1, 1, 'Gn 1')");
        Assert.Equal(1, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM CitationHistory"));
    }

    // ───────────────────────────── H13: abreviatura ─────────────────────────────

    [Fact]
    public async Task The_abbreviation_is_trimmed_and_compared_without_case_so_rvr1960_is_the_same_bible_as_RVR1960()
    {
        var first = await _env.ImportAsync(Base(), "RVR1960");

        var again = await _env.ImportAsync(Base(), " rvr1960 ");

        Assert.False(again.CreatedBible);
        Assert.Equal(first.BibleId, again.BibleId);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Bibles"));
        Assert.Equal("RVR1960", (await _env.Query.ListBiblesAsync(CancellationToken.None)).Single().Abbreviation);   // se conserva la abreviatura guardada
        Assert.Equal(0, again.Updated);
    }

    [Fact]
    public async Task Another_translation_under_the_same_name_in_lowercase_hits_the_same_protections()
    {
        await _env.ImportAsync(Base(), "RVR1960");
        var other = Base().Select(v => v with { Text = "Otra traduccion " + v.Verse }).ToList();

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(other, "rvr1960"));

        Assert.Equal(BibleErrorCodes.TooManyChanges, ex.Code);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Bibles"));                       // y no creó una «gemela»
    }

    [Fact]
    public async Task Reimporting_by_a_different_case_keeps_the_passages_saved_with_the_stored_abbreviation()
    {
        await _env.ImportAsync(Base(), "RVR1960");
        await using (var c = await _env.OpenAsync())
        {
            var playlist = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name) VALUES ('Culto'); SELECT last_insert_rowid();");
            await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation) VALUES (@p, 0, 'PASSAGE', 'a', 1, 1, 149, 150, 'RVR1960')", new { p = playlist });
        }

        var report = await _env.ImportAsync(Base().Where(v => !(v.Book == 1 && v.Chapter == 1 && v.Verse == 150)).ToList(), "rvr1960");

        Assert.Equal(1, report.Deleted);
        var affected = Assert.Single(report.AffectedItems);                                    // el aviso de afectados usa la abreviatura GUARDADA, no la escrita
        Assert.Equal("playlist", affected.Kind);
    }

    [Theory]
    [InlineData("RV\u0007R")]
    [InlineData("RV\u0000R")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_abbreviation_with_control_characters_or_nothing_is_refused(string abbreviation)
    {
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, Base());
        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.Importer.ImportAsync(new BibleImportRequest(path, abbreviation, "n", "es"), null, CancellationToken.None));
        Assert.Equal(BibleErrorCodes.InvalidRequest, ex.Code);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Bibles"));
    }

    [Fact]
    public async Task Two_stored_bibles_that_differ_only_in_case_need_the_exact_abbreviation()
    {
        await _env.ImportAsync(Base(), "Abc");
        await using (var c = await _env.OpenAsync())   // una situación heredada (antes se distinguían las mayúsculas): dos filas que solo difieren en ellas
            await c.ExecuteAsync("INSERT INTO Bibles (Abbreviation, Name, Language) VALUES ('ABC', 'Otra', 'es')");

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(Base(), "abc"));
        Assert.Equal(BibleErrorCodes.InvalidRequest, ex.Code);

        var exact = await _env.ImportAsync(Base(), "Abc");                                      // la exacta sí se entiende
        Assert.False(exact.CreatedBible);
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM Bibles"));
    }

    // ───────────────────────────── copia previa ─────────────────────────────

    [Fact]
    public async Task A_reimport_that_modifies_makes_a_real_backup_with_the_old_content()
    {
        await _env.ImportAsync(Base());

        var report = await _env.ImportAsync(Edit(Base(), v => v is { Book: 1, Chapter: 1, Verse: 1 }));

        Assert.NotNull(report.BackupPath);
        Assert.StartsWith(_env.BackupsDirectory, report.BackupPath);
        Assert.Contains(BibleImporter.BackupPrefix, Path.GetFileName(report.BackupPath));
        await using var backup = new SqliteConnection($"Data Source={report.BackupPath};Mode=ReadOnly;Pooling=False");
        await backup.OpenAsync();
        Assert.Equal(156, await backup.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Verses"));
        Assert.Equal(SyntheticBible.TextOf(1, 1, 1), await backup.ExecuteScalarAsync<string>("SELECT Text FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BookNumber = 1 AND v.Chapter = 1 AND v.VerseNumber = 1"));   // el texto VIEJO
        Assert.EndsWith(" corregido", (await TextAsync(1, 1, 1))!);                              // y la BD ya tiene el nuevo
    }

    [Fact]
    public async Task No_backup_for_a_first_import_an_identical_reimport_or_one_that_only_adds_verses()
    {
        var first = await _env.ImportAsync(Base());
        var identical = await _env.ImportAsync(Base());
        var onlyAdds = await _env.ImportAsync([.. Base(), .. SyntheticBible.Chapter(1, 3, 4)]);

        Assert.Null(first.BackupPath);
        Assert.Null(identical.BackupPath);
        Assert.Null(onlyAdds.BackupPath);
        Assert.Equal(4, onlyAdds.Inserted);
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task Changing_only_the_attribution_or_the_profile_also_backs_up_first()
    {
        await _env.ImportAsync(SyntheticBible.Small());
        var attribution = await _env.ImportAsync(SyntheticBible.Small(), attribution: "Aviso nuevo");
        var profile = await _env.ImportAsync(SyntheticBible.Small(), profile: "merge-ps47-3jn");

        Assert.NotNull(attribution.BackupPath);
        Assert.NotNull(profile.BackupPath);
        Assert.Equal(2, BackupFiles().Count());
    }

    [Fact]
    public async Task Only_the_last_five_backups_are_kept_and_other_files_in_the_folder_are_left_alone()
    {
        Directory.CreateDirectory(_env.BackupsDirectory);
        var migrationBackup = Path.Combine(_env.BackupsDirectory, "oak-proyektor.v6.20260101T000000000Z.db");   // copia de una migración: no es de este tipo
        File.WriteAllText(migrationBackup, "no se toca");
        await _env.ImportAsync(Base());

        var paths = new List<string>();
        for (var i = 1; i <= 8; i++)
        {
            var report = await _env.ImportAsync(Edit(Base(), v => v is { Book: 1, Chapter: 1, Verse: 1 }, " ronda" + i));   // cada ronda modifica el versículo 1
            paths.Add(report.BackupPath!);
            await Task.Delay(3);   // sellos de tiempo distintos
        }

        var remaining = BackupFiles().Select(Path.GetFileName).Order().ToList();
        Assert.Equal(BibleImporter.BackupsToKeep, remaining.Count);
        Assert.Equal(paths.TakeLast(5).Select(Path.GetFileName).Order(), remaining);            // se conservan las 5 MÁS RECIENTES
        Assert.True(File.Exists(migrationBackup));
        Assert.Equal("no se toca", File.ReadAllText(migrationBackup));
    }

    [Fact]
    public async Task A_rejected_reimport_leaves_no_backup()
    {
        await _env.ImportAsync(Base());
        await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(Base().Select(v => v with { Text = "otra " + v.Verse }).ToList()));
        await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(Base().Where(v => v.Book != 65).ToList()));
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task If_the_backup_cannot_be_made_nothing_is_imported()
    {
        await _env.ImportAsync(Base());
        var blocker = Path.Combine(_env.FilesDirectory, "no-es-carpeta");                        // un ARCHIVO donde debería haber una carpeta
        Directory.CreateDirectory(_env.FilesDirectory);
        File.WriteAllText(blocker, "x");
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, Edit(Base(), v => v is { Book: 1, Chapter: 1, Verse: 1 }));

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.Importer.ImportAsync(
            new BibleImportRequest(path, "SIN", "Biblia sintética", "es", BackupDirectory: Path.Combine(blocker, "respaldos")), null, CancellationToken.None));

        Assert.Equal(BibleErrorCodes.ImportFailed, ex.Code);
        Assert.Contains("copias de seguridad", ex.Message);
        Assert.Equal(SyntheticBible.TextOf(1, 1, 1), await TextAsync(1, 1, 1));                  // nada cambió
    }

    [Fact]
    public async Task Without_a_backup_folder_the_import_still_runs_and_says_no_backup_was_made()
    {
        await _env.ImportAsync(Base());
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, Edit(Base(), v => v is { Book: 1, Chapter: 1, Verse: 1 }));

        var report = await _env.Importer.ImportAsync(new BibleImportRequest(path, "SIN", "Biblia sintética", "es"), null, CancellationToken.None);

        Assert.Null(report.BackupPath);
        Assert.Contains(report.Warnings, w => w.Contains("No se hizo copia de seguridad"));
        Assert.Equal(1, report.Updated);
    }

    [Fact]
    public async Task The_reports_and_warnings_never_contain_bible_text_even_with_every_new_message()
    {
        await _env.ImportAsync(Base());
        var messages = new List<string>();
        try { await _env.ImportAsync(Base().Select(v => v with { Text = "Otra traduccion Sintetico lorem ipsum" }).ToList()); } catch (BibleException ex) { messages.Add(ex.Message); }
        try { await _env.ImportAsync(Base().Where(v => v.Book != 65).ToList()); } catch (BibleException ex) { messages.Add(ex.Message); }
        var ok = await _env.ImportAsync(Edit(Base(), v => v.Verse == 1));
        messages.AddRange(ok.Warnings);
        messages.Add(JsonSerializer.Serialize(ok));

        foreach (var banned in new[] { "Sintetico", "lorem", "ipsum", "dolor", "Otra traduccion" })
            Assert.DoesNotContain(messages, m => m.Contains(banned, StringComparison.OrdinalIgnoreCase));
    }
}
