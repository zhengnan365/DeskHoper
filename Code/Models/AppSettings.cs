namespace MonitorSwitch.Models;

public sealed record AppSettings
{
    public MonitorInputMapping DefaultInputMapping { get; init; } = new();
    public Dictionary<string, MonitorInputMapping> MonitorInputOverrides { get; init; } = new(StringComparer.Ordinal);
    public bool MinimizeToTrayOnClose { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool ShowSuccessNotifications { get; init; } = true;

    public MonitorInputMapping GetInputMapping(string monitorId) => MonitorInputOverrides.TryGetValue(monitorId, out var mapping) ? mapping : DefaultInputMapping;
}
