using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OakProyektor.Core.Scripture;
using OakProyektor.Data.Scripture;
using Xunit.Abstractions;

namespace OakProyektor.Tests.Scripture;

/// <summary>
/// H14: consumo de memoria al importar un archivo SINTÉTICO de ≈ 24 MB con la forma del real (66 libros, 1189 capítulos, 31 102 versículos y el campo <c>html</c> de relleno
/// por capítulo, que el lector descarta). Mide el PICO de memoria gestionada sobre la línea base (muestreo cada 5 ms con el recolector sin forzar) y el pico del conjunto de
/// trabajo del proceso, y lo escribe en la salida de la prueba y en <c>%TEMP%\oak-memory-report.txt</c>. Lo que se comprueba es una cota holgada para cazar una
/// regresión grave (varias copias del texto vivas a la vez), no una medición exacta.
/// </summary>
// La medición es del PROCESO entero: otras pruebas en paralelo la falsearían (104 MB con la batería completa frente a ≈ 30 MB sola). Esta colección corre sin paralelismo.
[CollectionDefinition("Memoria", DisableParallelization = true)]
public sealed class MemoryCollection;

[Collection("Memoria")]
public sealed class BibleMemoryTests(ITestOutputHelper output) : IAsyncLifetime
{
    private BibleEnv _env = null!;
    public async Task InitializeAsync() => _env = await BibleEnv.CreateAsync();
    public Task DisposeAsync() { _env.Dispose(); return Task.CompletedTask; }

    private static readonly string[] Vocabulary =
        ["alfa", "beta", "gamma", "delta", "sigma", "omega", "lorem", "ipsum", "dolor", "amet", "tempus", "fugit", "nova", "stella", "mare", "terra", "lumen", "umbra", "flumen", "silva"];

    /// <summary>Archivo de ≈ 24 MB: 31 102 versículos de ≈ 125 caracteres y ≈ 14 KB de <c>html</c> de relleno por capítulo.</summary>
    internal static string WriteFullSizeWithHtml(string directory, string fileName = "completa-24mb.json", int htmlBytesPerChapter = 14_000)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var filler = new string('x', 80);
        var html = new StringBuilder(htmlBytesPerChapter + 100);
        while (html.Length < htmlBytesPerChapter) html.Append("<span class=\"content\">HTML sintético de relleno</span>").Append(filler);
        var htmlText = html.ToString();

        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream);
        w.WriteStartObject();
        w.WriteNumber("version_id", 1);
        w.WriteStartArray("books");
        var chapterIndex = 0;
        foreach (var book in CanonicalBooks.All)
        {
            w.WriteStartObject();
            w.WriteString("usfm", book.Usfm);
            w.WriteString("human", "Libro sintético");
            w.WriteStartArray("chapters");
            for (var chapter = 1; chapter <= book.Chapters; chapter++, chapterIndex++)
            {
                var count = chapterIndex < 188 ? 27 : 26;
                w.WriteStartObject();
                w.WriteString("usfm", $"{book.Usfm}.{chapter}");
                w.WriteString("human", chapter.ToString());
                w.WriteString("html", htmlText);
                w.WriteStartArray("items");
                for (var verse = 1; verse <= count; verse++)
                {
                    var text = new StringBuilder($"Sintetico{book.Number}x{chapter}x{verse}");
                    for (var k = 0; text.Length < 125; k++) text.Append(' ').Append(Vocabulary[(book.Number * 31 + chapter * 17 + verse * 7 + k * 3) % Vocabulary.Length]);
                    w.WriteStartObject();
                    w.WriteString("type", "verse");
                    w.WriteStartArray("verse_numbers"); w.WriteNumberValue(verse); w.WriteEndArray();
                    w.WriteStartArray("lines"); w.WriteStringValue(text.ToString()); w.WriteEndArray();
                    w.WriteBoolean("np", false);
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

    private sealed record Measurement(double PeakManagedMb, double AllocatedMb, double WorkingSetGrowthMb, long Ms, ImportReport Report);

    /// <summary>Importa y devuelve el pico de memoria gestionada sobre la línea base (muestreo cada 5 ms), lo asignado en total y el crecimiento del pico del conjunto de trabajo.</summary>
    private async Task<Measurement> MeasureAsync(BibleImportRequest request)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var baseline = GC.GetTotalMemory(forceFullCollection: false);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;

        long peakManaged = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(forceFullCollection: false));
                try { await Task.Delay(5, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });

        var clock = Stopwatch.StartNew();
        var report = await _env.Importer.ImportAsync(request, null, CancellationToken.None);
        clock.Stop();
        stop.Cancel();
        await sampler;
        peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(forceFullCollection: false));

        var peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
        return new Measurement((peakManaged - baseline) / 1_048_576.0, (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / 1_048_576.0,
            Math.Max(0, peakWorkingSet - workingSetBefore) / 1_048_576.0, clock.ElapsedMilliseconds, report);
    }

    [Fact]
    public async Task Importing_and_reimporting_a_24_MB_file_keeps_the_memory_peak_low()
    {
        var path = WriteFullSizeWithHtml(_env.FilesDirectory);
        var megabytes = new FileInfo(path).Length / 1_048_576.0;
        Assert.InRange(megabytes, 20, 28);

        var first = await MeasureAsync(new BibleImportRequest(path, "MEM", "Biblia sintética de 24 MB", "es"));
        var again = await MeasureAsync(new BibleImportRequest(path, "MEM", "Biblia sintética de 24 MB", "es"));

        string Line(string what, Measurement m) =>
            $"{what}: archivo {megabytes:F1} MB | pico gestionado {m.PeakManagedMb:F1} MB | asignado en total {m.AllocatedMb:F0} MB | crecimiento del pico del proceso {m.WorkingSetGrowthMb:F0} MB | {m.Ms} ms";
        var text = Line("primera importación", first) + Environment.NewLine + Line("reimportación idéntica", again);
        output.WriteLine(text);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "oak-memory-report.txt"), $"{DateTime.Now:HH:mm:ss}{Environment.NewLine}{text}{Environment.NewLine}");

        Assert.Equal(31_102, first.Report.Inserted);
        Assert.Equal((66, 1189, 31_102), (first.Report.Validation.Books, first.Report.Validation.Chapters, first.Report.Validation.Verses));
        Assert.Equal((0, 0, 31_102), (again.Report.Inserted, again.Report.Updated, again.Report.Unchanged));
        // Medido: antes de quitar copias del texto, 38,8 MB (primera importacion) y 43-50 MB (reimportacion); ahora ~30-31 MB. La cota deja margen y caza una vuelta atras (cada copia del texto son ~8 MB en UTF-16).
        Assert.True(first.PeakManagedMb < 60, $"primera importación: pico gestionado {first.PeakManagedMb:F1} MB");
        Assert.True(again.PeakManagedMb < 60, $"reimportación: pico gestionado {again.PeakManagedMb:F1} MB");
    }
}