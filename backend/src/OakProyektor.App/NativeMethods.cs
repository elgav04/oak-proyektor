using System.Runtime.InteropServices;
using OakProyektor.Core.Output;

namespace OakProyektor.App;

/// <summary>Llamadas a Win32 que Photino no expone. Solo Windows (objetivo del proyecto).</summary>
internal static partial class NativeMethods
{
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    public static readonly nint HWND_TOPMOST = new(-1);
    public static readonly nint HWND_NOTOPMOST = new(-2);
    public const int GWL_EXSTYLE = -20;
    public const nint WS_EX_TOPMOST = 0x00000008;
    public const uint WM_CLOSE = 0x0010, WM_QUIT = 0x0012, WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    public const uint ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2, ES_CONTINUOUS = 0x80000000;
    public const uint PM_NOREMOVE = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int PtX; public int PtY; public uint Private; }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out MSG msg, nint hWnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG msg, nint hWnd, uint min, uint max);

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll")]
    public static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    public static partial uint SetThreadExecutionState(uint flags);

    private static readonly nint DpiContextPerMonitorAwareV2 = new(-4);

    /// <summary>
    /// Mientras exista, las llamadas Win32 de ESTE hilo usan coordenadas físicas. Necesario porque el proceso no es DPI-aware:
    /// sin esto GetWindowRect/SetWindowPos trabajan en coordenadas virtualizadas en un monitor escalado (p. ej. 125 %),
    /// mientras que Photino (y <c>DisplayInfo</c>) usan píxeles físicos. Verificado: 1440×740 (Photino) vs 1152×592 (Win32).
    /// </summary>
    public static PhysicalCoordinates UsePhysicalCoordinates() => new(SetThreadDpiAwarenessContext(DpiContextPerMonitorAwareV2));

    public readonly struct PhysicalCoordinates(nint previous) : IDisposable
    {
        public void Dispose()
        {
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    public static ScreenRect? GetRect(nint hwnd)
    {
        if (hwnd == 0) return null;
        using var _ = UsePhysicalCoordinates();
        return GetWindowRect(hwnd, out var r) ? new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
    }

    public static bool IsTopmost(nint hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>Quita topmost y minimiza. Seguro desde cualquier hilo (el sistema envía los mensajes al hilo dueño de la ventana).</summary>
    public static void MinimizeAndDropTopmost(nint hwnd)
    {
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        ShowWindow(hwnd, SW_MINIMIZE);
    }

    /// <summary>Restaura la ventana, la coloca exactamente en <paramref name="area"/> y la vuelve a poner topmost.</summary>
    public static void RestoreTopmostAt(nint hwnd, ScreenRect area)
    {
        using var _ = UsePhysicalCoordinates();
        ShowWindow(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, HWND_TOPMOST, area.X, area.Y, area.Width, area.Height, SWP_SHOWWINDOW | SWP_NOACTIVATE);
    }

    public static void SetTopmost(nint hwnd, bool topmost) =>
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}



