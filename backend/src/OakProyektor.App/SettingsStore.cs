using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OakProyektor.Core;
using OakProyektor.Core.Output;
using OakProyektor.Core.Playlists;
using OakProyektor.Core.Scripture;
using OakProyektor.Core.Songs;

namespace OakProyektor.App;

internal interface ISettingsStore
{
    AppSettings Current { get; }
    /// <summary>Avisos de la última carga (bloques inválidos que volvieron a su valor por defecto, archivo dañado). Vacío si todo estaba bien. El panel los muestra.</summary>
    IReadOnlyList<string> Warnings { get; }
    /// <summary>Se completa cuando <see cref="LoadAsync"/> terminó (con o sin avisos), para que quien pregunte por los avisos no los pierda por llegar antes.</summary>
    Task Loaded { get; }
    Task LoadAsync(CancellationToken cancellationToken);
    Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken);
}

/// <summary>
/// Configuración en <c>data/settings.json</c>. Un archivo ausente o corrupto no impide arrancar.
/// <para><b>Carga por BLOQUE (H10)</b>: cada propiedad se lee y se valida por separado. Un valor inválido (un tema editado a mano con un color que no es un color, un
/// enum desconocido, un número fuera de rango) vuelve a SU valor por defecto y se avisa, sin tirar el resto de la configuración y sin que cada proyección de la Biblia
/// se rechace después con <c>output.invalidContent</c>. Si se corrigió algo o el archivo estaba dañado, se guarda antes una copia como <c>settings.json.bad</c>
/// (el siguiente guardado reemplaza el archivo y, sin copia, se perdería lo que el operador escribió a mano).</para>
/// <para><b>Escritura atómica de verdad</b>: se escribe un temporal, se VACÍA a disco (<c>Flush(true)</c>, no solo a la caché del sistema) y solo entonces se renombra sobre el
/// archivo: un corte de luz deja el archivo anterior entero, no uno vacío.</para>
/// </summary>
internal sealed class SettingsStore(ApplicationPaths paths, ILogger<SettingsStore> logger) : ISettingsStore
{
    // Los enums se guardan como texto en camelCase (igual que en el protocolo IPC): «moveToBest», no un número que cambia si se reordena el enum.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile AppSettings _current = new();
    private volatile IReadOnlyList<string> _warnings = [];

    public const int MaxRecentDisplays = 8;

    private string FilePath => Path.Combine(paths.DataDirectory, "settings.json");
    private string BadPath => FilePath + ".bad";

