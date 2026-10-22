namespace MonitorSwitch.Models;

public sealed record AppSettings
{
    public MonitorInputMapping DefaultInputMapping { get; init; } = new();
    public Dictionary<string, MonitorInputMapping> MonitorInputOverrides { get; init; } = new(StringComparer.Ordinal);
    /// <summary>第一次点关闭时问过一次没有；没问过就走弹框，问过就按 MinimizeToTrayOnClose 直接办。</summary>
    public bool CloseActionDecided { get; init; }
    public bool MinimizeToTrayOnClose { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool ShowSuccessNotifications { get; init; } = true;

    public MonitorInputMapping GetInputMapping(string monitorId) => MonitorInputOverrides.TryGetValue(monitorId, out var mapping) ? mapping : DefaultInputMapping;
}
