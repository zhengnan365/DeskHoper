using MonitorSwitch.Models;
using System.IO;

namespace MonitorSwitch.Services;

public sealed class MonitorPreferencesService(string? filePath = null) : JsonFileService<List<MonitorPreferences>>(filePath ?? DefaultPath)
{
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private Dictionary<string, MonitorPreferences>? _cache;
    private static string DefaultPath => AppPaths.MonitorsFile;
    public async Task<IReadOnlyList<MonitorPreferences>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null) return _cache.Values.ToArray();
        await _cacheGate.WaitAsync(cancellationToken);
        try { if (_cache is null) _cache = (await base.LoadAsync([], cancellationToken)).Where(item => !string.IsNullOrWhiteSpace(item.StableId)).ToDictionary(item => item.StableId, StringComparer.Ordinal); return _cache.Values.ToArray(); }
        finally { _cacheGate.Release(); }
    }
    public async Task SaveAsync(IEnumerable<MonitorPreferences> preferences, CancellationToken cancellationToken = default)
    {
        var list = preferences.Where(item => !string.IsNullOrWhiteSpace(item.StableId)).ToList();
        await base.SaveAsync(list, cancellationToken);
        _cache = list.ToDictionary(item => item.StableId, StringComparer.Ordinal);
    }
    public async Task<MonitorPreferences> GetOrCreateAsync(string stableId, string? manufacturer, string? model, CancellationToken cancellationToken = default)
    {
        var all = (await LoadAsync(cancellationToken)).ToList();
        var current = all.FirstOrDefault(item => string.Equals(item.StableId, stableId, StringComparison.Ordinal));
        if (current is not null) return current;
        var mapping = IsAoc(model) ? new MonitorInputMapping() : new MonitorInputMapping { DisplayPort = 0, UsbC = 0, Hdmi = null };
        current = new MonitorPreferences { StableId = stableId, InputMappings = mapping };
        all.Add(current); await SaveAsync(all, cancellationToken); return current;
    }
    public async Task RenameAsync(string stableId, string? customName, CancellationToken cancellationToken = default)
    {
        var all = (await LoadAsync(cancellationToken)).ToList(); var current = all.FirstOrDefault(item => item.StableId == stableId);
        if (current is null) { all.Add(new MonitorPreferences { StableId = stableId, CustomName = NormalizeName(customName) }); }
        else { var index = all.IndexOf(current); all[index] = new MonitorPreferences { StableId = current.StableId, CustomName = NormalizeName(customName), InputMappings = current.InputMappings }; }
        await SaveAsync(all, cancellationToken);
    }
    public async Task SetMappingAsync(string stableId, MonitorInputMapping mapping, CancellationToken cancellationToken = default)
    {
        var all = (await LoadAsync(cancellationToken)).ToList(); var current = all.FirstOrDefault(item => item.StableId == stableId); var replacement = new MonitorPreferences { StableId = stableId, CustomName = current?.CustomName, InputMappings = mapping };
        if (current is null) all.Add(replacement); else all[all.IndexOf(current)] = replacement; await SaveAsync(all, cancellationToken);
    }
    public async Task<MonitorInputMapping> ResetMappingAsync(string stableId, string? model, CancellationToken cancellationToken = default) { var mapping = IsAoc(model) ? new MonitorInputMapping() : new MonitorInputMapping { DisplayPort = 0, UsbC = 0, Hdmi = null }; await SetMappingAsync(stableId, mapping, cancellationToken); return mapping; }
    public async Task<MonitorPreferences?> GetAsync(string stableId, CancellationToken cancellationToken = default) => (await LoadAsync(cancellationToken)).FirstOrDefault(item => item.StableId == stableId);
    private static bool IsAoc(string? model) => model?.Contains("Q27V5", StringComparison.OrdinalIgnoreCase) == true;
    private static string? NormalizeName(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
