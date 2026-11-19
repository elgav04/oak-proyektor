using System.Runtime.InteropServices;

namespace OakProyektor.App;

/// <summary>
/// Un fallo al arrancar sin ventana ni log útil dejaría al operador sin ninguna pista (la aplicación es WinExe: no tiene consola).
/// Se muestra un cuadro de diálogo con el motivo y dónde mirar. <c>OAK_NO_DIALOG=1</c> lo desactiva (pruebas automáticas).
/// </summary>
internal static partial class StartupFailure
{
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x0, MB_ICONERROR = 0x10, MB_TOPMOST = 0x40000;

    public static void Show(string message, string? logDirectory)
    {
        if (Environment.GetEnvironmentVariable("OAK_NO_DIALOG") == "1") return;
        var text = message + (logDirectory is null ? "" : $"\n\nDetalle en el registro:\n{logDirectory}");
        try { MessageBox(0, text, "Oak-Proyektor no pudo iniciar", MB_OK | MB_ICONERROR | MB_TOPMOST); }
        catch (Exception) { /* sin diálogo no hay más que hacer */ }
    }
}