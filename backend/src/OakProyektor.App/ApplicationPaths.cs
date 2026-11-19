namespace OakProyektor.App;

/// <summary>
/// Carpetas de la aplicación (hallazgo I10):
/// <list type="bullet">
/// <item><c>OAK_DATA_DIR</c>, si está definida, manda sobre todo lo demás.</item>
/// <item><b>Desarrollo</b> (se detecta el repositorio subiendo desde el ejecutable hasta <c>CLAUDE.md</c>): <c>data/</c> del repositorio.</item>
/// <item><b>Producción</b>: <c>%LOCALAPPDATA%\Oak-Proyektor</c>. Junto al ejecutable (p. ej. en <c>Program Files</c>) no se puede escribir.</item>
/// </list>
/// Dentro de la carpeta de datos: la BD, <c>backups/</c>, <c>logs/</c>, <c>settings.json</c> y <c>cache/</c> (incluido el perfil de WebView2).
/// </summary>
/// <param name="Origin">De dónde salió la carpeta de datos (para el log): OAK_DATA_DIR, repositorio, LocalAppData o temporal.</param>
internal sealed record ApplicationPaths(string DataDirectory, string LogsDirectory, string WebRoot, string Origin = "")
{
    public const string ProductFolderName = "Oak-Proyektor";

    public string ControlPage => Path.Combine(WebRoot, "index.html");
    public string OutputPage => Path.Combine(WebRoot, "output.html");

    /// <summary>Cachés regenerables: se pueden borrar sin perder datos del usuario.</summary>
    public string CacheDirectory => Path.Combine(DataDirectory, "cache");

    /// <summary>
    /// Perfil de WebView2 (<c>EBWebView</c>). Por defecto Photino lo deja en <c>%LOCALAPPDATA%\Photino</c>, compartido con cualquier otra
    /// aplicación Photino; aquí queda dentro de nuestra carpeta. Es el perfil del PANEL; la salida usa la carpeta hermana con sufijo -output (otro entorno de WebView2: ver WebViewProfile).
    /// </summary>
    public string WebViewDataDirectory => Path.Combine(CacheDirectory, "WebView2");

    public static ApplicationPaths Resolve() =>
        Resolve(Environment.GetEnvironmentVariable, AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath());

    /// <summary>Versión con las entradas inyectadas (para probar cada caso sin tocar el entorno real).</summary>
    public static ApplicationPaths Resolve(Func<string, string?> getEnvironmentVariable, string baseDirectory, string localAppData, string tempPath)
    {
        var repoRoot = FindRepoRoot(baseDirectory);

        string data, origin;
        var overrideDir = getEnvironmentVariable("OAK_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) { data = overrideDir; origin = "OAK_DATA_DIR"; }
        else if (repoRoot is not null) { data = Path.Combine(repoRoot, "data"); origin = "repositorio (desarrollo)"; }
        else if (!string.IsNullOrWhiteSpace(localAppData)) { data = Path.Combine(localAppData, ProductFolderName); origin = "LocalAppData"; }
        else { data = Path.Combine(tempPath, ProductFolderName); origin = "temporal (LocalAppData no disponible)"; }

        var webRoot = Path.Combine(baseDirectory, "wwwroot");
        if (!Directory.Exists(webRoot) && repoRoot is not null)
            webRoot = Path.Combine(repoRoot, "frontend", "dist");   // desarrollo sin copiar estáticos

        return new ApplicationPaths(data, Path.Combine(data, "logs"), webRoot, origin);
    }

    private static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")) && Directory.Exists(Path.Combine(dir.FullName, "backend")))
                return dir.FullName;
        }
        return null;
    }
}