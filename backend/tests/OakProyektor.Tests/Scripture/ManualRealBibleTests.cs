using OakProyektor.Data.Scripture;

namespace OakProyektor.Tests.Scripture;

/// <summary>
/// Prueba MANUAL contra el archivo REAL del operador. Se omite si no existe. Para ejecutarla (con la ruta fuera del repositorio):
/// <code>
/// $env:OAK_REAL_BIBLE_PATH = 'C:\ruta\al\archivo.json'
/// dotnet test backend/Oak-Proyektor.sln --filter "FullyQualifiedName~ManualRealBibleTests"
/// </code>
/// Importa en una BD TEMPORAL (nunca en <c>data/</c>) y comprueba solo conteos: no imprime ni compara texto.
/// </summary>
public sealed class ManualRealBibleTests : IAsyncLifetime
{
    private BibleEnv _env = null!;
    public async Task InitializeAsync() => _env = await BibleEnv.CreateAsync();
    public Task DisposeAsync() { _env.Dispose(); return Task.CompletedTask; }

    [ManualFact("OAK_REAL_BIBLE_PATH")]
    public async Task The_real_file_imports_with_66_books_and_1189_chapters_and_reimports_without_changes()
    {
        var path = Environment.GetEnvironmentVariable("OAK_REAL_BIBLE_PATH")!;
        var request = new BibleImportRequest(path, "REAL", "Biblia real (prueba manual)", "es");

        var first = await _env.Importer.ImportAsync(request, null, CancellationToken.None);

        Assert.Equal((66, 1189, 0), (first.Validation.Books, first.Validation.Chapters, first.Validation.EmptyVerses));
        Assert.True(first.FtsIntegrityOk);
        Assert.Equal(0, first.Validation.VerseGapChapters);

        var second = await _env.Importer.ImportAsync(request, null, CancellationToken.None);
        Assert.Equal(first.BibleId, second.BibleId);
        Assert.Equal((0, 0, 0), (second.Inserted, second.Updated, second.Deleted));
    }
}
