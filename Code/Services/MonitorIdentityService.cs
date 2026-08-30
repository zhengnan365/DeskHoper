using System.Management;
using System.Runtime.InteropServices;
using MonitorSwitch.Models;
using MonitorSwitch.Native;

namespace MonitorSwitch.Services;

public sealed class MonitorIdentityService
{
    private readonly Dictionary<string, MonitorHardwareIdentity> _identityCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;
    public void BeginScan() { _identityCache.Clear(); _cacheLoaded = false; }
    public MonitorHardwareIdentity? TryGetIdentity(string windowsDisplayName)
    {
        try
        {
            var devicePath = TryGetTargetDevicePath(windowsDisplayName);
            if (string.IsNullOrWhiteSpace(devicePath)) return null;
            var normalizedPath = NormalizeDevicePath(devicePath);
            EnsureWmiCache();
            foreach (var cached in _identityCache)
            {
                if (string.Equals(cached.Key, normalizedPath, StringComparison.OrdinalIgnoreCase) || cached.Key.StartsWith(normalizedPath + "_", StringComparison.OrdinalIgnoreCase)) return cached.Value with { DevicePath = devicePath };
            }
        }
        catch (Exception ex) { _ = AppLogger.ErrorAsync($"Monitor identity lookup failed for {windowsDisplayName}", ex); }
        return null;
    }

    private void EnsureWmiCache()
    {
        if (_cacheLoaded) return;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _cacheLoaded = true;
        using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT InstanceName, ManufacturerName, UserFriendlyName, SerialNumberID FROM WmiMonitorID");
        foreach (ManagementObject result in searcher.Get())
        {
            var instanceName = result["InstanceName"] as string;
            var manufacturer = DecodeChars(result["ManufacturerName"] as ushort[]);
            var model = DecodeChars(result["UserFriendlyName"] as ushort[]);
            var serial = DecodeChars(result["SerialNumberID"] as ushort[]);
            if (!string.IsNullOrWhiteSpace(instanceName) && !string.IsNullOrWhiteSpace(serial) && serial != "0") _identityCache[instanceName] = new MonitorHardwareIdentity($"{manufacturer}|{model}|{serial}", manufacturer, model, serial, string.Empty);
        }
        _ = AppLogger.PerfAsync($"WMI identity query totalMs={stopwatch.ElapsedMilliseconds} records={_identityCache.Count}");
    }

    private static string? TryGetTargetDevicePath(string windowsDisplayName)
    {
        if (DisplayConfig.GetDisplayConfigBufferSizes(DisplayConfig.QueryOnlyActivePaths, out var pathCount, out var modeCount) != 0) return null;
        var paths = new DisplayConfig.PathInfo[pathCount];
        var modes = Enumerable.Range(0, (int)modeCount).Select(_ => new DisplayConfig.ModeInfo { Details = new byte[48] }).ToArray();
        if (DisplayConfig.QueryActiveDisplayConfig(DisplayConfig.QueryOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0) != 0) return null;
        foreach (var path in paths.Take((int)pathCount))
        {
            var source = new DisplayConfig.SourceDeviceName
            {
                Header = new DisplayConfig.DeviceInfoHeader { Type = DisplayConfig.GetSourceName, Size = (uint)Marshal.SizeOf<DisplayConfig.SourceDeviceName>(), AdapterId = path.SourceInfo.AdapterId, Id = path.SourceInfo.Id },
                ViewGdiDeviceName = string.Empty
            };
            if (DisplayConfig.DisplayConfigGetDeviceInfo(ref source) != 0 || !string.Equals(source.ViewGdiDeviceName, windowsDisplayName, StringComparison.OrdinalIgnoreCase)) continue;
            var target = new DisplayConfig.TargetDeviceName
            {
                Header = new DisplayConfig.DeviceInfoHeader { Type = DisplayConfig.GetTargetName, Size = (uint)Marshal.SizeOf<DisplayConfig.TargetDeviceName>(), AdapterId = path.TargetInfo.AdapterId, Id = path.TargetInfo.Id },
                MonitorFriendlyDeviceName = string.Empty,
                MonitorDevicePath = string.Empty
            };
            return DisplayConfig.DisplayConfigGetDeviceInfo(ref target) == 0 ? target.MonitorDevicePath : null;
        }
        return null;
    }

    private static string NormalizeDevicePath(string devicePath)
    {
        var instance = devicePath.StartsWith("\\\\?\\", StringComparison.Ordinal) ? devicePath[4..] : devicePath;
        var interfaceDelimiter = instance.IndexOf("#{", StringComparison.Ordinal);
        if (interfaceDelimiter >= 0) instance = instance[..interfaceDelimiter];
        return instance.Replace('#', '\\');
    }

    private static string DecodeChars(ushort[]? values) => values is null ? string.Empty : new string(values.TakeWhile(value => value != 0).Select(value => (char)value).ToArray()).Trim();
}
