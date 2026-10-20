using System.Runtime.InteropServices;

namespace MonitorSwitch.Native;

internal static class User32
{
    internal const int CchDeviceName = 32;
    internal const uint MonitorInfoFPrimary = 0x00000001;
    internal const int WmClipboardUpdate = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(nint hwnd);

    internal delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, nint lprcMonitor, nint dwData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc callback, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfoEx monitorInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
}
