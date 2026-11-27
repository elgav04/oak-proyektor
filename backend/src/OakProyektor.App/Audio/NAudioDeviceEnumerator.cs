using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using OakProyektor.Core.Audio;

namespace OakProyektor.App.Audio;

/// <summary>
/// Enumerador REAL de dispositivos de salida (NAudio / Core Audio de Windows). Escucha las notificaciones de Windows (<c>IMMNotificationClient</c>: dispositivo agregado, quitado, estado y
/// predeterminado cambiado) y, como RED DE SEGURIDAD, compara la lista cada 2 s por si una notificación no llega (USB y Bluetooth son justo donde fallan): cualquier diferencia dispara
/// <see cref="Changed"/>. Nunca lanza: ante un fallo devuelve lo último que supo (o vacío) y lo registra. El predeterminado es el del rol de consola, el que usa el navegador.
/// </summary>
internal sealed class NAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly ILogger<NAudioDeviceEnumerator> _logger;
    private readonly MMDeviceEnumerator? _notifier;
    private readonly MMDeviceNotificationClient? _client;
    private readonly Timer _poll;
    private readonly object _gate = new();
    private string _signature = "";
    private int _disposed;

    public NAudioDeviceEnumerator(ILogger<NAudioDeviceEnumerator> logger)
    {
        _logger = logger;
        try
        {
            _notifier = new MMDeviceEnumerator();
            _client = _notifier.CreateNotificationClient(false);   // sin contexto de sincronización: los eventos llegan en un hilo de Windows
            _client.DeviceAdded += (_, _) => Notify();
            _client.DeviceRemoved += (_, _) => Notify();
            _client.DeviceStateChanged += (_, _) => Notify();
            _client.DefaultDeviceChanged += (_, e) => { if (e.Flow == DataFlow.Render) Notify(); };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudieron registrar las notificaciones de dispositivos de audio; solo se comparará la lista cada 2 s");
        }
        _signature = ComputeSignature();
        _poll = new Timer(_ => Notify(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public event Action? Changed;

    private string ComputeSignature()
    {
        try { return string.Join('|', GetActiveDevices().Select(d => d.Id + "=" + d.Name).Append("default=" + GetDefaultDevice()?.Id)); }
        catch (Exception) { return "?"; }
    }

    private void Notify()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var signature = ComputeSignature();
            lock (_gate)
            {
                if (signature == _signature) return;
                _signature = signature;
            }
            Changed?.Invoke();
        }
        catch (Exception ex) { _logger.LogError(ex, "Falló el aviso de un cambio de dispositivos de audio"); }
    }

    public IReadOnlyList<AudioDeviceInfo> GetActiveDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var list = new List<AudioDeviceInfo>();
            foreach (var device in collection)
            {
                try { list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName)); }
                catch (Exception) { /* un dispositivo que desaparece mientras se lee */ }
                finally { device.Dispose(); }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer la lista de dispositivos de audio");
            return [];
        }
    }

    public AudioDeviceInfo? GetDefaultDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, NAudioBackend.DefaultRole, out var device) || device is null) return null;
            using (device) return new AudioDeviceInfo(device.ID, device.FriendlyName);
        }
        catch (Exception) { return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _poll.Dispose();
        try { _client?.Dispose(); } catch (Exception) { }
        try { _notifier?.Dispose(); } catch (Exception) { }
    }
}
