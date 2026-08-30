namespace MonitorSwitch.Models;

public sealed record MonitorHardwareIdentity(string StableId, string Manufacturer, string Model, string SerialNumber, string DevicePath);
