using System.Runtime.InteropServices;

namespace OakProyektor.App;

/// <summary>
/// Intercepta <c>WM_SYSCOMMAND</c> con <c>SC_RESTORE</c> en la ventana de salida MIENTRAS está en pausa y se lo traga: así restaurarla desde la barra de
/// tareas no la hace aparecer un instante antes de que el sondeo de 500 ms la vuelva a minimizar (parpadeo, sobre todo en modo Duplicar, donde taparía el panel).
/// Se instala con <c>SetWindowSubclass</c> y DEBE instalarse desde el hilo que posee la ventana (el de UI). Nuestras propias restauraciones usan
/// <c>ShowWindow</c>/<c>SetWindowPos</c>, que no pasan por aquí. Si algo falla, el mensaje sigue su camino normal (el sondeo es la red de seguridad).
/// </summary>
internal sealed class PausedRestoreGuard
{
    public const uint WM_SYSCOMMAND = 0x0112, WM_NCDESTROY = 0x0082;
    public const nuint SC_RESTORE = 0xF120;
    private const nuint SubclassId = 0x4F414B;   // "OAK"

    private delegate nint SubclassProc(nint hWnd, uint msg, nuint wParam, nint lParam, nuint idSubclass, nuint refData);

    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nuint idSubclass, nuint refData);

    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc proc, nuint idSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    private readonly Func<bool> _isPaused;
    private readonly Action<string>? _log;
    private readonly SubclassProc _proc;   // referencia fuerte: el delegado no puede recolectarse mientras exista la ventana

    public PausedRestoreGuard(Func<bool> isPaused, Action<string>? log = null)
    {
        _isPaused = isPaused;
        _log = log;
        _proc = Callback;
    }

    /// <summary>Solo desde el hilo dueño de la ventana. Devuelve false si no se pudo instalar (la política sigue funcionando por sondeo).</summary>
    public bool Install(nint hwnd)
    {
        try { return hwnd != 0 && SetWindowSubclass(hwnd, _proc, SubclassId, 0); }
        catch (Exception ex) { _log?.Invoke($"No se pudo instalar el guardia de restauración: {ex.Message}"); return false; }
    }

    private nint Callback(nint hWnd, uint msg, nuint wParam, nint lParam, nuint idSubclass, nuint refData)
    {
        try
        {
            // La ventana se destruye: se retira el subclass AHORA (patrón estándar de comctl32), para que nunca quede una llamada nativa pendiente
            // hacia un delegado que el recolector ya liberó.
            if (msg == WM_NCDESTROY) { RemoveWindowSubclass(hWnd, _proc, SubclassId); return DefSubclassProc(hWnd, msg, wParam, lParam); }

            // Los 4 bits bajos de wParam los usa el sistema: se enmascaran.
            if (msg == WM_SYSCOMMAND && (wParam & 0xFFF0) == SC_RESTORE && _isPaused()) return 0;
        }
        catch { /* nunca propagar a código nativo; ante la duda, el mensaje sigue */ }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
