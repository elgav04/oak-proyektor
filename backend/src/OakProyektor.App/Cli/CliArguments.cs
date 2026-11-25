using OakProyektor.Core.Scripture;

namespace OakProyektor.App.Cli;

internal enum CliCommandKind { Help, ImportBible, VerifyBible, ShowVerse, ListBibles, ListBackups, RestoreBackup, ImportSong }

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
        "import-bible", "abbr", "name", "lang", "numbering", "attribution", "format", "against", "against-format", "show-verse", "bible", "restore-backup",
        "import-song", "title", "author", "copyright", "ccli",
    };
    private static readonly HashSet<string> SwitchOptions = new(StringComparer.Ordinal) { "verify-bible", "list-bibles", "list-backups", "allow-large-deletion", "allow-large-change", "no-merge", "help" };

    /// <summary>true si alguno de los argumentos pide una orden de consola (entonces no se abre ninguna ventana).</summary>
    public static bool IsCliInvocation(string[] args) =>
        args.Any(a => a is "--import-bible" or "--import-song" or "--verify-bible" or "--show-verse" or "--list-bibles" or "--list-backups" or "--restore-backup" or "--help" or "-h" or "/?");

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
        if (options.ContainsKey("list-backups")) kinds.Add(CliCommandKind.ListBackups);
        if (options.ContainsKey("restore-backup")) kinds.Add(CliCommandKind.RestoreBackup);
        if (options.ContainsKey("import-song")) kinds.Add(CliCommandKind.ImportSong);
        if (kinds.Count == 0) return (null, "Falta la orden. Usa --help para ver las opciones.");
        if (kinds.Count > 1) return (null, "Pide una sola orden a la vez (--import-bible, --import-song, --verify-bible, --show-verse, --list-bibles, --list-backups o --restore-backup).");

        var kind = kinds[0];
        string[] required = kind switch
        {
            CliCommandKind.ImportBible => ["import-bible", "abbr", "name", "lang"],
            CliCommandKind.VerifyBible => ["abbr", "against"],
            CliCommandKind.ShowVerse => ["show-verse"],
            CliCommandKind.RestoreBackup => ["restore-backup"],
            CliCommandKind.ImportSong => ["import-song"],
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
                             [--numbering <perfil>] [--attribution "<aviso de derechos>"] [--format <formato>]
                             [--allow-large-deletion] [--allow-large-change]
            Sin --numbering se CONSERVA el perfil guardado de la Biblia (``--numbering none`` lo quita de forma explícita).
            Una reimportación se RECHAZA si elimina más del 1 % de los versículos o hace desaparecer un libro o capítulo entero (--allow-large-deletion), o si cambia más
            del 1 % de los textos, cambia el nombre de la Biblia o deja vacíos textos que no lo están (--allow-large-change: otra traducción con la misma abreviatura).
            Antes de modificar o borrar algo se hace una copia de seguridad en la carpeta backups (se conservan las últimas 5).

          Importar la letra de una canción desde un archivo .txt (crea una canción NUEVA):
            OakProyektor.App --import-song <ruta.txt> [--title "<título>"] [--author "<autor>"] [--copyright "<derechos>"] [--ccli <número>] [--no-merge]
            Etiquetas entre corchetes ([Verso 1], [Coro]…), o bloques separados por líneas en blanco; una línea final «Orden: V1 C V2 C» es opcional.
            UTF-8 estricto y, si no lo es, Windows-1252 (se indica qué se detectó). Sin --title se usa el nombre del archivo. Solo muestra conteos, nunca la letra.

          Comparar una Biblia ya importada con otro archivo (no lo importa):
            OakProyektor.App --verify-bible --abbr <ABREV> --against <ruta> [--against-format <formato>]

          Ver un versículo o pasaje de la BD:
            OakProyektor.App --show-verse "Sal 47:9" [--bible <ABREV>]

          Listar las Biblias instaladas:
            OakProyektor.App --list-bibles

          Respaldos de la base de datos (carpeta backups de los datos):
            OakProyektor.App --list-backups                     lista las copias (diarias, previas a migraciones y a reimportaciones, anteriores a restaurar)
            OakProyektor.App --restore-backup <nombre|ruta>     restaura una copia. Se NIEGA si la aplicación está abierta; comprueba la copia con
                                                                PRAGMA integrity_check y guarda la base actual como oak-proyektor.before-restore.<fecha>.db

        Perfiles de numeración (--numbering): {string.Join(", ", NumberingProfiles.All.Select(p => $"{p.Id} ({p.Description})"))}
        Formatos (--format / --against-format): {string.Join(", ", BibleSourceFormats.All.Select(f => $"{f.Id}{(f.CanImport ? "" : " [solo verificar]")}"))}

        Códigos de salida: 0 correcto; 1 falló; 2 argumentos incorrectos; 3 importó pero el índice de búsqueda falló su comprobación.
        """;

    private static string Shorten(string text) => text.Length <= 30 ? text : text[..30] + "…";
}
