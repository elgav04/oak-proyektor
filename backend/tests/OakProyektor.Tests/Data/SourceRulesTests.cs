using System.Text.RegularExpressions;

namespace OakProyektor.Tests.Data;

/// <summary>
/// Reglas de código de la capa de datos (CLAUDE.md): INSERT OR REPLACE y REPLACE INTO están PROHIBIDOS. Borran la fila vieja por debajo
/// (con cascada sobre las hijas y sin garantías para el índice FTS); usa <c>INSERT … ON CONFLICT … DO UPDATE</c>.
/// </summary>
public partial class SourceRulesTests
{
    [GeneratedRegex(@"\bINSERT\s+OR\s+REPLACE\b|\bREPLACE\s+INTO\b|\bOR\s+REPLACE\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReplaceRegex();

    private static string FindBackendSrc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "src");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("No se encontró backend/src subiendo desde " + AppContext.BaseDirectory);
    }

    [Fact]
    public void No_source_or_migration_uses_insert_or_replace()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(FindBackendSrc(), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".sql")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("--") || line.StartsWith("//") || line.StartsWith("///") || line.StartsWith("*")) continue;   // comentarios que mencionan la prohibición
                if (ReplaceRegex().IsMatch(line)) offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {line.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0, "INSERT OR REPLACE / REPLACE INTO está prohibido (usa ON CONFLICT DO UPDATE):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("INSERT OR REPLACE INTO Verses VALUES (1)", true)]
    [InlineData("insert  or   replace into x", true)]
    [InlineData("REPLACE INTO Songs (Id) VALUES (1)", true)]
    [InlineData("INSERT INTO Songs (Id) VALUES (1) ON CONFLICT(Id) DO UPDATE SET Title = excluded.Title", false)]
    [InlineData("SELECT replace(Title, 'a', 'b') FROM Songs", false)]   // la función replace() es inofensiva
    public void The_rule_detects_the_forbidden_forms_and_not_the_harmless_ones(string sql, bool forbidden)
    {
        Assert.Equal(forbidden, ReplaceRegex().IsMatch(sql));
    }
}