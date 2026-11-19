using System.Text.RegularExpressions;

namespace OakProyektor.Tests.Data;

/// <summary>
/// Reglas de código de la capa de datos (CLAUDE.md), vigiladas sobre <c>backend/src</c>:
/// <list type="number">
/// <item>INSERT OR REPLACE y REPLACE INTO están PROHIBIDOS: borran la fila vieja por debajo (con cascada sobre las hijas y sin garantías para el
/// índice FTS). Usa <c>INSERT … ON CONFLICT … DO UPDATE</c>. La comprobación NO es por línea: normaliza el texto para detectar SQL partido en
/// varias cadenas (<c>"INSERT OR " + "REPLACE INTO …"</c>), en varias líneas (cadenas crudas) o con la cláusula calculada (<c>INSERT OR {modo}</c>).</item>
/// <item>Solo <c>SqliteConnectionFactory</c> crea <c>SqliteConnection</c>: es quien activa <c>foreign_keys</c> y <c>recursive_triggers</c> (este último es
/// por conexión; una conexión creada a mano lo pierde y el FTS se desincroniza). Y quien abre para los repositorios espera a la BD
/// (<c>OpenWhenReadyAsync</c>).</item>
/// </list>
/// LÍMITE conocido: es un análisis de texto, no semántico. No detecta SQL armado con <c>string.Concat</c>/<c>string.Join</c>/<c>StringBuilder</c> ni
/// leído de un archivo en tiempo de ejecución; para eso están las pruebas de comportamiento (<c>ReplaceAndTriggerTests</c>).
/// </summary>
public partial class SourceRulesTests
{
    private const string FactoryFileName = "SqliteConnectionFactory.cs";

    // ───────────────────────────── detectores (puros, probados abajo) ─────────────────────────────

