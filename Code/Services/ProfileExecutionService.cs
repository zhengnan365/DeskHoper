using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public sealed class ProfileExecutionService(IDdcCiService ddcCiService, IProjectionService projectionService, MonitorPreferencesService? preferencesService = null)
{
    public const int DisplayReconnectDelayMs = 750;

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
            if (profile.Monitors.Count > 0) await Task.Delay(TimeSpan.FromMilliseconds(DisplayReconnectDelayMs), cancellationToken);
            projectionResult = await projectionService.ApplyAsync(mode, cancellationToken);
        }
        _ = AppLogger.PerfAsync($"Profile switch name={profile.Name} totalMs={stopwatch.ElapsedMilliseconds}");
        return new ProfileExecutionResult(inputResults, projectionResult);
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
