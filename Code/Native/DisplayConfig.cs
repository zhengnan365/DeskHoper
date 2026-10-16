using System.Runtime.InteropServices;

namespace MonitorSwitch.Native;

internal static class DisplayConfig
{
    internal const uint QueryDatabaseCurrent = 0x00000004;
    internal const uint QueryOnlyActivePaths = 0x00000002;
    internal const uint PathIsActive = 0x00000001;
    internal const uint GetSourceName = 1;
    internal const uint GetTargetName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeInfoCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] pathInfoArray, ref uint modeInfoCount, [Out] ModeInfo[] modeInfoArray, out uint currentTopologyId);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "QueryDisplayConfig")]
    internal static extern int QueryActiveDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] pathInfoArray, ref uint modeInfoCount, [Out] ModeInfo[] modeInfoArray, nint currentTopologyId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName requestPacket);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo { public PathSourceInfo SourceInfo; public PathTargetInfo TargetInfo; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] Details;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader { public uint Type; public uint Size; public Luid AdapterId; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }
}
