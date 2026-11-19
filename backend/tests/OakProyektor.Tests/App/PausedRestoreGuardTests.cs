using System.Runtime.InteropServices;
using OakProyektor.App;

namespace OakProyektor.Tests.App;

/// <summary>
/// Prueba el mecanismo REAL (SetWindowSubclass) sobre una ventana nativa creada y minimizada en este mismo hilo: un SC_RESTORE se traga mientras está en
/// pausa y pasa cuando no. NO prueba la ruta exacta de la barra de tareas de Windows (eso es una prueba manual): solo que si llega ese mensaje, se intercepta.
/// Si no hay escritorio interactivo (CI sin sesión), no se puede crear la ventana y la prueba termina sin comprobar nada.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class PausedRestoreGuardTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hwnd, uint msg, nuint wParam, nint lParam);

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000;

    /// <summary>El guardia que devuelve <paramref name="test"/> se mantiene vivo HASTA DESPUÉS de destruir la ventana (DestroyWindow aún llama al subclass).</summary>
    private static void WithMinimizedWindow(Func<nint, PausedRestoreGuard> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            PausedRestoreGuard? keep = null;
            var hwnd = CreateWindowEx(0, "STATIC", "oak-test", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 0, 0, 200, 100, 0, 0, 0, 0);
            if (hwnd == 0) return;   // sin escritorio interactivo
            try
            {
                ShowWindow(hwnd, 6);   // SW_MINIMIZE
                Assert.True(IsIconic(hwnd));
                keep = test(hwnd);
            }
            catch (Exception ex) { failure = ex; }
            finally { DestroyWindow(hwnd); GC.KeepAlive(keep); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void A_restore_command_is_swallowed_while_paused_and_the_window_stays_minimized()
    {
        WithMinimizedWindow(hwnd =>
        {
            var guard = new PausedRestoreGuard(() => true);
            Assert.True(guard.Install(hwnd));

            SendMessage(hwnd, PausedRestoreGuard.WM_SYSCOMMAND, PausedRestoreGuard.SC_RESTORE, 0);
            Assert.True(IsIconic(hwnd));
            SendMessage(hwnd, PausedRestoreGuard.WM_SYSCOMMAND, PausedRestoreGuard.SC_RESTORE | 0x2, 0);   // los 4 bits bajos los usa el sistema
            Assert.True(IsIconic(hwnd));
            return guard;   // el delegado nativo vive mientras viva la ventana (en la aplicación lo guarda OutputController)
        });
    }

    [Fact]
    public void A_restore_command_goes_through_when_not_paused()
    {
        WithMinimizedWindow(hwnd =>
        {
            var guard = new PausedRestoreGuard(() => false);
            Assert.True(guard.Install(hwnd));

            SendMessage(hwnd, PausedRestoreGuard.WM_SYSCOMMAND, PausedRestoreGuard.SC_RESTORE, 0);
            Assert.False(IsIconic(hwnd));
            return guard;
        });
    }

    [Fact]
    public void Our_own_restore_through_ShowWindow_is_not_blocked_even_while_paused()
    {
        WithMinimizedWindow(hwnd =>
        {
            var guard = new PausedRestoreGuard(() => true);
            Assert.True(guard.Install(hwnd));
            ShowWindow(hwnd, 9);   // SW_RESTORE: lo que usa NativeMethods.RestoreTopmostAt al reanudar
            Assert.False(IsIconic(hwnd));
            return guard;
        });
    }

    [Fact]
    public void A_throwing_pause_check_never_escapes_and_lets_the_message_through()
    {
        WithMinimizedWindow(hwnd =>
        {
            var guard = new PausedRestoreGuard(() => throw new InvalidOperationException("x"));
            Assert.True(guard.Install(hwnd));
            SendMessage(hwnd, PausedRestoreGuard.WM_SYSCOMMAND, PausedRestoreGuard.SC_RESTORE, 0);
            Assert.False(IsIconic(hwnd));
            return guard;
        });
    }
}
