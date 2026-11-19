# Spike: identidad de monitores en Win32. Muestra, por monitor, el rectangulo y los candidatos a identificador.
# Uso:  powershell -File tools\monitor-ids.ps1            (una vez)
#       powershell -File tools\monitor-ids.ps1 -Watch     (cada 1 s; cambia la resolucion / el monitor principal y mira que el DevicePath NO cambie)
param([switch]$Watch)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class MonitorSpike
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

    public static List<string> Read()
    {
        var lines = new List<string>();
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));   // coordenadas fisicas, como en la aplicacion
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                GetMonitorInfo(h, ref info);
                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                // EDD_GET_DEVICE_INTERFACE_NAME (1): DeviceID pasa a ser la ruta de la interfaz del MONITOR (hardware + instancia).
                bool ok = EnumDisplayDevices(info.szDevice, 0, ref mon, 1);
                lines.Add(string.Format("{0}x{1} en ({2},{3}) principal={4} | GdiName={5} | DevicePath={6} | Nombre={7}",
                    r.Right - r.Left, r.Bottom - r.Top, r.Left, r.Top, (info.dwFlags & 1) != 0, info.szDevice,
                    ok ? mon.DeviceID : "(sin datos)", ok ? mon.DeviceString : ""));
                return true;
            }, IntPtr.Zero);
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        return lines;
    }
}
'@

do {
    Write-Host ("--- " + (Get-Date -Format 'HH:mm:ss'))
    [MonitorSpike]::Read() | ForEach-Object { Write-Host $_ }
    if ($Watch) { Start-Sleep -Seconds 1 }
} while ($Watch)
