using System.Runtime.InteropServices;
using OakProyektor.Core.Output;

namespace OakProyektor.App;

/// <summary>
/// Ruta de dispositivo de cada monitor físico (Win32), para identificarlo por algo que no cambie con la resolución (ver <c>DisplayId</c>).
/// Photino solo da el rectángulo; aquí se enumera con <c>EnumDisplayMonitors</c> + <c>GetMonitorInfo</c> (nombre GDI) y
/// <c>EnumDisplayDevices(nombreGDI, 0, EDD_GET_DEVICE_INTERFACE_NAME)</c>, cuyo <c>DeviceID</c> es la ruta de la interfaz del MONITOR.
/// Se empareja con los monitores de Photino por rectángulo (ambos en píxeles físicos). Nunca lanza: si algo falla, devuelve vacío y el ID cae al rectángulo.
/// </summary>
internal static class MonitorDevices
{
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public NativeMethods.RECT rcMonitor;
        public NativeMethods.RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref NativeMethods.RECT rect, nint data);

#pragma warning disable SYSLIB1054 // estructuras con cadenas de tamaño fijo: DllImport clásico es lo más simple y verificado
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
#pragma warning restore SYSLIB1054

    /// <summary>Rectángulo (físico) → ruta de dispositivo del monitor. Solo válido desde un hilo que pueda llamar a user32 (cualquiera).</summary>
    public static IReadOnlyDictionary<ScreenRect, string> Read()
    {
        var result = new Dictionary<ScreenRect, string>();
        try
        {
            using var _ = NativeMethods.UsePhysicalCoordinates();   // sin esto, en monitores escalados los rectángulos no coinciden con los de Photino
            EnumDisplayMonitors(0, 0, (nint handle, nint hdc, ref NativeMethods.RECT rect, nint data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(handle, ref info)) return true;
                var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (EnumDisplayDevices(info.szDevice, 0, ref device, EDD_GET_DEVICE_INTERFACE_NAME) && !string.IsNullOrWhiteSpace(device.DeviceID))
                    result[new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)] = device.DeviceID;
                return true;
            }, 0);
        }
        catch { /* sin ruta de dispositivo: se identifica por rectángulo */ }
        return result;
    }
}
