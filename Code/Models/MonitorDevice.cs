namespace MonitorSwitch.Models;

public sealed record MonitorDevice
{
    public required string Id { get; init; }
    public string? StableId { get; init; }
    public required string FriendlyName { get; init; }
    public string? CustomName { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? DevicePath { get; init; }
    public required string WindowsDisplayName { get; init; }
    public nint HMonitor { get; init; }
    public nint PhysicalMonitorHandle { get; init; }
    public uint? CurrentInput { get; init; }
    public bool IsDdcSupported { get; init; }
    public string DdcStatus { get; init; } = "未检测";
    public int DisplayNumber { get; init; }
    public DisplayBounds? Bounds { get; init; }
    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomName) ? (Model ?? FriendlyName) : CustomName.Trim();
    public string ModelSubtitle => string.IsNullOrWhiteSpace(CustomName) || string.Equals(CustomName.Trim(), Model?.Trim(), StringComparison.OrdinalIgnoreCase) ? string.Empty : Model ?? string.Empty;
}
