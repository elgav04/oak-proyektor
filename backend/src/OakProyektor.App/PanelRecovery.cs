using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace OakProyektor.App;

/// <summary>
/// Recuperar un panel cuyo renderizador (el proceso que ejecuta su JavaScript) está atascado. Medido en este equipo (Photino.NET 4.0.16, WebView2 157):
/// <list type="bullet">
/// <item>Con un renderizador atascado, <c>Load()</c> NO recupera la página mientras el proceso siga vivo (con perfiles separados: 0 de 1 intentos en 20 s).</item>
/// <item>Terminar SOLO el proceso renderizador del panel y después <c>Load()</c> sí la recupera (≈ 2 s), sin tocar la salida ni la aplicación.</item>
/// <item>El panel y la salida tienen perfiles de WebView2 distintos (<see cref="WebViewProfile"/>): son navegadores distintos, así que el renderizador que se termina es
/// inequívocamente el del panel (su carpeta de perfil está en su línea de comandos) y la salida no puede verse afectada.</item>
/// </list>
/// Si no se puede identificar el renderizador (perfil predeterminado de Photino, línea de comandos ilegible), no se termina NADA y se recarga a secas.
/// </summary>
internal static class PanelRecovery
{
    /// <summary>
    /// ¿Es esta línea de comandos la de un proceso RENDERIZADOR de WebView2 cuyo perfil es <paramref name="userDataDir"/>? (función pura).
    /// WebView2 pasa <c>--user-data-dir=&lt;perfil&gt;\EBWebView</c>; se compara con la barra final para que <c>…\WebView2</c> no coincida con <c>…\WebView2-output</c>.
    /// </summary>
    public static bool IsRendererOfProfile(string? commandLine, string userDataDir)
    {
        if (string.IsNullOrEmpty(commandLine) || string.IsNullOrWhiteSpace(userDataDir)) return false;
        if (!commandLine.Contains("--type=renderer", StringComparison.Ordinal)) return false;
        var profile = Path.GetFullPath(userDataDir).TrimEnd('\\', '/') + "\\EBWebView";
        return commandLine.Contains(profile, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Termina los renderizadores del perfil dado (solo los del PANEL: el de la salida es otro perfil). Devuelve cuántos terminó.</summary>
    public static int KillRenderersOf(string userDataDir, ILogger logger)
    {
        var killed = 0;
        foreach (var process in Process.GetProcessesByName("msedgewebview2"))
        {
            using (process)
            {
                try
                {
                    if (!IsRendererOfProfile(GetCommandLine(process.Id), userDataDir)) continue;
                    process.Kill();
                    process.WaitForExit(2000);
                    killed++;
                    logger.LogWarning("Renderizador atascado del panel terminado (PID {Pid})", process.Id);
                }
                catch (Exception ex) { logger.LogWarning(ex, "No se pudo terminar el proceso {Pid}", process.Id); }
            }
        }
        return killed;
    }

    // ───────────── línea de comandos de otro proceso (NtQueryInformationProcess, ProcessCommandLineInformation = 60; Windows 8.1+) ─────────────

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass, nint buffer, int length, out int returnLength);

    internal static string? GetCommandLine(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == 0) return null;
        var buffer = nint.Zero;
        try
        {
            var size = 8192;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                buffer = Marshal.AllocHGlobal(size);
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out var needed);
                if (status == 0)
                {
                    // El resultado es un UNICODE_STRING (Length en bytes, MaximumLength, puntero) seguido del texto, todo dentro del mismo buffer.
                    var length = (ushort)Marshal.ReadInt16(buffer);
                    var text = Marshal.ReadIntPtr(buffer, nint.Size == 8 ? 8 : 4);
                    return text == 0 ? null : Marshal.PtrToStringUni(text, length / 2);
                }
                Marshal.FreeHGlobal(buffer); buffer = nint.Zero;
                if (needed <= size) return null;   // otro error (no un buffer corto)
                size = needed;
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            CloseHandle(handle);
        }
    }
}
