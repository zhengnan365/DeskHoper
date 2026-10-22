using Microsoft.Win32;

namespace MonitorSwitch.Services;

public sealed class StartupService
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "DeskHoper";
    public bool IsEnabled => ReadCommand() is not null;
    public bool SetEnabled(bool enabled, string? executablePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key?.SetValue(ValueName, $"\"{executablePath ?? Environment.ProcessPath}\""); else key?.DeleteValue(ValueName, false);
            return IsEnabled == enabled;
        }
        catch (Exception ex) { _ = AppLogger.ErrorAsync("Startup setting failed", ex); return false; }
    }
    private static string? ReadCommand() { try { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(ValueName) as string; } catch { return null; } }
}
