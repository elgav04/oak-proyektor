using Microsoft.Extensions.Logging;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App;

/// <param name="Action">Nombre estable de la acción (<c>emergency</c>, <c>black</c>, <c>clearText</c>, <c>logo</c>, <c>reloadPanel</c>).</param>
/// <param name="Key">Tecla virtual; el modificador es siempre Ctrl+Alt+Shift (una combinación que nadie pulsa sin querer).</param>
internal sealed record HotkeyBinding(string Action, uint Key, string Keys, Action OnPressed);

/// <summary>
/// Atajos globales con <c>RegisterHotKey</c>, independientes de qué ventana tenga el foco Y DEL PANEL: no pasan por el JavaScript, así que funcionan
/// aunque su página esté atascada. Todos viven en UN hilo propio con su propio bucle de mensajes (Photino no expone el suyo; <c>RegisterHotKey</c>
/// con <c>hWnd</c> nulo entrega <c>WM_HOTKEY</c> a la cola del hilo que lo registró). Cada atajo se registra por separado: si otra aplicación ya usa
/// una combinación, solo ese falla y se informa (el panel avisa), los demás siguen. Las acciones se ejecutan en el hilo del bucle: deben ser rápidas
/// y capturar sus excepciones (aquí se capturan también); lo lento se lanza aparte.
/// </summary>
internal sealed class GlobalHotkeys(ILogger<GlobalHotkeys> logger) : IDisposable
{
    public const uint ModifierAll = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
    private const int FirstId = 0x0A4B;

    private Thread? _thread;
    private uint _threadId;

    /// <summary>Registra los atajos y espera el resultado (milisegundos). Llamar desde un hilo que no sea el de UI. Devuelve el estado de cada uno, en orden.</summary>
    public IReadOnlyList<HotkeyStatus> Start(IReadOnlyList<HotkeyBinding> bindings)
    {
        if (_thread is not null) return [];

        var statuses = new bool[bindings.Count];
        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            var registeredAny = false;
            try
            {
                _threadId = NativeMethods.GetCurrentThreadId();
                NativeMethods.PeekMessage(out _, 0, 0, 0, NativeMethods.PM_NOREMOVE);   // crea la cola de mensajes del hilo
                for (var i = 0; i < bindings.Count; i++)
                {
                    statuses[i] = NativeMethods.RegisterHotKey(0, FirstId + i, ModifierAll, bindings[i].Key);
                    registeredAny |= statuses[i];
                    if (!statuses[i]) logger.LogWarning("No se pudo registrar el atajo {Action} ({Keys}); ¿otra aplicación lo usa?", bindings[i].Action, bindings[i].Keys);
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Error registrando los atajos globales"); }
            finally { ready.Set(); }

            if (!registeredAny) return;
            try
            {
                while (NativeMethods.GetMessage(out var msg, 0, 0, 0) > 0)
                {
                    if (msg.Message != NativeMethods.WM_HOTKEY) continue;
                    var index = (int)msg.WParam - FirstId;
                    if (index < 0 || index >= bindings.Count) continue;
                    try { bindings[index].OnPressed(); }
                    catch (Exception ex) { logger.LogError(ex, "Falló la acción del atajo {Action}", bindings[index].Action); }
                }
            }
            catch (Exception ex) { logger.LogError(ex, "El bucle de los atajos globales terminó con error"); }
            finally
            {
                for (var i = 0; i < bindings.Count; i++) if (statuses[i]) NativeMethods.UnregisterHotKey(0, FirstId + i);
            }
        }) { IsBackground = true, Name = "GlobalHotkeys" };
        _thread.Start();

        ready.Wait(TimeSpan.FromSeconds(3));
        var result = bindings.Select((b, i) => new HotkeyStatus(b.Action, b.Keys, statuses[i])).ToList();
        foreach (var s in result.Where(s => s.Registered)) logger.LogInformation("Atajo global registrado: {Action} = {Keys}", s.Action, s.Keys);
        return result;
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
