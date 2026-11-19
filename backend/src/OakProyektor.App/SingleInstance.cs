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

internal enum InstanceOutcome
{
    /// <summary>Esta es la única instancia: arrancar con normalidad (<see cref="InstanceResult.Guard"/> la protege).</summary>
    Primary,
    /// <summary>Había otra instancia sana: se trajo al frente su ventana y esta debe salir.</summary>
    ExistingFocused,
    /// <summary>Hay otro proceso con el candado pero no tiene ventana ni lo suelta tras esperar: no se puede arrancar ni enfocar nada.</summary>
    ExistingUnresponsive,
}

internal sealed record InstanceResult(InstanceOutcome Outcome, SingleInstanceGuard? Guard);

/// <summary>
/// Decide qué hace una instancia recién lanzada (menor de la ronda 2). Casos:
/// <list type="bullet">
/// <item>No hay otra: <c>Primary</c>.</item>
/// <item>Hay otra con ventana (confirmada dos veces, por si se estaba cerrando): se enfoca y <c>ExistingFocused</c>.</item>
/// <item>Hay otra SIN ventana: puede estar arrancando (la ventana aparecerá) o CERRÁNDOSE (soltará el candado en unos segundos, el apagado
/// tiene un límite de 3 s). Se reintenta durante unos segundos y, en cuanto el candado queda libre, esta instancia ARRANCA con normalidad en
/// lugar de salir en silencio. Si pasado el tiempo sigue sin haber ventana ni candado libre: <c>ExistingUnresponsive</c> (el llamador lo muestra).</item>
/// </list>
/// Es SÍNCRONO a propósito: <c>Main</c> es <c>[STAThread]</c> y WebView2 exige STA; un <c>await</c> aquí devolvería la continuación a un hilo del
/// pool (MTA) y la ventana quedaría negra sin error. Solo bloquea cuando ya hay otra instancia (nunca en el arranque normal) y antes de crear ninguna ventana.
/// </summary>
internal static class SingleInstanceCoordinator
{
    /// <summary>32 intentos × 250 ms ≈ 8 s: más que el límite de apagado de la primera instancia (3 s).</summary>
    public const int DefaultAttempts = 32;
    public const int DefaultPollMs = 250;

    public static InstanceResult Acquire(Func<SingleInstanceGuard?> tryAcquire, Func<bool> bringToFront, int attempts, Action<int> sleep, int pollMs)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (tryAcquire() is { } guard) return new InstanceResult(InstanceOutcome.Primary, guard);

            // Con ventana: se enfoca y se CONFIRMA tras una pausa, porque una instancia que se está cerrando aún puede tenerla un instante.
            if (bringToFront())
            {
                sleep(pollMs);
                if (bringToFront()) return new InstanceResult(InstanceOutcome.ExistingFocused, null);
            }

            if (attempt >= attempts) return new InstanceResult(InstanceOutcome.ExistingUnresponsive, null);
            sleep(pollMs);
        }
    }
}

internal static class ExistingInstance
{
    /// <summary>
    /// Trae al frente la ventana de control de la otra instancia (restaurándola si está minimizada). Solo considera ventanas de un
    /// proceso que ejecuta ESTE mismo ejecutable (otra aplicación con el mismo título no cuenta). Un solo intento, sin esperas
    /// (<see cref="SingleInstanceCoordinator"/> decide cuánto reintentar). Devuelve true si la encontró.
    /// </summary>
    public static bool TryBringToFront(string windowTitle, string? exePath)
    {
        var hwnd = FindOtherInstanceWindow(windowTitle, exePath);
        if (hwnd == 0) return false;
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);
        return true;
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