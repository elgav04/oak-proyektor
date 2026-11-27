// Medidor de picos de Windows para las pruebas reales (tools\media-playback-test.ps1 e i8-frozen-panel-test.ps1 -Full): COM directo, sin librerias de terceros.
// Peak(id) = pico del dispositivo ENTERO (incluye a otras aplicaciones); SessionPeak(id, pids) = pico SOLO del sonido de esos procesos (la aplicacion y su WebView2).
// El id vacio o nulo es el dispositivo predeterminado de Windows.
using System;
using System.Runtime.InteropServices;
public static class PeakMeter {
  [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom {}
  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDeviceEnumerator {
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
  }
  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDevice {
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    int OpenPropertyStore(int access, out IntPtr props);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out int state);
  }
  [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioMeterInformation {
    int GetPeakValue(out float peak);
  }
  static IMMDevice Dev(string id) {
    var e = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
    IMMDevice d;
    if (string.IsNullOrEmpty(id)) Marshal.ThrowExceptionForHR(e.GetDefaultAudioEndpoint(0, 0, out d)); else Marshal.ThrowExceptionForHR(e.GetDevice(id, out d));
    return d;
  }
  // ── Medidor por SESION de audio: solo cuenta el sonido de los procesos indicados (la aplicacion y su WebView2), no el de otras aplicaciones del equipo ──
  [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionManager2 {
    int GetAudioSessionControl(IntPtr g, int f, out IntPtr c);
    int GetSimpleAudioVolume(IntPtr g, int f, out IntPtr v);
    int GetSessionEnumerator(out IAudioSessionEnumerator e);
  }
  [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionEnumerator { int GetCount(out int n); int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object s); }
  [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionControl2 {
    int a0(); int a1(); int a2(); int a3(); int a4(); int a5(); int a6(); int a7(); int a8();
    int GetSessionIdentifier(IntPtr p); int GetSessionInstanceIdentifier(IntPtr p); int GetProcessId(out uint pid);
  }
  public static float SessionPeak(string id, int[] pids) {
    var iid = typeof(IAudioSessionManager2).GUID; object o;
    Marshal.ThrowExceptionForHR(Dev(id).Activate(ref iid, 23, IntPtr.Zero, out o));
    IAudioSessionEnumerator en; Marshal.ThrowExceptionForHR(((IAudioSessionManager2)o).GetSessionEnumerator(out en));
    int n; en.GetCount(out n); float max = 0;
    for (int i = 0; i < n; i++) {
      object s; if (en.GetSession(i, out s) != 0) continue;
      uint pid; if (((IAudioSessionControl2)s).GetProcessId(out pid) != 0) continue;
      if (Array.IndexOf(pids, (int)pid) < 0) continue;
      float p; if (((IAudioMeterInformation)s).GetPeakValue(out p) == 0 && p > max) max = p;
    }
    return max;
  }
  public static string Sessions(string id) {
    var iid = typeof(IAudioSessionManager2).GUID; object o;
    Marshal.ThrowExceptionForHR(Dev(id).Activate(ref iid, 23, IntPtr.Zero, out o));
    IAudioSessionEnumerator en; Marshal.ThrowExceptionForHR(((IAudioSessionManager2)o).GetSessionEnumerator(out en));
    int n; en.GetCount(out n); var sb = new System.Text.StringBuilder();
    for (int i = 0; i < n; i++) { object s; if (en.GetSession(i, out s) != 0) continue; uint pid; ((IAudioSessionControl2)s).GetProcessId(out pid); float p; ((IAudioMeterInformation)s).GetPeakValue(out p); sb.Append(pid + ":" + p.ToString("0.000") + " "); }
    return sb.ToString();
  }
  public static string DefaultId() { string id; Marshal.ThrowExceptionForHR(Dev(null).GetId(out id)); return id; }
  public static float Peak(string id) {
    var iid = typeof(IAudioMeterInformation).GUID; object o;
    Marshal.ThrowExceptionForHR(Dev(id).Activate(ref iid, 23, IntPtr.Zero, out o));
    float p; Marshal.ThrowExceptionForHR(((IAudioMeterInformation)o).GetPeakValue(out p)); return p;
  }
}
