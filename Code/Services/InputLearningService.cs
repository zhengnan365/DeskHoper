using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public sealed class InputLearningService(IDdcCiService ddc, MonitorPreferencesService preferences)
{
    public Task<InputLearningResult> LearnInputSourceAsync(string stableMonitorId, InputSourceKind inputKind, CancellationToken cancellationToken) => LearnInputSourceAsync(stableMonitorId, inputKind, false, cancellationToken);
    public async Task<InputLearningResult> LearnInputSourceAsync(string stableMonitorId, InputSourceKind inputKind, bool allowConflict = false, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var value = await ddc.ReadInputAsync(stableMonitorId, cancellationToken);
        if (value is null) return new InputLearningResult(false, stableMonitorId, inputKind, null, "无法读取当前 VCP 0x60 值。");
        var all = await preferences.LoadAsync(cancellationToken); var current = all.FirstOrDefault(item => item.StableId == stableMonitorId) ?? new MonitorPreferences { StableId = stableMonitorId };
        var mapping = current.InputMappings; var conflict = inputKind switch { InputSourceKind.DisplayPort => mapping.UsbC == value || mapping.Hdmi == value, InputSourceKind.UsbC => mapping.DisplayPort == value || mapping.Hdmi == value, _ => mapping.DisplayPort == value || mapping.UsbC == value };
        var updated = inputKind switch { InputSourceKind.DisplayPort => new MonitorInputMapping { DisplayPort = value.Value, UsbC = mapping.UsbC, Hdmi = mapping.Hdmi }, InputSourceKind.UsbC => new MonitorInputMapping { DisplayPort = mapping.DisplayPort, UsbC = value.Value, Hdmi = mapping.Hdmi }, _ => new MonitorInputMapping { DisplayPort = mapping.DisplayPort, UsbC = mapping.UsbC, Hdmi = value.Value } };
        if (conflict && !allowConflict) return new InputLearningResult(false, stableMonitorId, inputKind, value, "该值已经被分配给其他输入源。", true);
        await preferences.SetMappingAsync(stableMonitorId, updated, cancellationToken);
        _ = AppLogger.PerfAsync($"Input learning stableId={stableMonitorId} kind={inputKind} totalMs={stopwatch.ElapsedMilliseconds}");
        return new InputLearningResult(true, stableMonitorId, inputKind, value, conflict ? "已确认覆盖原输入映射。" : null, conflict);
    }
}
