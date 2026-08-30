namespace MonitorSwitch.Models;

public sealed class MonitorPreferences
{
    public required string StableId { get; init; }
    public string? CustomName { get; init; }
    public MonitorInputMapping InputMappings { get; init; } = new();
}
