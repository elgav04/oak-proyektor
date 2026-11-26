using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using OakProyektor.App;
using OakProyektor.App.Handlers;
using OakProyektor.Core.Scripture;
using OakProyektor.Data;
using OakProyektor.Data.Scripture;
using OakProyektor.Ipc;

namespace OakProyektor.Tests.Scripture;

/// <summary>Parser: tope de tramos (H6) y ordinales (H7). Funciones puras: sin BD.</summary>
public class ReferenceParserHardeningTests
{
    private static string Repeat(string prefix, string item, int count) => prefix + string.Join(",", Enumerable.Repeat(item, count));

    [Fact]
    public void Exactly_the_maximum_number_of_segments_is_accepted_and_one_more_gives_a_clear_error()
    {
        var ok = ReferenceParser.Parse(Repeat("Jn 3:", "16", ReferenceParser.MaxSegments));
        Assert.True(ok.Success);
        Assert.Equal(ReferenceParser.MaxSegments, ok.Reference!.Segments.Count);

        var tooMany = ReferenceParser.Parse(Repeat("Jn 3:", "16", ReferenceParser.MaxSegments + 1));
        Assert.False(tooMany.Success);
        Assert.Equal(ReferenceErrorCode.TooManySegments, tooMany.Error!.Code);
        Assert.Contains("Demasiados tramos", tooMany.Error.Message);
        Assert.Contains(ReferenceParser.MaxSegments.ToString(), tooMany.Error.Message);
    }

