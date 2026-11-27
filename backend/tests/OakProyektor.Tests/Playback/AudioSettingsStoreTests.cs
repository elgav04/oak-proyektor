using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.Core.Audio;

namespace OakProyektor.Tests.Playback;

/// <summary>El bloque <c>audio</c> de settings.json: carga por partes (un valor inválido vuelve a SU valor por defecto con aviso), el dispositivo guardado por id sobrevive y un archivo anterior sin el bloque carga los valores por defecto.</summary>
public sealed class AudioSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private SettingsStore Create() => new(new ApplicationPaths(_dir, Path.Combine(_dir, "logs"), _dir), NullLogger<SettingsStore>.Instance);

    private async Task<SettingsStore> LoadAsync(string json)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(SettingsPath, json);
        var store = Create();
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task A_file_from_before_the_audio_block_loads_the_defaults_without_warnings()
    {
        var store = await LoadAsync("""{ "autoRestoreOutput": true }""");
        Assert.Equal(new AudioSettings(), store.Current.AudioConfig);
        Assert.Empty(store.Warnings);
    }

    [Fact]
    public async Task The_church_device_and_the_volume_survive_a_restart_by_stable_id()
    {
        var a = Create();
        await a.UpdateAsync(s => s with { Audio = new AudioSettings("{0.0.1.00000000}.{abc-123}", "Interfaz USB", 35, 600, 900, 300, 300, true) }, CancellationToken.None);
        var b = Create();
        await b.LoadAsync(CancellationToken.None);
        Assert.Equal(new AudioSettings("{0.0.1.00000000}.{abc-123}", "Interfaz USB", 35, 600, 900, 300, 300, true), b.Current.AudioConfig);
        Assert.Empty(b.Warnings);
    }

    [Fact]
    public async Task An_invalid_part_returns_to_its_own_default_without_dropping_the_rest()
    {
        var store = await LoadAsync("""{ "audio": { "churchDeviceId": "id-usb", "masterVolumePct": 400, "trackFadeInMs": 99999, "trackFadeOutMs": 700, "resumeOnDeviceReturn": true } }""");
        var a = store.Current.AudioConfig;
        Assert.Equal(("id-usb", 50, 500, 700, true), (a.ChurchDeviceId, a.MasterVolumePct, a.TrackFadeInMs, a.TrackFadeOutMs, a.ResumeOnDeviceReturn));
        Assert.Equal(2, store.Warnings.Count(w => w.Contains("audio", StringComparison.OrdinalIgnoreCase) || w.Contains("volumen", StringComparison.OrdinalIgnoreCase) || w.Contains("fundido", StringComparison.OrdinalIgnoreCase)));   // y el aviso de la copia .bad
    }

    [Fact]
    public async Task A_device_id_with_control_characters_falls_back_to_the_windows_default()
    {
        var store = await LoadAsync("""{ "audio": { "churchDeviceId": "id\u0001roto", "churchDeviceName": "x", "masterVolumePct": 60 } }""");
        Assert.Null(store.Current.AudioConfig.ChurchDeviceId);
        Assert.Equal(60, store.Current.AudioConfig.MasterVolumePct);
        Assert.Contains(store.Warnings, w => w.Contains("dispositivo de audio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_audio_block_returns_to_the_defaults_with_a_warning_and_keeps_the_other_settings()
    {
        var store = await LoadAsync("""{ "autoRestoreOutput": false, "audio": { "masterVolumePct": "alto" } }""");
        Assert.False(store.Current.AutoRestoreOutput);
        Assert.Equal(new AudioSettings(), store.Current.AudioConfig);
        Assert.Contains(store.Warnings, w => w.Contains("configuración de audio", StringComparison.Ordinal));
    }
}
