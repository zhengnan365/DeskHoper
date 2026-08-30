namespace MonitorSwitch.Models;

public sealed record MonitorIdentificationTarget(int DisplayNumber, string StableId, string WindowsDisplayName, DisplayBounds? Bounds);
