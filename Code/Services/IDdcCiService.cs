using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public interface IDdcCiService : IAsyncDisposable
{
    Task<IReadOnlyList<MonitorDevice>> RefreshAsync(CancellationToken cancellationToken = default);
    Task<uint?> ReadInputAsync(string monitorId, CancellationToken cancellationToken = default);
    Task<DdcOperationResult> SetInputAsync(string monitorId, uint inputValue, CancellationToken cancellationToken = default);
}
