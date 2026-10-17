using MonitorSwitch.Models;
using System.IO;

namespace MonitorSwitch.Services;

public sealed class ProfileService(string? filePath = null) : JsonFileService<List<SwitchProfile>>(filePath ?? DefaultPath)
{
    private List<SwitchProfile>? _cache;
    private static string DefaultPath => AppPaths.ProfilesFile;
    public async Task<IReadOnlyList<SwitchProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null) return _cache;
        _cache = (await base.LoadAsync([], cancellationToken)).Select(Normalize).ToList();
        return _cache;
    }
    public async Task SaveAsync(IEnumerable<SwitchProfile> profiles, CancellationToken cancellationToken = default) { _cache = profiles.ToList(); await base.SaveAsync(_cache, cancellationToken); }
    public async Task RenameAsync(string oldName, SwitchProfile renamed, CancellationToken cancellationToken = default) { var profiles = (await LoadAsync(cancellationToken)).ToList(); var index = profiles.FindIndex(item => string.Equals(item.Name, oldName, StringComparison.Ordinal)); if (index < 0) return; profiles[index] = renamed; await SaveAsync(profiles, cancellationToken); }

    public async Task<IReadOnlyList<SwitchProfile>> LoadOrCreateDefaultsAsync(IReadOnlyCollection<MonitorDevice> monitors, CancellationToken cancellationToken = default)
    {
        var profiles = await LoadAsync(cancellationToken);
        if (profiles.Count > 0) return profiles;
        var ids = monitors.Where(m => m.StableId is not null).Select(m => m.Id).Take(2).ToArray();
        var defaults = new[]
        {
            new SwitchProfile { Id = "desktop", Name = "台式机", Monitors = ids.Select(id => new SwitchProfileMonitor { StableId = id, InputKind = InputSourceKind.DisplayPort, Input = 15 }).ToList(), ProjectionMode = ProjectionMode.Extend },
            new SwitchProfile { Id = "laptop", Name = "笔记本", Monitors = ids.Select(id => new SwitchProfileMonitor { StableId = id, InputKind = InputSourceKind.UsbC, Input = 16 }).ToList(), ProjectionMode = ProjectionMode.Extend }
        };
        await SaveAsync(defaults, cancellationToken);
        return defaults;
    }
    public async Task UpdateAsync(SwitchProfile profile, CancellationToken cancellationToken = default)
    {
        var profiles = (await LoadAsync(cancellationToken)).ToList();
        var index = profiles.FindIndex(item => item.Id == profile.Id);
        if (index >= 0) { profiles[index] = profile; await SaveAsync(profiles, cancellationToken); }
    }

    public async Task AddAsync(SwitchProfile profile, CancellationToken cancellationToken = default)
    {
        var profiles = (await LoadAsync(cancellationToken)).ToList();
        profiles.Add(profile);
        await SaveAsync(profiles, cancellationToken);
    }

    public async Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profiles = (await LoadAsync(cancellationToken)).ToList();
        profiles.RemoveAll(item => item.Id == profileId);
        await SaveAsync(profiles, cancellationToken);
    }
    private static SwitchProfile Normalize(SwitchProfile profile) => new()
    {
        Id = string.IsNullOrWhiteSpace(profile.Id) ? profile.Name switch { "台式机" => "desktop", "笔记本" => "laptop", _ => Guid.NewGuid().ToString("N") } : profile.Id,
        Name = profile.Name,
        ProjectionMode = profile.ProjectionMode,
        Monitors = profile.Monitors.Select(target => new SwitchProfileMonitor { MonitorId = target.MonitorId, StableId = target.StableId ?? (LooksLikeStableId(target.MonitorId) ? target.MonitorId : null), Input = target.Input, InputKind = target.InputKind }).ToList()
    };
    private static bool LooksLikeStableId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Count(character => character == '|') >= 2;
}
