namespace MonitorSwitch.Models;

public sealed class SwitchProfile
{
    public string? Id { get; init; }
    public required string Name { get; init; }
    public List<SwitchProfileMonitor> Monitors { get; init; } = [];
    public ProjectionMode? ProjectionMode { get; init; }
}

public sealed class SwitchProfileMonitor
{
    public string? MonitorId { get; init; }
    public string? StableId { get; init; }
    public uint Input { get; init; }
    public InputSourceKind? InputKind { get; init; }
    public string TargetId => StableId ?? MonitorId ?? string.Empty;
}