    [Theory]
    [InlineData("Jn 3:", "99")]        // versículos inexistentes: el caso del hallazgo (miles de consultas sin filas)
    [InlineData("Sal ", "23")]         // capítulos sueltos
    [InlineData("Mt 5:", "3-5")]       // rangos
    public void A_reference_with_thousands_of_segments_is_rejected_fast_without_building_them(string prefix, string item)
    {
        var sw = Stopwatch.StartNew();
        var result = ReferenceParser.Parse(Repeat(prefix, item, 5000));
        sw.Stop();
        Assert.Equal(ReferenceErrorCode.TooManySegments, result.Error!.Code);
        Assert.Null(result.Reference);
        Assert.True(sw.ElapsedMilliseconds < 500, $"tardó {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Semicolon_groups_count_too()
    {
        var text = "Jn " + string.Join("; ", Enumerable.Repeat("3:16", ReferenceParser.MaxSegments + 1));
        Assert.Equal(ReferenceErrorCode.TooManySegments, ReferenceParser.Parse(text).Error!.Code);
    }

    [Theory]
    [InlineData("1° Juan 3:16", 62)]
    [InlineData("1º Juan 3:16", 62)]
    [InlineData("1ª Juan 3:16", 62)]
    [InlineData("1.º Juan 3:16", 62)]
    [InlineData("1.ª Juan 3:16", 62)]
    [InlineData("1a Juan 3:16", 62)]
    [InlineData("1A Juan 3:16", 62)]
    [InlineData("1o Juan 3:16", 62)]
    [InlineData("1. Juan 3:16", 62)]
    [InlineData("1°Juan 3:16", 62)]
    [InlineData("1° Jn 3:16", 62)]
    [InlineData("1 Juan 3:16", 62)]
    [InlineData("1Jn 3:16", 62)]
    [InlineData("I Juan 3:16", 62)]
    [InlineData("2a Juan 4", 63)]
    [InlineData("3° Juan 4", 64)]
    [InlineData("1° Samuel 3:1", 9)]
    [InlineData("2a Samuel 3:1", 10)]
    [InlineData("2.ª Corintios 5:17", 47)]
    [InlineData("1a Tesalonicenses 4:16", 52)]
    public void Ordinal_markers_before_a_numbered_book_are_accepted(string text, int book)
    {
        var result = ReferenceParser.Parse(text);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(book, result.Reference!.BookNumber);
    }

    [Theory]
    [InlineData("Jud 14", 65)]            // Jud y Jue
    [InlineData("Jue 3:1", 7)]
    [InlineData("Fil 1:1", 50)]           // Fil y Flm
    [InlineData("Flm 5", 57)]
    [InlineData("Jn 3:16", 43)]           // Jn y 1 Jn
    [InlineData("1 Jn 3:16", 62)]
    [InlineData("Juan 3:16", 43)]
    [InlineData("Am 3:1", 30)]            // un libro que empieza por «a»: el dígito no existe aquí
    [InlineData("Os 3:1", 28)]            // y por «o»
    public void The_collisions_already_covered_still_resolve_to_the_same_book(string text, int book)
    {
        var result = ReferenceParser.Parse(text);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(book, result.Reference!.BookNumber);
    }

    [Fact]
    public void An_ordinal_form_gives_the_same_reference_as_the_plain_form_and_the_book_comes_back_normalized()
    {
        Assert.Equal(ReferenceParser.Parse("1 Juan 3:16").Reference!.Format(), ReferenceParser.Parse("1.ª Juan 3:16").Reference!.Format());
        Assert.Equal(("1 Juan", "3:16"), ReferenceParser.SplitBookAndRest("1° Juan 3:16"));
        Assert.Equal(("1 Juan", "3:16"), ReferenceParser.SplitBookAndRest("1a Juan 3:16"));
        Assert.Equal(("Juan", "3:16"), ReferenceParser.SplitBookAndRest("Juan 3:16"));
        Assert.Equal(("1co", "13:4"), ReferenceParser.SplitBookAndRest("1co 13:4"));
    }

    [Theory]
    [InlineData("1 a")]            // sin nombre
    [InlineData("1°")]
    [InlineData("1.ª")]
    [InlineData("4° Juan 3:16")]   // no existe un 4.º
    public void Dangling_or_impossible_ordinals_fail_without_throwing(string text) => Assert.False(ReferenceParser.Parse(text).Success);
}

/// <summary>Historial con la referencia completa (H8), anotación en segundo plano (H5), cancelación a mitad de escritura y privacidad (H15).</summary>
public class BibleHardeningTests : IAsyncLifetime
{
    private BibleEnv _env = null!;

    public async Task InitializeAsync() => _env = await BibleEnv.CreateAsync();
    public Task DisposeAsync() { _env.Dispose(); return Task.CompletedTask; }

    private static List<SyntheticBible.V> Without(params (int Book, int Chapter, int Verse)[] gone) =>
        [.. SyntheticBible.Small().Where(v => !gone.Contains((v.Book, v.Chapter, v.Verse)))];

    private async Task<long> CountAsync(string sql)
    {
        await using var c = await _env.OpenAsync();
        return await c.ExecuteScalarAsync<long>(sql);
    }

    // ───────────────────────────── H8: historial completo ─────────────────────────────

    [Fact]
    public async Task The_history_stores_every_segment_of_the_reference_including_chapter_crossing_ones()
    {
        await _env.ImportAsync(SyntheticBible.Small());
        await _env.Query.LookupAsync("Mt 5:3,5,7", "SIN", true, CancellationToken.None);
        await _env.Query.LookupAsync("Gn 1-2", "SIN", true, CancellationToken.None);
        await _env.Query.LookupAsync("Gn 1:5-2:2", "SIN", true, CancellationToken.None);

        var history = await _env.Query.GetHistoryAsync(10, CancellationToken.None);
        Assert.Equal(["Gn 1:5-2:2", "Gn 1-2", "Mt 5:3,5,7"], history.Select(h => h.Label));   // la forma canónica COMPLETA

        await using var c = await _env.OpenAsync();
        var rows = (await c.QueryAsync<(string Label, long Position, long Sc, long? Sv, long Ec, long? Ev)>(
            "SELECT h.Label, s.Position, s.StartChapter, s.StartVerse, s.EndChapter, s.EndVerse FROM CitationHistorySegments s JOIN CitationHistory h ON h.Id = s.HistoryId ORDER BY h.Label, s.Position")).ToList();
        Assert.Equal(
            [("Gn 1-2", 0L, 1L, (long?)null, 2L, (long?)null),
             ("Gn 1:5-2:2", 0L, 1L, 5L, 2L, 2L),
             ("Mt 5:3,5,7", 0L, 5L, 3L, 5L, 3L), ("Mt 5:3,5,7", 1L, 5L, 5L, 5L, 5L), ("Mt 5:3,5,7", 2L, 5L, 7L, 5L, 7L)],
            rows);
    }

    [Fact]
    public async Task Citing_the_same_passage_again_moves_it_up_without_duplicating_its_segments_and_trimming_removes_them_too()
    {
        _env.Dispose();
        _env = await BibleEnv.CreateAsync(new BibleQueryOptions(() => null, () => 2));
        await _env.ImportAsync(SyntheticBible.Small());
        await _env.Query.LookupAsync("Mt 5:3,5,7", "SIN", true, CancellationToken.None);
        await _env.Query.LookupAsync("Mt 5:3,5,7", "SIN", true, CancellationToken.None);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM CitationHistory"));
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM CitationHistorySegments"));

        await _env.Query.LookupAsync("Gn 1-2", "SIN", true, CancellationToken.None);
        await _env.Query.LookupAsync("Mt 5:4", "SIN", false, CancellationToken.None);   // no se anota
        await _env.Query.LookupAsync("Sal 47:9-10", "SIN", true, CancellationToken.None);   // el límite es 2: sale «Mt 5:3,5,7»

        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM CitationHistory"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM CitationHistorySegments"));   // Gn 1-2 (1) + Sal 47:9-10 (1): los tramos del recortado se fueron en cascada
    }

