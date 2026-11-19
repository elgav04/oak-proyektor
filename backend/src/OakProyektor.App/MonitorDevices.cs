using System.Runtime.InteropServices;
using OakProyektor.Core.Display;
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

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0, ERROR_INSUFFICIENT_BUFFER = 122;
    // Tamaños fijos de Win32 (x64 y x86): DISPLAYCONFIG_PATH_INFO = 72 bytes, DISPLAYCONFIG_MODE_INFO = 64 bytes.
    // En PATH_INFO, sourceInfo empieza en el byte 0: adapterId (LUID, 8 bytes) + id de la fuente (uint, 4 bytes) = los primeros 12 bytes identifican la fuente.
    private const int PathInfoSize = 72, ModeInfoSize = 64, SourceKeyBytes = 12;

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, byte[] paths, ref uint numModes, byte[] modes, nint topologyId);
#pragma warning restore SYSLIB1054

    /// <summary>
    /// ¿Está Windows en modo «Duplicar» (Win+P)? Sí si dos caminos de pantalla ACTIVOS comparten la misma fuente (<see cref="DisplayTopology"/>).
    /// Nunca lanza: ante cualquier fallo devuelve false (no se afirma «Duplicar» sin estar seguro; el modo normal es el seguro para no pausar sin motivo).
    /// </summary>
    public static bool ReadDuplicateMode() => DisplayTopology.IsDuplicate(ReadActiveSourceKeys());

    public static IReadOnlyList<string> ReadActiveSourceKeys()
    {
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)   // la configuración puede cambiar entre pedir el tamaño y leer
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != ERROR_SUCCESS) return [];
                var paths = new byte[checked((int)pathCount * PathInfoSize)];
                var modes = new byte[checked((int)modeCount * ModeInfoSize)];
                var result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, 0);
                if (result == ERROR_INSUFFICIENT_BUFFER) continue;
                if (result != ERROR_SUCCESS) return [];

                var keys = new List<string>((int)pathCount);
                for (var i = 0; i < pathCount; i++)
                    keys.Add(Convert.ToHexString(paths, i * PathInfoSize, SourceKeyBytes));
                return keys;
            }
        }
        catch { /* sin información: se asume que no es Duplicar */ }
        return [];
    }

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
