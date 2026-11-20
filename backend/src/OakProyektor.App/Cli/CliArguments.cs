using OakProyektor.Core.Scripture;

namespace OakProyektor.App.Cli;

internal enum CliCommandKind { Help, ImportBible, VerifyBible, ShowVerse, ListBibles }

/// <summary>Una orden de consola ya interpretada. <c>Options</c> usa los nombres de las opciones sin «--» (<c>abbr</c>, <c>name</c>…).</summary>
internal sealed record CliCommand(CliCommandKind Kind, IReadOnlyDictionary<string, string> Options)
{
    public string? Get(string name) => Options.TryGetValue(name, out var v) ? v : null;
    public bool Has(string name) => Options.ContainsKey(name);
}

/// <summary>
/// Interpreta los argumentos de consola. Si hay una orden de consola, la aplicación NO abre ventanas (ver <c>Program</c>). Función pura: se prueba sin consola.
/// </summary>
internal static class CliArguments
{
    // Opciones que llevan valor y opciones que son interruptores.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "import-bible", "abbr", "name", "lang", "numbering", "attribution", "format", "against", "against-format", "show-verse", "bible",
    };
    private static readonly HashSet<string> SwitchOptions = new(StringComparer.Ordinal) { "verify-bible", "list-bibles", "allow-large-deletion", "help" };

    /// <summary>true si alguno de los argumentos pide una orden de consola (entonces no se abre ninguna ventana).</summary>
    public static bool IsCliInvocation(string[] args) =>
        args.Any(a => a is "--import-bible" or "--verify-bible" or "--show-verse" or "--list-bibles" or "--help" or "-h" or "/?");

    public static (CliCommand? Command, string? Error) Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "/?") { options["help"] = ""; continue; }
            if (!arg.StartsWith("--", StringComparison.Ordinal)) return (null, $"Argumento inesperado «{Shorten(arg)}». Usa --help para ver las opciones.");
            var name = arg[2..];
            if (SwitchOptions.Contains(name)) { options[name] = ""; continue; }
            if (!ValueOptions.Contains(name)) return (null, $"Opción desconocida «--{Shorten(name)}». Usa --help para ver las opciones.");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return (null, $"Falta el valor de --{name}.");
            if (!options.TryAdd(name, args[++i])) return (null, $"La opción --{name} está repetida.");
        }

        if (options.ContainsKey("help")) return (new CliCommand(CliCommandKind.Help, options), null);

        var kinds = new List<CliCommandKind>();
        if (options.ContainsKey("import-bible")) kinds.Add(CliCommandKind.ImportBible);
        if (options.ContainsKey("verify-bible")) kinds.Add(CliCommandKind.VerifyBible);
        if (options.ContainsKey("show-verse")) kinds.Add(CliCommandKind.ShowVerse);
        if (options.ContainsKey("list-bibles")) kinds.Add(CliCommandKind.ListBibles);
        if (kinds.Count == 0) return (null, "Falta la orden. Usa --help para ver las opciones.");
        if (kinds.Count > 1) return (null, "Pide una sola orden a la vez (--import-bible, --verify-bible, --show-verse o --list-bibles).");

        var kind = kinds[0];
        string[] required = kind switch
        {
            CliCommandKind.ImportBible => ["import-bible", "abbr", "name", "lang"],
            CliCommandKind.VerifyBible => ["abbr", "against"],
            CliCommandKind.ShowVerse => ["show-verse"],
            _ => [],
        };
        var missing = required.Where(r => !options.ContainsKey(r) || string.IsNullOrWhiteSpace(options[r])).Select(r => "--" + r).ToList();
        if (missing.Count > 0) return (null, $"Faltan opciones obligatorias: {string.Join(", ", missing)}.");
        return (new CliCommand(kind, options), null);
    }

    public static string HelpText() =>
        $"""
        Oak-Proyektor — órdenes de consola (no abren ventanas).

          Importar una Biblia (actualiza en el sitio: conserva el BibleId):
            OakProyektor.App --import-bible <ruta> --abbr <ABREV> --name "<nombre>" --lang <idioma>
                             [--numbering <perfil>] [--attribution "<aviso de derechos>"] [--format <formato>] [--allow-large-deletion]

          Comparar una Biblia ya importada con otro archivo (no lo importa):
            OakProyektor.App --verify-bible --abbr <ABREV> --against <ruta> [--against-format <formato>]

          Ver un versículo o pasaje de la BD:
            OakProyektor.App --show-verse "Sal 47:9" [--bible <ABREV>]

          Listar las Biblias instaladas:
            OakProyektor.App --list-bibles

        Perfiles de numeración (--numbering): {string.Join(", ", NumberingProfiles.All.Select(p => $"{p.Id} ({p.Description})"))}
        Formatos (--format / --against-format): {string.Join(", ", BibleSourceFormats.All.Select(f => $"{f.Id}{(f.CanImport ? "" : " [solo verificar]")}"))}

        Códigos de salida: 0 correcto; 1 falló; 2 argumentos incorrectos; 3 importó pero el índice de búsqueda falló su comprobación.
        """;

    private static string Shorten(string text) => text.Length <= 30 ? text : text[..30] + "…";
}