    /// <summary>Cualquier forma de REPLACE como cláusula de conflicto, o una cláusula INSERT OR calculada en tiempo de ejecución.</summary>
    [GeneratedRegex(
        @"\bOR\s+REPLACE\b|\bREPLACE\s+INTO\b|\bON\s+CONFLICT\s+REPLACE\b|\bINSERT\s+OR\s*\{|\bINSERT\s+OR\s+""\s*\+",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReplaceRegex();

    /// <summary>Concatenación de literales: <c>"…" + "…"</c> (también con prefijos <c>$</c>/<c>@</c> y a través de saltos de línea).</summary>
    [GeneratedRegex(@"""\s*\+\s*(?:\$@|@\$|\$|@)?""")]
    private static partial Regex LiteralConcatenationRegex();

    [GeneratedRegex(@"\bnew\s+(?:[\w.]+\.)?SqliteConnection\s*\(|\bSqliteConnection\s+\w+\s*=\s*new\s*\(")]
    private static partial Regex NewSqliteConnectionRegex();

    /// <summary>
    /// Quita los comentarios de línea completa (que mencionan la prohibición) conservando los saltos de línea, y une los literales concatenados
    /// para que el SQL partido en varias cadenas vuelva a ser una sola cadena. No quita comentarios al final de una línea de código: ante la
    /// duda se prefiere un falso positivo (la prueba falla y se revisa) a un falso negativo.
    /// </summary>
    internal static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("//") || line.StartsWith("--") || line.StartsWith("*") || line.StartsWith("/*")) lines[i] = "";
        }
        return LiteralConcatenationRegex().Replace(string.Join('\n', lines), "");
    }

    internal static IReadOnlyList<string> FindReplaceViolations(string text)
    {
        var normalized = Normalize(text);
        return [.. ReplaceRegex().Matches(normalized).Select(m =>
            $"≈ línea {normalized.AsSpan(0, m.Index).Count('\n') + 1}: …{Snippet(normalized, m)}…")];
    }

    internal static IReadOnlyList<string> FindNewSqliteConnections(string text)
    {
        var normalized = Normalize(text);
        return [.. NewSqliteConnectionRegex().Matches(normalized).Select(m =>
            $"≈ línea {normalized.AsSpan(0, m.Index).Count('\n') + 1}: {m.Value}")];
    }

    private static string Snippet(string text, Match m)
    {
        var start = Math.Max(0, m.Index - 15);
        var end = Math.Min(text.Length, m.Index + m.Length + 25);
        return Regex.Replace(text[start..end], @"\s+", " ");
    }

    // ───────────────────────────── el código real ─────────────────────────────

    private static string FindBackendSrc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "src");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("No se encontró backend/src subiendo desde " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourceFiles(params string[] extensions) =>
        Directory.EnumerateFiles(FindBackendSrc(), "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void The_scan_actually_sees_the_source_files()
    {
        // Sin esto, una ruta mal resuelta haría pasar las reglas de abajo «en vacío».
        var files = SourceFiles(".cs", ".sql").Select(Path.GetFileName).ToList();
        Assert.Contains(FactoryFileName, files);
        Assert.Contains("0001_Initial.sql", files);
        Assert.Contains("MigrationRunner.cs", files);
    }

    [Fact]
    public void No_source_or_migration_uses_insert_or_replace()
    {
        var offenders = SourceFiles(".cs", ".sql")
            .SelectMany(file => FindReplaceViolations(File.ReadAllText(file)).Select(v => $"{Path.GetFileName(file)} {v}"))
            .ToList();
        Assert.True(offenders.Count == 0, "INSERT OR REPLACE / REPLACE INTO está prohibido (usa ON CONFLICT DO UPDATE):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Only_the_connection_factory_creates_sqlite_connections()
    {
        var offenders = SourceFiles(".cs")
            .Where(file => !string.Equals(Path.GetFileName(file), FactoryFileName, StringComparison.Ordinal))
            .SelectMany(file => FindNewSqliteConnections(File.ReadAllText(file)).Select(v => $"{Path.GetFileName(file)} {v}"))
            .ToList();
        Assert.True(offenders.Count == 0,
            "Toda conexión debe salir de ISqliteConnectionFactory (activa foreign_keys y recursive_triggers; abre con OpenWhenReadyAsync):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_factory_does_create_the_connection_so_the_rule_is_not_vacuous()
    {
        var factory = SourceFiles(".cs").Single(f => Path.GetFileName(f) == FactoryFileName);
        Assert.NotEmpty(FindNewSqliteConnections(File.ReadAllText(factory)));
    }

    // ───────────────────────────── los detectores detectan (y no se pasan de listos) ─────────────────────────────

    [Theory]
    [InlineData("INSERT OR REPLACE INTO Verses VALUES (1)")]
    [InlineData("insert  or   replace into x")]
    [InlineData("REPLACE INTO Songs (Id) VALUES (1)")]
    [InlineData("CREATE TABLE T (Id INTEGER UNIQUE ON CONFLICT REPLACE)")]
    // SQL partido en varias cadenas
    [InlineData("""var sql = "INSERT OR " + "REPLACE INTO Verses VALUES (1)";""")]
    [InlineData("""var sql = "INSERT " + "OR " + "REPLACE" + " INTO Verses";""")]
    [InlineData("var sql = \"INSERT OR \"\n    + \"REPLACE INTO Verses\";")]
    [InlineData("""var sql = $"INSERT OR " + $"REPLACE INTO {table}";""")]
    [InlineData("""var sql = @"INSERT OR " + @"REPLACE INTO x";""")]
    // SQL en varias líneas (cadena cruda o verbatim)
    [InlineData("var sql = \"\"\"\n    INSERT OR\n    REPLACE INTO Verses VALUES (1)\n    \"\"\";")]
    [InlineData("var sql = @\"INSERT OR\r\nREPLACE INTO x\";")]
    // cláusula calculada: no se puede saber qué será
    [InlineData("""var sql = $"INSERT OR {mode} INTO Verses VALUES (1)";""")]
    [InlineData("""var sql = "INSERT OR " + mode + " INTO Verses";""")]
    public void The_replace_rule_detects_the_forbidden_forms_even_when_split(string source)
    {
        Assert.NotEmpty(FindReplaceViolations(source));
    }

    [Theory]
    [InlineData("INSERT INTO Songs (Id) VALUES (1) ON CONFLICT(Id) DO UPDATE SET Title = excluded.Title")]
    [InlineData("SELECT replace(Title, 'a', 'b') FROM Songs")]                  // la función replace() es inofensiva
    [InlineData("INSERT OR IGNORE INTO Songs (Id) VALUES (1)")]                // otras cláusulas están permitidas
    [InlineData("INSERT OR ABORT INTO Songs (Id) VALUES (1)")]
    [InlineData("// NO uses INSERT OR REPLACE aquí")]                          // comentarios que mencionan la prohibición
    [InlineData("/// <summary>Prohibido: REPLACE INTO</summary>")]
    [InlineData("-- no usar INSERT OR REPLACE")]
    [InlineData(" * INSERT OR REPLACE está prohibido")]
    [InlineData("""var a = "INSERT INTO X" + " VALUES (1)";""")]                  // concatenación inocente
    [InlineData("""var path = "reports" + "/replace-notes.txt";""")]
    public void The_replace_rule_ignores_harmless_text(string source)
    {
        Assert.Empty(FindReplaceViolations(source));
    }

    [Theory]
    [InlineData("var c = new SqliteConnection(cs);")]
    [InlineData("var c = new Microsoft.Data.Sqlite.SqliteConnection(cs);")]
    [InlineData("await using var c = new  SqliteConnection (cs);")]
    [InlineData("SqliteConnection c = new(cs);")]
    [InlineData("SqliteConnection connection = new (builder.ToString());")]
    public void The_connection_rule_detects_hand_made_connections(string source)
    {
        Assert.NotEmpty(FindNewSqliteConnections(source));
    }

    [Theory]
    [InlineData("var c = await connections.OpenWhenReadyAsync(readiness, ct);")]
    [InlineData("var c = await _connections.OpenAsync(ct);")]
    [InlineData("public async Task<SqliteConnection> OpenAsync(CancellationToken ct)")]   // el tipo en una firma no es una creación
    [InlineData("// new SqliteConnection(cs) está prohibido fuera de la fábrica")]
    [InlineData("var b = new SqliteConnectionStringBuilder { DataSource = path };")]       // otro tipo
    public void The_connection_rule_ignores_harmless_text(string source)
    {
        Assert.Empty(FindNewSqliteConnections(source));
    }
}
