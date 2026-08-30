using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public interface IMonitorIdentificationPresenter : IDisposable
{
    Task ShowAsync(IReadOnlyList<MonitorIdentificationTarget> targets, TimeSpan duration, CancellationToken cancellationToken = default);
}
