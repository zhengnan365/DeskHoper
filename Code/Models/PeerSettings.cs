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
    /// 三张键鼠卡片。名字是两台机器之间的约定：两边选同一张，角色互补才允许接管。
    public List<KeyboardMouseMode> KeyboardMouseModes { get; init; } = [new("各用各的键鼠", InputRole.None), new("共用笔记本的键鼠", InputRole.None), new("共用台式机的键鼠", InputRole.None)];
    public int KeyboardMouseActive { get; init; }
}

public enum InputRole
{
    None,
    /// 这台机器把键盘鼠标转出去。
    Controller,
    /// 这台机器接受对端注入。
    Target
}

public sealed record KeyboardMouseMode(string Name, InputRole Role);

public sealed record PinnedPeer(string Fingerprint, string Name, DateTimeOffset AddedAt);