    [Theory]
    [InlineData("Mt 5:3,5,7", 40, 5, 7, true)]       // el ÚLTIMO tramo: antes solo se miraba el primero
    [InlineData("Mt 5:3,5,7", 40, 5, 5, true)]       // el del medio
    [InlineData("Mt 5:3,5,7", 40, 5, 3, true)]
    [InlineData("Mt 5:3,5,7", 40, 5, 4, false)]      // entre tramos: no es de la cita
    [InlineData("Mt 5:3,5,7", 40, 5, 8, false)]
    [InlineData("Gn 1-2", 1, 2, 4, true)]            // el segundo capítulo: antes solo se miraba el primero
    [InlineData("Gn 1-2", 1, 1, 1, true)]
    [InlineData("Gn 1:5-2:2", 1, 2, 1, true)]        // rango que cruza de capítulo
    [InlineData("Gn 1:5-2:2", 1, 2, 3, false)]
    [InlineData("Gn 1:5-2:2", 1, 1, 4, false)]
    public async Task A_deleted_verse_inside_any_segment_of_a_history_entry_is_reported(string reference, int book, int chapter, int verse, bool affected)
    {
        await _env.ImportAsync(SyntheticBible.Small());
        await _env.Query.LookupAsync(reference, "SIN", true, CancellationToken.None);

        var report = await _env.ImportAsync(Without((book, chapter, verse)));

        Assert.Equal(1, report.Deleted);
        var items = report.AffectedItems.Where(a => a.Kind == "history").ToList();
        if (affected) Assert.Equal(reference, Assert.Single(items).Reference);
        else Assert.Empty(items);
    }

    [Fact]
    public async Task Entries_written_before_the_segments_existed_are_checked_through_their_canonical_label()
    {
        await _env.ImportAsync(SyntheticBible.Small());
        await using (var c = await _env.OpenAsync())
        {
            // Como las dejó la migración 7: solo el primer tramo en las columnas y SIN filas de tramos.
            await c.ExecuteAsync("INSERT INTO CitationHistory (BibleAbbreviation, BookNumber, Chapter, VerseStart, VerseEnd, Label) VALUES ('SIN', 40, 5, 3, NULL, 'Mt 5:3,5,7')");
            await c.ExecuteAsync("INSERT INTO CitationHistory (BibleAbbreviation, BookNumber, Chapter, VerseStart, VerseEnd, Label) VALUES ('SIN', 1, 1, NULL, NULL, 'Gn 1-2')");
        }
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM CitationHistorySegments"));

        var report = await _env.ImportAsync(Without((40, 5, 7), (1, 2, 4)));

        Assert.Equal(["Gn 1-2", "Mt 5:3,5,7"], report.AffectedItems.Where(a => a.Kind == "history").Select(a => a.Reference).Order());
    }