    public AppSettings Current => _current;
    public IReadOnlyList<string> Warnings => _warnings;
    public Task Loaded => _loaded.Task;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            string text;
            try { text = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "settings.json no se pudo leer; se usan los valores por defecto");
                _warnings = ["No se pudo leer la configuración (settings.json): se usan los valores por defecto."];
                return;
            }

            var warnings = new List<string>();
            var parsed = Parse(text, warnings);
            if (parsed is not null) _current = parsed;
            if (warnings.Count > 0)
            {
                var copied = TryKeepBadCopy();
                if (copied) warnings.Add("Se guardó una copia del archivo original como settings.json.bad (en la carpeta de datos) por si quieres recuperar lo que habías escrito.");
                foreach (var w in warnings) logger.LogWarning("settings.json: {Warning}", w);
            }
            _warnings = warnings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "settings.json ilegible; se usan los valores por defecto");
            _warnings = ["No se pudo leer la configuración (settings.json): se usan los valores por defecto."];
        }
        finally { _loaded.TrySetResult(); }
    }

    /// <summary>Lee la configuración bloque a bloque. Devuelve null si el archivo no es un objeto JSON (dañado): entonces todo es por defecto.</summary>
    internal static AppSettings? Parse(string text, List<string> warnings)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException)
        {
            warnings.Add("El archivo de configuración (settings.json) estaba dañado: se usan los valores por defecto.");
            return null;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("El archivo de configuración (settings.json) estaba dañado: se usan los valores por defecto.");
                return null;
            }
            var root = doc.RootElement;
            var d = new AppSettings();

            T Block<T>(string name, T fallback, string label, Func<T, string?>? validate = null)
            {
                if (!TryGet(root, name, out var element) || element.ValueKind == JsonValueKind.Null) return fallback;
                try
                {
                    var value = element.Deserialize<T>(Json);
                    if (value is null) return fallback;
                    if (validate?.Invoke(value) is { } problem)
                    {
                        warnings.Add($"{label}: {problem} Se usa el valor por defecto.");
                        return fallback;
                    }
                    return value;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
                {
                    warnings.Add($"{label} no es válido en settings.json: se usa el valor por defecto.");
                    return fallback;
                }
            }

            var recent = Block<string[]?>("recentDisplayIds", null, "Monitores recientes");
            return new AppSettings(
                AutoRestoreOutput: Block("autoRestoreOutput", d.AutoRestoreOutput, "«Reanudar la salida sola»"),
                PreferredDisplayId: Block<string?>("preferredDisplayId", null, "Monitor preferido"),
                OnDisplayLost: Block("onDisplayLost", d.OnDisplayLost, "«Si el monitor de la salida desaparece»", v => Enum.IsDefined(v) ? null : "valor desconocido."),
                RecentDisplayIds: recent is null ? null : [.. recent.Where(s => !string.IsNullOrWhiteSpace(s)).Take(MaxRecentDisplays)],
                ShowPanelAlertOnOutput: Block("showPanelAlertOnOutput", d.ShowPanelAlertOnOutput, "Aviso de panel sin respuesta"),
                DefaultBible: Block<string?>("defaultBible", null, "Biblia por defecto"),
                CitationHistoryLimit: Block("citationHistoryLimit", d.CitationHistoryLimit, "Límite del historial de citas", v => v is >= 0 and <= 1000 ? null : "debe estar entre 0 y 1000."),
                BibleDisplay: ReadBibleDisplay(root, warnings),
                SongDisplay: ReadSongDisplay(root, warnings),
                PlaylistDisplay: ReadPlaylistDisplay(root, warnings),
                AlertDisplay: ReadAlertDisplay(root, warnings));
        }
    }

    /// <summary>
    /// El bloque de las alertas (v15), por partes: un tema, un modo o una duración inválidos vuelven a SU valor por defecto con un aviso (si no, cada alerta se rechazaría con
    /// <c>alert.invalidContent</c>); un bloque ilegible entero vuelve a los valores por defecto.
    /// </summary>
    private static AlertDisplaySettings? ReadAlertDisplay(JsonElement root, List<string> warnings)
    {
        if (!TryGet(root, "alertDisplay", out var element) || element.ValueKind == JsonValueKind.Null) return null;
        AlertDisplaySettings? block;
        try { block = element.Deserialize<AlertDisplaySettings>(Json); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException) { block = null; }
        if (block is null)
        {
            warnings.Add("La configuración de las alertas (alertDisplay) no es válida en settings.json: se usan los valores por defecto.");
            return null;
        }
        var mode = block.Mode;
        if (AlertRules.ValidateMode(mode) is not null)
        {
            warnings.Add("El modo de las alertas no es válido en settings.json: se usa la cinta estática.");
            mode = AlertMode.Band;
        }
        var duration = block.DurationSec;
        if (AlertRules.ValidateDuration(duration) is not null)
        {
            warnings.Add($"La duración de las alertas no es válida en settings.json ({duration} s): se usan {AlertRules.DefaultDurationSec} s.");
            duration = AlertRules.DefaultDurationSec;
        }
        var theme = block.Theme;
        if (theme is not null && AlertRules.ValidateTheme(theme) is { } themeProblem)
        {
            warnings.Add($"El tema de las alertas no es válido en settings.json ({themeProblem.TrimEnd('.')}): se usa el tema por defecto.");
            theme = null;
        }
        return block with { Mode = mode, DurationSec = duration, Theme = theme };
    }

    /// <summary>El bloque de las listas (v13): un valor inválido (o ilegible) vuelve a su valor por defecto («quedarse») con un aviso; no tira el resto de la configuración.</summary>
    private static PlaylistDisplaySettings? ReadPlaylistDisplay(JsonElement root, List<string> warnings)
    {
        if (!TryGet(root, "playlistDisplay", out var element) || element.ValueKind == JsonValueKind.Null) return null;
        PlaylistDisplaySettings? block;
        try { block = element.Deserialize<PlaylistDisplaySettings>(Json); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException) { block = null; }
        if (block is null || PlaylistDisplaySettings.Validate(block) is not null)
        {
            warnings.Add("La configuración de las listas (playlistDisplay) no es válida en settings.json: se usan los valores por defecto.");
            return null;
        }
        return block;
    }

    /// <summary>
    /// El bloque de las canciones (v12), validado como el de la Biblia: un tema o un crossfade inválidos vuelven a su valor por defecto (si no, cada proyección de una canción se
    /// rechazaría con <c>output.invalidContent</c>) y se avisa; un bloque ilegible entero vuelve a los valores por defecto.
    /// </summary>
    private static SongDisplaySettings? ReadSongDisplay(JsonElement root, List<string> warnings)
    {
        if (!TryGet(root, "songDisplay", out var element) || element.ValueKind == JsonValueKind.Null) return null;
        SongDisplaySettings? block;
        try { block = element.Deserialize<SongDisplaySettings>(Json); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException) { block = null; }
        if (block is null || !Enum.IsDefined(block.AtEnd))
        {
            warnings.Add("La configuración de las canciones (songDisplay) no es válida en settings.json: se usan los valores por defecto.");
            return null;
        }
        var theme = block.Theme;
        if (theme is not null && ContentValidator.ValidateTheme(theme) is { } themeProblem)
        {
            warnings.Add($"El tema de las canciones no es válido en settings.json ({themeProblem.TrimEnd('.')}): se usa el tema por defecto.");
            theme = null;
        }
        var transition = block.TransitionMs;
        if (ContentValidator.ValidateTransition(transition, "Canciones") is not null)
        {
            warnings.Add($"La duración del crossfade de las canciones no es válida en settings.json ({transition} ms): se usa {SongDisplaySettings.DefaultTransitionMs} ms.");
            transition = SongDisplaySettings.DefaultTransitionMs;
        }
        return block with { Theme = theme, TransitionMs = transition };
    }

    /// <summary>
    /// El bloque de la Biblia se valida por partes: un tema inválido no tira las opciones ni los nombres cortos, y viceversa. Lo que no es válido vuelve a su valor por defecto
    /// (el tema por defecto, <c>BibleDisplaySettings.DefaultTheme</c>); de lo contrario cada <c>showContent</c> de un versículo se rechazaría con <c>output.invalidContent</c>.
    /// </summary>
    private static BibleDisplaySettings? ReadBibleDisplay(JsonElement root, List<string> warnings)
    {
        if (!TryGet(root, "bibleDisplay", out var element) || element.ValueKind == JsonValueKind.Null) return null;
        BibleDisplaySettings? block;
        try { block = element.Deserialize<BibleDisplaySettings>(Json); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // Un campo con el tipo equivocado (p. ej. «transitionMs»: «rápido»): se intenta rescatar el tema y los nombres por separado antes de rendirse del todo.
            block = null;
        }
        if (block is null)
        {
            warnings.Add("La configuración de la Biblia (bibleDisplay) no es válida en settings.json: se usan los valores por defecto.");
            return null;
        }
        var theme = block.Theme;
        if (theme is not null && ContentValidator.ValidateTheme(theme) is { } themeProblem)
        {
            warnings.Add($"El tema de la Biblia no es válido en settings.json ({themeProblem.TrimEnd('.')}): se usa el tema por defecto.");
            theme = null;
        }
        var transition = block.TransitionMs;
        if (ContentValidator.ValidateTransition(transition, "Biblia") is not null)
        {
            warnings.Add($"La duración del crossfade de la Biblia no es válida en settings.json ({transition} ms): se usa {BibleDisplaySettings.DefaultTransitionMs} ms.");
            transition = BibleDisplaySettings.DefaultTransitionMs;
        }
        var names = block.ShortNames;
        if (names is not null && BibleDisplaySettings.Validate(new BibleDisplaySettings(ShortNames: names)) is { } namesProblem)
        {
            warnings.Add($"Los nombres cortos de las Biblias no son válidos en settings.json ({namesProblem.TrimEnd('.')}): se usan las abreviaturas.");
            names = null;
        }
        return block with { Theme = theme, TransitionMs = transition, ShortNames = names };
    }

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>Copia el archivo tal como estaba a <c>settings.json.bad</c> (se reemplaza una copia anterior). false si no se pudo: no impide arrancar.</summary>
    private bool TryKeepBadCopy()
    {
        try
        {
            File.Copy(FilePath, BadPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "No se pudo guardar settings.json.bad");
            return false;
        }
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = change(_current);
            _current = next;   // el ajuste vale ya aunque falle el disco
            Directory.CreateDirectory(paths.DataDirectory);
            var temp = FilePath + ".tmp";
            await WriteDurablyAsync(temp, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(next, Json)), cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);   // renombrado atómico: el temporal ya está entero EN DISCO
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo guardar settings.json");
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Escribe y VACÍA a disco (<c>Flush(true)</c> = FlushFileBuffers) antes de cerrar: sin esto, un corte de luz justo tras el renombrado puede dejar el archivo vacío.</summary>
    internal static async Task WriteDurablyAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
}
