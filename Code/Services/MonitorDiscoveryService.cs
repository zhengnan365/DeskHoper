using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public sealed class MonitorDiscoveryService(IDdcCiService ddcCiService)
{
    public Task<IReadOnlyList<MonitorDevice>> ScanAsync(CancellationToken cancellationToken = default) => ddcCiService.RefreshAsync(cancellationToken);
}
