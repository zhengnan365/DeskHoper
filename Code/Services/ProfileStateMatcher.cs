using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public static class ProfileStateMatcher
{
    public static bool Matches(SwitchProfile profile, IReadOnlyDictionary<string, uint?> currentInputs, IReadOnlyDictionary<string, MonitorInputMapping> mappings)
    {
        return profile.Monitors.Count > 0 && profile.Monitors.All(target =>
        {
            if (!currentInputs.TryGetValue(target.TargetId, out var current) || current is null) return false;
            if (target.InputKind is not { } kind || !mappings.TryGetValue(target.TargetId, out var mapping)) return current == target.Input;
            return current == Resolve(kind, mapping, target.Input);
        });
    }

    private static uint Resolve(InputSourceKind kind, MonitorInputMapping mapping, uint legacyValue) => kind switch
    {
        InputSourceKind.DisplayPort => mapping.DisplayPort,
        InputSourceKind.UsbC => mapping.UsbC,
        InputSourceKind.Hdmi when mapping.Hdmi is { } value => value,
        _ => legacyValue
    };
}
