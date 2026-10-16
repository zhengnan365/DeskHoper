using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public sealed class ProfileExecutionService(IDdcCiService ddcCiService, IProjectionService projectionService, MonitorPreferencesService? preferencesService = null)
{
    public const int MaxDisplayReconnectWaitMs = 750;
    private const int DisplaySettleFloorMs = 200;
    private const int DisplaySettlePollMs = 60;
    private const int DisplaySettleStableReads = 2;

    public async Task<ProfileExecutionResult> ApplyAsync(SwitchProfile profile, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var inputResults = new Dictionary<string, DdcOperationResult>();
        foreach (var monitor in profile.Monitors)
        {
            try
            {
                var inputValue = await ResolveInputValueAsync(monitor, cancellationToken);
                inputResults[monitor.TargetId] = await ddcCiService.SetInputAsync(monitor.TargetId, inputValue, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch { inputResults[monitor.TargetId] = new DdcOperationResult(false, 0, 0, TimeSpan.Zero); }
        }
        ProjectionResult? projectionResult = null;
        if (profile.ProjectionMode is { } mode && mode != ProjectionMode.Unchanged)
        {
            if (profile.Monitors.Count > 0) await WaitForDisplaySettledAsync(cancellationToken);
            projectionResult = await projectionService.ApplyAsync(mode, cancellationToken);
        }
        _ = AppLogger.PerfAsync($"Profile switch name={profile.Name} totalMs={stopwatch.ElapsedMilliseconds}");
        return new ProfileExecutionResult(inputResults, projectionResult);
    }

    private async Task WaitForDisplaySettledAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var previous = await projectionService.GetTopologySnapshotAsync(cancellationToken);
        var stableReads = 0;
        while (stopwatch.ElapsedMilliseconds < MaxDisplayReconnectWaitMs && stableReads < DisplaySettleStableReads)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(DisplaySettlePollMs), cancellationToken);
            var current = await projectionService.GetTopologySnapshotAsync(cancellationToken);
            stableReads = current == previous ? stableReads + 1 : 0;
            previous = current;
        }
        if (stopwatch.ElapsedMilliseconds < DisplaySettleFloorMs) await Task.Delay(TimeSpan.FromMilliseconds(DisplaySettleFloorMs - stopwatch.ElapsedMilliseconds), cancellationToken);
        _ = AppLogger.PerfAsync($"Display settle waitMs={stopwatch.ElapsedMilliseconds} snapshot={previous}");
    }

    private async Task<uint> ResolveInputValueAsync(SwitchProfileMonitor target, CancellationToken cancellationToken)
    {
        if (target.InputKind is not { } kind || preferencesService is null) return target.Input;
        var preferences = await preferencesService.LoadAsync(cancellationToken);
        var mapping = preferences.FirstOrDefault(item => item.StableId == target.TargetId)?.InputMappings ?? new MonitorInputMapping();
        return kind switch
        {
            InputSourceKind.DisplayPort => mapping.DisplayPort,
            InputSourceKind.UsbC => mapping.UsbC,
            InputSourceKind.Hdmi when mapping.Hdmi is { } value => value,
            _ => target.Input
        };
    }
}

public sealed record ProfileExecutionResult(IReadOnlyDictionary<string, DdcOperationResult> InputResults, ProjectionResult? ProjectionResult);
