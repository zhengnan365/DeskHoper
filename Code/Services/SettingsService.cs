using MonitorSwitch.Models;
using System.IO;

namespace MonitorSwitch.Services;

public sealed class SettingsService(string? filePath = null) : JsonFileService<AppSettings>(filePath ?? DefaultPath)
{
    private static string DefaultPath => AppPaths.SettingsFile;
    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => base.LoadAsync(new AppSettings(), cancellationToken);
    public new Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => base.SaveAsync(settings, cancellationToken);
}
