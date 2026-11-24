using System.Diagnostics;
using System.Text;
using Dapper;
using OakProyektor.Core.Scripture;
using OakProyektor.Data.Scripture;
using Xunit.Abstractions;

namespace OakProyektor.Tests.Scripture;

/// <summary>
/// Rendimiento con un archivo SINTÉTICO de la forma real: 66 libros, 1189 capítulos y 31 102 versículos de ~120 caracteres (texto inventado).
/// Los tiempos se escriben en la salida de la prueba (<c>dotnet test --logger "console;verbosity=detailed"</c>); las comprobaciones son cotas muy holgadas
/// para detectar un problema grave (p. ej. una importación cuadrática), no para medir con precisión.
/// </summary>
public sealed class BiblePerformanceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private BibleEnv _env = null!;
    public async Task InitializeAsync() => _env = await BibleEnv.CreateAsync();
    public Task DisposeAsync() { _env.Dispose(); return Task.CompletedTask; }

    private static readonly string[] Vocabulary =
        ["alfa", "beta", "gamma", "delta", "sigma", "omega", "lorem", "ipsum", "dolor", "amet", "tempus", "fugit", "nova", "stella", "mare", "terra", "lumen", "umbra", "flumen", "silva"];

    /// <summary>31 102 versículos repartidos en los 1189 capítulos del canon (26 por capítulo y 188 capítulos con uno más).</summary>
    private static List<SyntheticBible.V> BuildFullSize()
    {
        var verses = new List<SyntheticBible.V>(31_102);
        var chapterIndex = 0;
        foreach (var book in CanonicalBooks.All)
            for (var chapter = 1; chapter <= book.Chapters; chapter++, chapterIndex++)
            {
                var count = chapterIndex < 188 ? 27 : 26;
                for (var verse = 1; verse <= count; verse++)
                {
                    var sb = new StringBuilder($"Sintetico{book.Number}x{chapter}x{verse}");
                    for (var w = 0; sb.Length < 120; w++) sb.Append(' ').Append(Vocabulary[(book.Number * 31 + chapter * 17 + verse * 7 + w * 3) % Vocabulary.Length]);
                    verses.Add(SyntheticBible.Verse(book.Number, chapter, verse, sb.ToString()));
                }
            }
        return verses;
    }

    [Fact]
    public async Task A_full_size_bible_imports_reimports_and_searches_in_reasonable_time()
    {
        var verses = BuildFullSize();
        Assert.Equal(31_102, verses.Count);

        var clock = Stopwatch.StartNew();
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, verses, splitInTwoLines: true, fileName: "completa.json");
        var writeMs = clock.ElapsedMilliseconds;
        output.WriteLine($"Archivo sintético: {new FileInfo(path).Length / 1_048_576.0:F1} MB, {verses.Count:N0} versículos (generarlo: {writeMs} ms)");

        // 1) Primera importación.
        var progress = new RecordingProgress();
        clock.Restart();
        var first = await _env.Importer.ImportAsync(new BibleImportRequest(path, "PRF", "Biblia sintética completa", "es"), progress, CancellationToken.None);
        var firstMs = clock.ElapsedMilliseconds;
        output.WriteLine($"Primera importación:           {firstMs,6} ms (informe: {first.ElapsedMs} ms) — {first.Inserted:N0} insertados");

        Assert.Equal(31_102, first.Inserted);
        Assert.Equal((66, 1189, 31_102, 0), (first.Validation.Books, first.Validation.Chapters, first.Validation.Verses, first.Validation.EmptyVerses));
        Assert.True(first.FtsIntegrityOk);
        Assert.Equal(31_102, first.JoinedVerses);                              // el archivo parte cada versículo en dos líneas
        Assert.Empty(first.Validation.MissingBooks);
        Assert.Equal(0, first.Validation.VerseGapChapters);
        Assert.True(progress.Reports.Count(r => r.Phase == "saving") > 50, "el avance debe reportar de forma continua");

        // 2) Reimportar lo mismo: no debe escribir nada.
        clock.Restart();
        var second = await _env.Importer.ImportAsync(new BibleImportRequest(path, "PRF", "Biblia sintética completa", "es"), null, CancellationToken.None);
        var secondMs = clock.ElapsedMilliseconds;
        output.WriteLine($"Reimportación sin cambios:     {secondMs,6} ms — {second.Unchanged:N0} sin cambios");
        Assert.Equal((0, 0, 31_102, 0), (second.Inserted, second.Updated, second.Unchanged, second.Deleted));
        Assert.Equal(first.BibleId, second.BibleId);

        // 3) Reimportar con 1 % de cambios y el perfil de numeración (uniones + eliminación explícita).
        var edited = verses.Select((v, i) => i % 100 == 0 ? v with { Text = v.Text + " editado" } : v).ToList();
        var editedPath = SyntheticBible.WriteYouVersion(_env.FilesDirectory, edited, fileName: "editada.json");
        clock.Restart();
        var third = await _env.Importer.ImportAsync(new BibleImportRequest(editedPath, "PRF", "Biblia sintética completa", "es", NumberingProfileId: "merge-ps47-3jn", AllowLargeChange: true), null, CancellationToken.None);   // 1 % + las uniones superan el tope: hay que permitirlo
        var thirdMs = clock.ElapsedMilliseconds;
        output.WriteLine($"Reimportación con 1 % de cambios y perfil: {thirdMs,6} ms — {third.Updated:N0} actualizados, {third.Deleted:N0} eliminados");
        Assert.Equal(2, third.Deleted);
        Assert.True(third.Updated >= 311);
        Assert.True(third.FtsIntegrityOk);

        // 4) Búsquedas y lecturas sobre la Biblia completa.
        clock.Restart();
        var word = await _env.Query.SearchAsync("Sintetico43x3x16", "PRF", 10, CancellationToken.None);
        var wordMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var common = await _env.Query.SearchAsync("lorem ipsum", "PRF", 50, CancellationToken.None);
        var commonMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var phrase = await _env.Query.SearchAsync("\"stella mare\"", "PRF", 50, CancellationToken.None);
        var phraseMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var lookup = await _env.Query.LookupAsync("Sal 119:1-10", "PRF", false, CancellationToken.None);
        var lookupMs = clock.ElapsedMilliseconds;
        output.WriteLine($"Búsqueda de una palabra única: {wordMs,6} ms ({word.Hits.Count} resultado)");
        output.WriteLine($"Búsqueda de dos palabras comunes: {commonMs,6} ms ({common.Hits.Count} resultados, truncado: {common.Truncated})");
        output.WriteLine($"Búsqueda de una frase:          {phraseMs,6} ms ({phrase.Hits.Count} resultados)");
        output.WriteLine($"Consulta de «Sal 119:1-10»:     {lookupMs,6} ms ({lookup.Verses.Count} versículos)");
        Assert.Single(word.Hits);
        Assert.Equal(50, common.Hits.Count);
        Assert.Equal(10, lookup.Verses.Count);

        // 5) El validador y el comparador por separado.
        await using var connection = await _env.OpenAsync();
        clock.Restart();
        var validation = await BibleValidator.ValidateAsync(connection, first.BibleId, CancellationToken.None);
        output.WriteLine($"Validación completa:           {clock.ElapsedMilliseconds,6} ms");
        clock.Restart();
        var ftsOk = await BibleImporter.CheckFtsAsync(connection, CancellationToken.None);
        output.WriteLine($"integrity-check del FTS:       {clock.ElapsedMilliseconds,6} ms");
        Assert.True(ftsOk.Ok);
        Assert.Equal(31_100, validation.Verses);

        // Cotas muy holgadas (el equipo de pruebas puede ser lento): solo cazan un comportamiento grave.
        Assert.True(firstMs < 120_000, $"La primera importación tardó {firstMs} ms");
        Assert.True(secondMs < 60_000, $"La reimportación sin cambios tardó {secondMs} ms");
        Assert.True(wordMs < 5_000 && commonMs < 5_000 && phraseMs < 5_000 && lookupMs < 5_000, "Una consulta tardó más de 5 s");
    }
}
