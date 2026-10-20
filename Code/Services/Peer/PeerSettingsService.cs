using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public sealed class PeerSettingsService(string? filePath = null) : JsonFileService<PeerSettings>(filePath ?? AppPaths.PeerFile)
{
    private PeerSettings? _cache;
    public async Task<PeerSettings> LoadAsync(CancellationToken cancellationToken = default) => _cache ??= await base.LoadAsync(new PeerSettings(), cancellationToken);
    public async Task SaveSettingsAsync(PeerSettings settings, CancellationToken cancellationToken = default) { _cache = settings; await base.SaveAsync(settings, cancellationToken); }

    public async Task<bool> PinAsync(string fingerprint, string name, CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(cancellationToken);
        if (current.PinnedPeers.Any(peer => peer.Fingerprint == fingerprint)) return false;
        var peers = current.PinnedPeers.ToList();
        peers.Add(new PinnedPeer(fingerprint, name, DateTimeOffset.Now));
        await SaveSettingsAsync(current with { PinnedPeers = peers }, cancellationToken);
        return true;
    }

    public async Task UnpinAllAsync(CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(cancellationToken);
        await SaveSettingsAsync(current with { PinnedPeers = [] }, cancellationToken);
    }
}
