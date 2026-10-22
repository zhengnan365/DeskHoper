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
    /// 三张键鼠卡片的显示名，可以右键改。卡片代表什么由位置决定，所以改名不会破坏两台机器之间的一致性。
    public List<string> KeyboardMouseNames { get; init; } = ["各用各的键鼠", "共用本端键鼠", "共用远端键鼠"];
    public int KeyboardMouseActive { get; init; }
    /// 分开开关：常见需求是只用对面的键盘，鼠标自己那台动。
    public bool ShareKeyboard { get; init; } = true;
    public bool ShareMouse { get; init; } = true;
}

/// 选哪张卡就等于定了本机角色，不需要再单独选一次。
public enum InputMode
{
    /// 各用各的，不转发。
    Off = 0,
    /// 这台的键盘鼠标同时控制另一台：本机是控制端。
    LocalControlsPeer = 1,
    /// 另一台的键盘鼠标控制这台：本机是被控端。
    PeerControlsLocal = 2
}

public sealed record PinnedPeer(string Fingerprint, string Name, DateTimeOffset AddedAt);
