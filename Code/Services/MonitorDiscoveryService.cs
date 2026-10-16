using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public sealed class MonitorDiscoveryService(IDdcCiService ddcCiService)
{
    public Task<IReadOnlyList<MonitorDevice>> ScanAsync(IProgress<MonitorDevice>? progress = null, CancellationToken cancellationToken = default) => ddcCiService.RefreshAsync(progress, cancellationToken);
}
