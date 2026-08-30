namespace MonitorSwitch.Models;

public sealed class SwitchProfile
{
    public required string Name { get; init; }
    public List<SwitchProfileMonitor> Monitors { get; init; } = [];
    public ProjectionMode? ProjectionMode { get; init; }
}

public sealed class SwitchProfileMonitor
{
    public required string MonitorId { get; init; }
    public uint Input { get; init; }
    public InputSourceKind? InputKind { get; init; }
}
