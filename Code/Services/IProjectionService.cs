using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public interface IProjectionService
{
    ProjectionMode? CurrentMode { get; }
    Task<ProjectionMode?> RefreshCurrentModeAsync(CancellationToken cancellationToken = default);
    Task<TopologySnapshot> GetTopologySnapshotAsync(CancellationToken cancellationToken = default);
    Task<ProjectionResult> ApplyAsync(ProjectionMode mode, CancellationToken cancellationToken = default);
}
