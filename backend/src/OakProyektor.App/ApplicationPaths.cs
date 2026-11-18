namespace OakProyektor.App;

/// <summary>Carpetas de la aplicación. <c>OAK_DATA_DIR</c> permite forzar la carpeta de datos.</summary>
internal sealed record ApplicationPaths(string DataDirectory, string LogsDirectory, string WebRoot)
{
    public string ControlPage => Path.Combine(WebRoot, "index.html");
    public string OutputPage => Path.Combine(WebRoot, "output.html");

    public static ApplicationPaths Resolve()
    {
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = FindRepoRoot(baseDir);

        var overrideDir = Environment.GetEnvironmentVariable("OAK_DATA_DIR");
        var data = !string.IsNullOrWhiteSpace(overrideDir) ? overrideDir : Path.Combine(repoRoot ?? baseDir, "data");

        var webRoot = Path.Combine(baseDir, "wwwroot");
        if (!Directory.Exists(webRoot) && repoRoot is not null)
            webRoot = Path.Combine(repoRoot, "frontend", "dist");   // desarrollo sin copiar estáticos

        return new ApplicationPaths(data, Path.Combine(data, "logs"), webRoot);
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