    [Fact]
    public async Task A_legacy_entry_with_an_unreadable_label_falls_back_to_its_own_columns()
    {
        await _env.ImportAsync(SyntheticBible.Small());
        await using (var c = await _env.OpenAsync())
            await c.ExecuteAsync("INSERT INTO CitationHistory (BibleAbbreviation, BookNumber, Chapter, VerseStart, VerseEnd, Label) VALUES ('SIN', 40, 5, 3, 4, '???')");

        var hit = await _env.ImportAsync(Without((40, 5, 4)));
        Assert.Equal("???", Assert.Single(hit.AffectedItems, a => a.Kind == "history").Reference);

        var miss = await _env.ImportAsync(Without((40, 5, 4), (40, 5, 9)));
        Assert.DoesNotContain(miss.AffectedItems, a => a.Kind == "history");
    }

    [Fact]
    public async Task Other_bibles_history_entries_are_never_reported()
    {
        await _env.ImportAsync(SyntheticBible.Small());
        await _env.ImportAsync(SyntheticBible.Small(), abbreviation: "OTRA", name: "Otra Biblia");
        await _env.Query.LookupAsync("Mt 5:3,5,7", "OTRA", true, CancellationToken.None);
        var report = await _env.ImportAsync(Without((40, 5, 7)));
        Assert.DoesNotContain(report.AffectedItems, a => a.Kind == "history");
    }

    // ───────────────────────────── H5: el historial no espera al bloqueo de escritura ─────────────────────────────

    private (BackgroundCitationRecorder Recorder, CapturingLogger Log) UseBackgroundRecorder(TimeSpan writeTimeout)
    {
        var log = new CapturingLogger();
        var recorder = new BackgroundCitationRecorder(_env.Connections, _env.Readiness, log.Factory().CreateLogger<BackgroundCitationRecorder>()) { WriteTimeout = writeTimeout };
        _env.UseRecorder(recorder);
        return (recorder, log);
    }

