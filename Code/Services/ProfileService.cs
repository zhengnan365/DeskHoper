using MonitorSwitch.Models;
using System.IO;

namespace MonitorSwitch.Services;

public sealed class ProfileService(string? filePath = null) : JsonFileService<List<SwitchProfile>>(filePath ?? DefaultPath)
{
    private List<SwitchProfile>? _cache;
    private static string DefaultPath => AppPaths.ProfilesFile;
    public async Task<IReadOnlyList<SwitchProfile>> LoadAsync(CancellationToken cancellationToken = default) => _cache ??= await base.LoadAsync([], cancellationToken);
    public async Task SaveAsync(IEnumerable<SwitchProfile> profiles, CancellationToken cancellationToken = default) { _cache = profiles.ToList(); await base.SaveAsync(_cache, cancellationToken); }
    public async Task RenameAsync(string oldName, SwitchProfile renamed, CancellationToken cancellationToken = default) { var profiles = (await LoadAsync(cancellationToken)).ToList(); var index = profiles.FindIndex(item => string.Equals(item.Name, oldName, StringComparison.Ordinal)); if (index < 0) return; profiles[index] = renamed; await SaveAsync(profiles, cancellationToken); }

    public async Task<IReadOnlyList<SwitchProfile>> LoadOrCreateDefaultsAsync(IReadOnlyCollection<MonitorDevice> monitors, CancellationToken cancellationToken = default)
    {
        var profiles = await LoadAsync(cancellationToken);
        if (profiles.Count > 0) return profiles;
        var ids = monitors.Where(m => m.StableId is not null).Select(m => m.Id).Take(2).ToArray();
        var defaults = new[]
        {
            new SwitchProfile { Name = "台式机", Monitors = ids.Select(id => new SwitchProfileMonitor { MonitorId = id, InputKind = InputSourceKind.DisplayPort, Input = 15 }).ToList(), ProjectionMode = ProjectionMode.Extend },
            new SwitchProfile { Name = "笔记本", Monitors = ids.Select(id => new SwitchProfileMonitor { MonitorId = id, InputKind = InputSourceKind.UsbC, Input = 16 }).ToList(), ProjectionMode = ProjectionMode.Extend }
        };
        await SaveAsync(defaults, cancellationToken);
        return defaults;
    }
}
