using System.IO;

namespace MonitorSwitch.Services;

public static class AppPaths
{
    public static string ConfigDirectory => Path.Combine(AppContext.BaseDirectory, "MonitorSwitcher-config");
    public static string LogsDirectory => Path.Combine(ConfigDirectory, "logs");
    public static string ProfilesFile => Path.Combine(ConfigDirectory, "profiles.json");
    public static string MonitorsFile => Path.Combine(ConfigDirectory, "monitors.json");
    public static string PeerFile => Path.Combine(ConfigDirectory, "peer.json");
    public static string ReceivedFilesDirectory => Path.Combine(ConfigDirectory, "接收文件");
    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
}
