using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OakProyektor.App;

/// <summary>
/// Instancia única (hallazgo I11). Un segundo proceso sobre la misma carpeta de datos significaría dos salidas sobre el proyector y dos
/// escritores en el mismo log y la misma BD. Es un Mutex con nombre que NO se toma (no hay propietario, así que no depende del hilo que
/// lo creó): basta con que el objeto exista mientras el proceso viva; el sistema lo libera solo si el proceso muere.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// El nombre depende de la carpeta de datos: dos aplicaciones que comparten datos chocan; una instalada y otra de desarrollo
    /// (carpetas distintas) pueden convivir. <c>Local\</c> = por sesión de usuario.
    /// </summary>
    public static string NameFor(string dataDirectory)
    {
        var normalized = Path.GetFullPath(dataDirectory).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        return $@"Local\Oak-Proyektor-{hash}";
    }

    /// <returns>El guardián si esta es la primera instancia; null si ya hay otra.</returns>
    public static SingleInstanceGuard? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        if (createdNew) return new SingleInstanceGuard(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}

internal static class ExistingInstance
{
    /// <summary>
    /// Trae al frente la ventana de control de la otra instancia (restaurándola si está minimizada). Solo considera ventanas de un
    /// proceso que ejecuta ESTE mismo ejecutable (otra aplicación con el mismo título no cuenta). Reintenta unos instantes por si la
    /// primera instancia aún está creando su ventana. Devuelve true si la encontró.
    /// </summary>
    public static async Task<bool> TryBringToFrontAsync(string windowTitle, string? exePath, int attempts = 15, int delayMs = 200)
    {
        for (var i = 0; i < attempts; i++)
        {
            var hwnd = FindOtherInstanceWindow(windowTitle, exePath);
            if (hwnd != 0)
            {
                if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
                return true;
            }
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
        return false;
    }

    private static nint FindOtherInstanceWindow(string windowTitle, string? exePath)
    {
        var current = Environment.ProcessId;
        nint hwnd = 0;
        while ((hwnd = NativeMethods.FindWindowEx(0, hwnd, null, windowTitle)) != 0)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == (uint)current) continue;
            try
            {
                var path = Process.GetProcessById((int)pid).MainModule?.FileName;
                if (exePath is null || string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase)) return hwnd;
            }
            catch (Exception) { /* proceso ya terminado o sin acceso: no es el que buscamos */ }
        }
        return 0;
    }
}