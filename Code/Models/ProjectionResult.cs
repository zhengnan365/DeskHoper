namespace MonitorSwitch.Models;

public sealed record ProjectionResult(bool Succeeded, ProjectionMode? AppliedMode = null, string? ErrorMessage = null)
{
    public static ProjectionResult NoChange { get; } = new(true);
}
