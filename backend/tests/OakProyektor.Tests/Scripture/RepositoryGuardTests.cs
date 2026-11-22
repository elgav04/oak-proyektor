using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OakProyektor.Tests.Scripture;

/// <summary>
/// Guardas del repositorio sobre los datos del usuario (CLAUDE.md, «Biblias y derechos de autor»):
/// <list type="bullet">
/// <item>NADA bajo <c>data/</c> está versionado (ahí viven las Biblias del operador, que tienen derechos de autor).</item>
/// <item>NINGUNA prueba depende de <c>data/</c>: las pruebas usan datos sintéticos y carpetas temporales. Una prueba manual que necesite los archivos reales
/// se marca con <see cref="ManualFactAttribute"/> y se omite si no existen.</item>
/// </list>
/// </summary>
public class RepositoryGuardTests
{
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")) && Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio subiendo desde " + AppContext.BaseDirectory);
    }

    /// <summary>Ejecuta git en la raíz y devuelve su salida; null si git no está disponible o la carpeta no es un repositorio (p. ej. un zip descargado).</summary>
    private static string? Git(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = FindRepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000)) { process.Kill(); return null; }
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    [Fact]
    public void No_file_under_data_is_tracked_by_git()
    {
        var tracked = Git("ls-files -- data");
        if (tracked is null) return;     // sin git o sin repositorio no hay nada versionado que comprobar

        Assert.True(string.IsNullOrWhiteSpace(tracked),
            "Hay archivos bajo data/ versionados (contienen datos del operador, p. ej. Biblias con derechos de autor). Quítalos del índice con `git rm --cached`:" +
            Environment.NewLine + tracked);
    }

    [Fact]
    public void Git_ignores_anything_inside_data_so_a_stray_add_cannot_pick_it_up()
    {
        var ignored = Git("check-ignore data/imports/cualquier.json data/oak-proyektor.db data/backups/x.db");
        if (ignored is null) return;
        var lines = ignored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("data/imports/cualquier.json", lines);
        Assert.Contains("data/oak-proyektor.db", lines);
        Assert.Contains("data/backups/x.db", lines);
    }

    [Fact]
    public void The_gitignore_has_the_data_rule_anchored_to_the_root()
    {
        var gitignore = File.ReadAllLines(Path.Combine(FindRepoRoot(), ".gitignore")).Select(l => l.Trim()).ToList();
        Assert.Contains("/data/", gitignore);     // con barra inicial: sin ella ignoraría también backend/tests/OakProyektor.Tests/Data/ (ver CLAUDE.md)
    }

    [Fact]
    public void No_test_reads_the_data_folder()
    {
        var tests = Path.Combine(FindRepoRoot(), "backend", "tests");
        var offenders = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.EndsWith("RepositoryGuardTests.cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line))
                .Where(x => Regex.IsMatch(x.Text, @"data[\\/]+imports", RegexOptions.IgnoreCase)))
            .Select(x => $"{x.File}:{x.Line}: {x.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Las pruebas no pueden depender de data/imports (los archivos del operador no están en el repositorio). Usa datos sintéticos, o marca la prueba como [ManualFact]:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_guard_actually_sees_the_test_sources_so_it_cannot_pass_empty()
    {
        var tests = Path.Combine(FindRepoRoot(), "backend", "tests");
        var files = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.Contains("BibleImporterTests.cs", files);
        Assert.Contains("RepositoryGuardTests.cs", files);
    }
}

/// <summary>
/// Una prueba MANUAL: necesita un archivo real del operador (fuera del repositorio) y se OMITE si no está. Se activa definiendo la variable de entorno
/// <paramref name="environmentVariable"/> con la ruta de ese archivo; sin ella, o si el archivo no existe, aparece como omitida y no falla.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ManualFactAttribute : FactAttribute
{
    public ManualFactAttribute(string environmentVariable)
    {
        var path = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            Skip = $"Prueba manual: define {environmentVariable} con la ruta de un archivo real que exista (fuera del repositorio) para ejecutarla.";
    }
}

/// <summary>Integridad de los archivos de texto del repositorio (hallazgo H15).</summary>
public class TextFileIntegrityTests
{

    [Fact]
    public void No_versioned_text_file_contains_stray_control_characters()
    {
        // H15: CLAUDE.md llegó a tener 0x08 (retroceso) y 0x07 (campana) donde debía haber `bible.x` y `attribution`: un script de PowerShell interpretó «`b» y «`a»
        // como escapes al escribir la línea. Un carácter de control (salvo tabulador, CR y LF) en un archivo de texto del repositorio siempre es ese tipo de daño.
        var root = FindRepositoryRoot();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".cs", ".ts", ".tsx", ".mjs", ".ps1", ".json", ".sql", ".html", ".css", ".csproj", ".props", ".yml" };
        var skipped = new[] { "node_modules", "bin", "obj", "dist", "data", ".git", "wwwroot", "TestResults" };
        var damaged = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => skipped.Contains(part))) continue;
            if (!extensions.Contains(Path.GetExtension(file))) continue;
            var bytes = File.ReadAllBytes(file);
            var line = 1;
            foreach (var b in bytes)
            {
                if (b == (byte)'\n') line++;
                else if (b < 32 && b != (byte)'\t' && b != (byte)'\r') { damaged.Add($"{relative}:{line} (0x{b:X2})"); break; }
            }
        }
        Assert.True(damaged.Count == 0, "Caracteres de control en archivos de texto: " + string.Join(", ", damaged));
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
        throw new DirectoryNotFoundException("No se encontró CLAUDE.md subiendo desde " + AppContext.BaseDirectory);
    }
}
