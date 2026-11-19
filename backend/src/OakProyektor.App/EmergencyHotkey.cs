using Microsoft.Extensions.Logging;

namespace OakProyektor.App;

/// <summary>
/// Atajo global de emergencia (Ctrl+Alt+Shift+M) con RegisterHotKey, independiente de qué ventana tenga el foco.
/// Vive en su propio hilo con su propio bucle de mensajes: Photino no expone su bucle, y RegisterHotKey con hWnd nulo
/// entrega WM_HOTKEY a la cola del hilo que lo registró. Falla (y se informa) si otra aplicación ya usa esa combinación.
/// </summary>
internal sealed class EmergencyHotkey(ILogger<EmergencyHotkey> logger) : IDisposable
{
    public const string DefaultDescription = "Ctrl+Alt+Shift+M";
    private const uint VkM = 0x4D;
    private const int HotkeyId = 0x0A4B;

    private Thread? _thread;
    private uint _threadId;

    /// <summary>La combinación registrada, o null si no se pudo registrar.</summary>
    public string? Description { get; private set; }

    /// <summary>Registra el atajo y espera el resultado (milisegundos). Llamar desde un hilo que no sea el de UI.</summary>
    public bool Start(Action onPressed)
    {
        if (_thread is not null) return Description is not null;

        using var ready = new ManualResetEventSlim(false);
        var registered = false;
        _thread = new Thread(() =>
        {
            try
            {
                _threadId = NativeMethods.GetCurrentThreadId();
                NativeMethods.PeekMessage(out _, 0, 0, 0, NativeMethods.PM_NOREMOVE);   // crea la cola de mensajes del hilo
                registered = NativeMethods.RegisterHotKey(0, HotkeyId,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, VkM);
                if (!registered) logger.LogWarning("No se pudo registrar el atajo de emergencia {Hotkey} (¿otra aplicación lo usa?)", DefaultDescription);
            }
            catch (Exception ex) { logger.LogError(ex, "Error registrando el atajo de emergencia"); }
            finally { ready.Set(); }

            if (!registered) return;
            try
            {
                while (NativeMethods.GetMessage(out var msg, 0, 0, 0) > 0)
                {
                    if (msg.Message != NativeMethods.WM_HOTKEY) continue;
                    try { onPressed(); }
                    catch (Exception ex) { logger.LogError(ex, "Falló la acción del atajo de emergencia"); }
                }
            }
            catch (Exception ex) { logger.LogError(ex, "El bucle del atajo de emergencia terminó con error"); }
            finally { NativeMethods.UnregisterHotKey(0, HotkeyId); }
        }) { IsBackground = true, Name = "EmergencyHotkey" };
        _thread.Start();

        ready.Wait(TimeSpan.FromSeconds(3));
        Description = registered ? DefaultDescription : null;
        if (registered) logger.LogInformation("Atajo de emergencia registrado: {Hotkey}", DefaultDescription);
        return registered;
    }

    public void Dispose()
    {
        var thread = _thread;
        if (thread is null) return;
        if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, 0, 0);
        thread.Join(TimeSpan.FromSeconds(1));
        _thread = null;
    }
}
