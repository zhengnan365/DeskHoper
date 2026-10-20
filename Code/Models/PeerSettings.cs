namespace MonitorSwitch.Models;

public sealed record PeerSettings
{
    public string LocalName { get; init; } = Environment.MachineName;
    public int Port { get; init; } = 47810;
    public List<PinnedPeer> PinnedPeers { get; init; } = [];
    public bool AutoReconnect { get; init; } = true;
    public string? LastPeerHost { get; init; }
    public bool AutoAcceptFiles { get; init; }
    /// 留空表示用默认目录。改目录不需要重开程序，PeerLink 会立刻用新值。
    public string? ReceiveDirectory { get; init; }
}

public sealed record PinnedPeer(string Fingerprint, string Name, DateTimeOffset AddedAt);
