namespace MonitorSwitch.Models;

public enum ProjectionMode
{
    Unchanged,
    Internal,
    Duplicate,
    Extend,
    External
}

public readonly record struct TopologySnapshot(ProjectionMode? Mode, int ActivePathCount)
{
    public static TopologySnapshot Unknown { get; } = new(null, 0);
}