    [Fact]
    public async Task A_lookup_with_record_returns_at_once_while_another_writer_holds_the_database()
    {
        await _env.ImportAsync(SyntheticBible.Small());
        var (recorder, log) = UseBackgroundRecorder(TimeSpan.FromMilliseconds(900));
        using var _ = recorder;

        await using var writer = await _env.OpenAsync();
        await writer.ExecuteAsync("BEGIN IMMEDIATE");   // un escritor largo (la importación de una Biblia retiene este bloqueo durante segundos)
        try
        {
            var sw = Stopwatch.StartNew();
            var passage = await _env.Query.LookupAsync("Mt 5:3,5,7", "SIN", true, CancellationToken.None);
            sw.Stop();

            Assert.Equal(3, passage.Verses.Count);
            Assert.True(sw.ElapsedMilliseconds < 500, $"la consulta esperó {sw.ElapsedMilliseconds} ms al bloqueo");

            // La anotación se descarta con un tiempo de espera corto, con registro (solo la referencia).
            var idle1 = await recorder.WaitIdleAsync(TimeSpan.FromSeconds(10));
            Assert.True(idle1, "discarded=" + recorder.Discarded + " log=" + string.Join(" || ", log.Lines.Take(8)));
            Assert.Equal(1, recorder.Discarded);
            Assert.Contains("Se descartó la anotación del historial de Mt 5:3,5,7", log.All);
        }
        finally { await writer.ExecuteAsync("ROLLBACK"); }

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM CitationHistory"));

        // Sin el bloqueo, la siguiente sí se anota (el consumidor no quedó atascado).
        await _env.Query.LookupAsync("Gn 1-2", "SIN", true, CancellationToken.None);
        Assert.True(await recorder.WaitIdleAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(["Gn 1-2"], (await _env.Query.GetHistoryAsync(10, CancellationToken.None)).Select(h => h.Label));
    }

    [Fact]
    public async Task Show_content_is_handled_without_waiting_for_the_history_while_a_long_writer_holds_the_database()
    {
        await _env.ImportAsync(SyntheticBible.Small());
        var (recorder, _) = UseBackgroundRecorder(TimeSpan.FromSeconds(6));
        using var disposeRecorder = recorder;

        var shown = new TaskCompletionSource<DateTime>();
        var intake = new InboundQueue(Microsoft.Extensions.Logging.Abstractions.NullLogger<InboundQueue>.Instance);
        var outbox = new Outbox(Microsoft.Extensions.Logging.Abstractions.NullLogger<Outbox>.Instance);
        var dispatcher = new IpcDispatcher(
            [new BibleLookupHandler(_env.Query), new LambdaHandler(IpcKind.Command, IpcTypes.OutputShowContent, () => shown.TrySetResult(DateTime.UtcNow))],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IpcDispatcher>.Instance);
        var processor = new InboundProcessor(intake, dispatcher, outbox, new OrderClock(), Microsoft.Extensions.Logging.Abstractions.NullLogger<InboundProcessor>.Instance);
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        await using var writer = await _env.OpenAsync();
        await writer.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var start = DateTime.UtcNow;
            intake.Enqueue(WindowKind.Control, """{"v":17,"id":"q1","kind":"query","type":"bible.lookup","payload":{"reference":"Mt 5:3","bible":"SIN","record":true}}""");
            intake.Enqueue(WindowKind.Control, """{"v":17,"id":"c1","kind":"command","type":"output.showContent","payload":{}}""");

            var at = await shown.Task.WaitAsync(TimeSpan.FromSeconds(3));   // el escritor sigue reteniendo el bloqueo (hasta 6 s): no hay espera
            Assert.True((at - start).TotalMilliseconds < 2500, $"showContent esperó {(at - start).TotalMilliseconds} ms");
        }
        finally { await writer.ExecuteAsync("ROLLBACK"); }

        cts.Cancel();
        intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class LambdaHandler(string kind, string type, Action body) : IIpcHandler
    {
        public string Kind => kind;
        public string Type => type;
        public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken) { body(); return Task.FromResult<object?>(null); }
    }

    [Fact]
    public async Task A_burst_of_citations_queues_a_bounded_number_and_never_throws()
    {
        await _env.ImportAsync(SyntheticBible.Small());
        var (recorder, log) = UseBackgroundRecorder(TimeSpan.FromSeconds(2));
        using var _ = recorder;
        for (var i = 0; i < 300; i++) await _env.Query.LookupAsync($"Sal 119:{1 + i % 24}", "SIN", true, CancellationToken.None);
        var idle = await recorder.WaitIdleAsync(TimeSpan.FromSeconds(20));
        Assert.True(idle, "discarded=" + recorder.Discarded + " log=" + string.Join(" || ", log.Lines.Take(3)));
        Assert.True(await CountAsync("SELECT COUNT(*) FROM CitationHistory") <= BibleQueryOptions.DefaultHistoryLimit);
    }

    // ───────────────────────────── H15: cancelación a mitad de escritura ─────────────────────────────

    private static List<SyntheticBible.V> Big(string suffix = "") =>
        [.. Enumerable.Range(1, 8).SelectMany(c => SyntheticBible.Chapter(19, c, 150)).Select(v => v with { Text = v.Text + suffix })];   // 1200 versículos: el avance sale cada 500

    [Fact]
    public async Task Cancelling_in_the_middle_of_a_first_import_leaves_the_database_exactly_as_it_was()
    {
        using var cts = new CancellationTokenSource();
        var cancelledAt = -1;
        var progress = new ActionProgress(p =>
        {
            if (p.Phase == "saving" && p.Done >= 500 && p.Done < p.Total) { cancelledAt = p.Done; cts.Cancel(); }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _env.ImportAsync(Big(), progress: progress, cancellationToken: cts.Token));

        Assert.True(cancelledAt >= 500, "la cancelación debió ocurrir DESPUÉS de empezar a escribir");
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Bibles"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Books"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Verses"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM VersesFts_docsize"));   // ni restos en el índice de búsqueda

