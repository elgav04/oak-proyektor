using System.Text.Json;
using Microsoft.Extensions.Logging;
using OakProyektor.Core;

namespace OakProyektor.App;

internal interface ISettingsStore
{
    AppSettings Current { get; }
    Task LoadAsync(CancellationToken cancellationToken);
    Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken);
}

/// <summary>Configuración en <c>data/settings.json</c>. Un archivo ausente o corrupto no impide arrancar: se usan los valores por defecto.</summary>
internal sealed class SettingsStore(ApplicationPaths paths, ILogger<SettingsStore> logger) : ISettingsStore
{
    // Los enums se guardan como texto en camelCase (igual que en el protocolo IPC): «moveToBest», no un número que cambia si se reordena el enum.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile AppSettings _current = new();

    private string FilePath => Path.Combine(paths.DataDirectory, "settings.json");

    public AppSettings Current => _current;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            await using var stream = File.OpenRead(FilePath);
            var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Json, cancellationToken).ConfigureAwait(false);
            if (loaded is not null) _current = loaded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "settings.json ilegible; se usan los valores por defecto");
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
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(next, Json), cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);   // escritura atómica: nunca queda un archivo a medias
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo guardar settings.json");
        }
        finally { _writeLock.Release(); }
    }
}