        // La base no quedó bloqueada ni a medias: una importación normal después funciona.
        var ok = await _env.ImportAsync(Big());
        Assert.Equal(1200, ok.Inserted);
        Assert.True(ok.FtsIntegrityOk);
    }

    [Fact]
    public async Task Cancelling_in_the_middle_of_a_reimport_keeps_every_original_text()
    {
        await _env.ImportAsync(Big());
        long Sum() => CountAsync("SELECT COALESCE(SUM(LENGTH(Text)), 0) FROM Verses").GetAwaiter().GetResult();
        var before = Sum();

        using var cts = new CancellationTokenSource();
        var cancelled = false;
        var progress = new ActionProgress(p =>
        {
            if (p.Phase == "saving" && p.Done >= 500 && !cancelled) { cancelled = true; cts.Cancel(); }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _env.ImportAsync(Big(" CAMBIADO"), allowLargeChange: true, progress: progress, cancellationToken: cts.Token));

        Assert.True(cancelled);
        Assert.Equal(before, Sum());                                                                  // ningún texto cambió: se revirtió TODO
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Verses WHERE Text LIKE '%CAMBIADO%'"));
        Assert.Equal(1200, await CountAsync("SELECT COUNT(*) FROM Verses"));
        await using var c = await _env.OpenAsync();
        await c.ExecuteAsync("INSERT INTO VersesFts(VersesFts, rank) VALUES('integrity-check', 1)");   // el índice sigue sincronizado (lanza si no)

        var again = await _env.ImportAsync(Big(" CAMBIADO"), allowLargeChange: true);                // y se puede repetir
        Assert.Equal(1200, again.Updated);
    }

    // ───────────────────────────── H15: privacidad con logs y mensajes de error ─────────────────────────────

    private const string Marker = "ZZMARCASINTETICAZZ";

    private string WriteRaw(string name, string json)
    {
        Directory.CreateDirectory(_env.FilesDirectory);
        var path = Path.Combine(_env.FilesDirectory, name);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    // JSON del formato aprobado armado con objetos (sin literales anidados): un capítulo con sus versículos, un libro con sus capítulos.
    private static object Item(int verse, string text) => new { type = "verse", verse_numbers = new[] { verse }, lines = new[] { text } };
    private static object Chapter(string usfm, int number, params (int V, string T)[] verses) => new { usfm = usfm + "." + number, items = verses.Select(v => Item(v.V, v.T)).ToArray() };
    private static object Book(string usfm, params object[] chapters) => new { usfm, chapters };
    private static string Json(params object[] books) => JsonSerializer.Serialize(new { books });

    [Fact]
    public async Task No_synthetic_bible_text_appears_in_reports_progress_logs_or_error_messages()
    {
        var log = new CapturingLogger();
        var importer = new BibleImporter(_env.Connections, _env.Readiness, log.Factory().CreateLogger<BibleImporter>());
        var progress = new RecordingProgress();
        var everything = new StringBuilder();   // TODO lo que se podría mostrar al operador

        async Task Run(string label, string path, bool large = false)
        {
            try
            {
                var report = await importer.ImportAsync(new BibleImportRequest(path, "PRIV", "Privacidad", "es", null, null, null, large, large, _env.BackupsDirectory), progress, CancellationToken.None);
                everything.AppendLine(JsonSerializer.Serialize(report));
            }
            catch (Exception ex)
            {
                everything.AppendLine($"[{label}] {ex.GetType().Name}: {ex.Message}");
                for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException) everything.AppendLine("  " + inner.Message);
            }
        }

        // 1) Una primera importación correcta, con texto marcado, versículos fuera del canon, duplicados DISTINTOS y un libro inexistente.
        await Run("primera", WriteRaw("privacidad.json", Json(
            Book("GEN", Chapter("GEN", 1, (1, Marker + " uno"), (2, Marker + " dos"), (200, Marker + " fuera")), Chapter("GEN", 99, (1, Marker + " capitulo inexistente"))),
            Book("GEN", Chapter("GEN", 1, (1, Marker + " OTRO"))),
            Book("ZZZ", Chapter("ZZZ", 1, (1, Marker + " libro desconocido"))))));

        // 2) Reimportación con otro texto (rechazada por cambiar demasiado) y luego forzada.
        var changed = WriteRaw("cambiado.json", Json(Book("GEN", Chapter("GEN", 1, (1, Marker + " CAMBIADO uno"), (2, Marker + " CAMBIADO dos")))));
        await Run("rechazada", changed);
        await Run("forzada", changed, large: true);

        // 3) JSON roto a mitad de un texto marcado, con sintaxis inválida, con un número que es texto y con un código de libro que es el marcador.
        await Run("truncado", WriteRaw("truncado.json", "{\"books\":[{\"usfm\":\"GEN\",\"chapters\":[{\"usfm\":\"GEN.1\",\"items\":[{\"type\":\"verse\",\"verse_numbers\":[1],\"lines\":[\"" + Marker + " cortado"));
        await Run("sintaxis", WriteRaw("sintaxis.json", "{\"books\":[{\"usfm\":\"GEN\",\"chapters\":[{\"usfm\":\"GEN.1\",\"items\":[{\"type\":\"verse\",\"verse_numbers\":[1],\"lines\":[\"" + Marker + "\"]]}]}]}"));
        await Run("numero", WriteRaw("numero.json", "{\"books\":[{\"usfm\":\"GEN\",\"chapters\":[{\"usfm\":\"GEN.1\",\"items\":[{\"type\":\"verse\",\"verse_numbers\":[\"" + Marker + "\"],\"lines\":[\"" + Marker + "\"]}]}]}]}"));
        await Run("usfm", WriteRaw("usfm.json", Json(new { usfm = Marker, chapters = new[] { new { usfm = Marker + ".1", items = Array.Empty<object>() } } })));
        // 4) No es UTF-8 (Windows-1252 con el marcador y acentos).
        var latin = Path.Combine(_env.FilesDirectory, "latin1.json");
        File.WriteAllBytes(latin, Encoding.Latin1.GetBytes("{\"books\":[{\"usfm\":\"GEN\",\"chapters\":[{\"usfm\":\"GEN.1\",\"items\":[{\"type\":\"verse\",\"verse_numbers\":[1],\"lines\":[\"" + Marker + " áéíóú ñ\"]}]}]}]}"));
        await Run("latin1", latin);

        // 5) Cancelada a mitad de la escritura (el informe parcial no existe, pero el mensaje y el log tampoco deben llevar texto).
        // 6) Una consulta que falla y los demás caminos de error del servicio de consultas.
        try { await _env.Query.LookupAsync("Mt 99:1", "PRIV", false, CancellationToken.None); } catch (BibleException ex) { everything.AppendLine(ex.Message); }   // (lo que ESCRIBE el operador se le devuelve en su error: no es texto de la Biblia)
        try { await _env.Query.SearchAsync("\"\"", "PRIV", 10, CancellationToken.None); } catch (BibleException ex) { everything.AppendLine(ex.Message); }
        everything.AppendLine(string.Join("\n", progress.Reports.Select(p => $"{p.Phase} {p.Done}/{p.Total}")));

        // El texto SÍ llegó a la base (el caso 1): la prueba comprueba que NO sale de ahí hacia ningún informe, log o mensaje.
        Assert.True(await CountAsync("SELECT COUNT(*) FROM Verses WHERE Text LIKE '%" + Marker + "%'") > 0);

        Assert.DoesNotContain(Marker, everything.ToString());
        Assert.DoesNotContain(Marker, log.All);
        Assert.NotEmpty(log.Lines);   // el registro se capturó de verdad (si no, la prueba no probaría nada)
        Assert.Contains("PRIV", everything.ToString());
    }
}
