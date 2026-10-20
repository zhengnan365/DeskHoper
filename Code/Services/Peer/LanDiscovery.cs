using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MonitorSwitch.Services;

public sealed record LanPeer(string Name, string Address, int Port, DateTimeOffset SeenAt);

internal sealed record LanAnnounce(int Version, string Kind, string Id, string Name, int Port);

/// 只回答"局域网上还有谁开着这个程序"。通告里不带任何凭据，也不构成信任：
/// 连过去仍然必须输对 6 位配对码，所以发现层被伪造最多是让列表里多一条假设备。
public sealed class LanDiscovery : IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PeerExpiresAfter = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly UdpClient _socket = new() { EnableBroadcast = true };
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, LanPeer> _peers = new(StringComparer.Ordinal);
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly int _discoveryPort;
    private readonly Func<string> _localName;

    public LanDiscovery(int discoveryPort, Func<string> localName, int dataPort)
    {
        _discoveryPort = discoveryPort;
        _localName = localName;
        DataPort = dataPort;
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
    }

    public int DataPort { get; }
    /// 列表在后台线程更新，订阅方负责切回 UI 线程。
    public event Action<IReadOnlyList<LanPeer>>? PeersChanged;

    public async Task StartAsync()
    {
        await SendAsync("probe");
        await SendAsync("announce");
        _ = ReceiveLoopAsync(_cancellation.Token);
        _ = AnnounceLoopAsync(_cancellation.Token);
        await AppLogger.InfoAsync($"LAN discovery listening on udp/{_discoveryPort} as {_localName()}");
    }

    private async Task AnnounceLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(AnnounceEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await SendAsync("announce", token);
                Prune();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await _socket.ReceiveAsync(token); }
            catch (OperationCanceledException) { return; }
            // Dispose 掉 socket 会让挂着的接收调用抛 SocketException，这是正常的收尾路径。
            catch (SocketException) { return; }
            Handle(packet);
        }
    }

    private void Handle(UdpReceiveResult packet)
    {
        LanAnnounce? announce;
        try { announce = JsonSerializer.Deserialize<LanAnnounce>(packet.Buffer, JsonOptions); }
        catch (JsonException) { return; }
        if (announce is null || announce.Version != ProtocolVersion || announce.Id == _runId) return;
        if (announce.Kind == "probe") { _ = SendAsync("announce", target: packet.RemoteEndPoint.Address.ToString()); return; }
        if (announce.Kind != "announce") return;
        lock (_peers)
        {
            _peers[announce.Id] = new LanPeer(announce.Name, packet.RemoteEndPoint.Address.ToString(), announce.Port, DateTimeOffset.Now);
            PruneLocked();
        }
        PeersChanged?.Invoke(Snapshot());
    }

    private void Prune()
    {
        lock (_peers) { if (PruneLocked()) PeersChanged?.Invoke(Snapshot()); }
    }

    private bool PruneLocked()
    {
        var expired = _peers.Where(entry => DateTimeOffset.Now - entry.Value.SeenAt > PeerExpiresAfter).Select(entry => entry.Key).ToList();
        foreach (var key in expired) _peers.Remove(key);
        return expired.Count > 0;
    }

    private IReadOnlyList<LanPeer> Snapshot()
    {
        lock (_peers) { return _peers.Values.OrderBy(peer => peer.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
    }

    private async Task SendAsync(string kind, CancellationToken token = default, string? target = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new LanAnnounce(ProtocolVersion, kind, _runId, _localName(), DataPort), JsonOptions);
        var endpoint = target is null ? new IPEndPoint(IPAddress.Broadcast, _discoveryPort) : new IPEndPoint(IPAddress.Parse(target), _discoveryPort);
        try { await _socket.SendAsync(payload, endpoint, token); }
        // 网卡被拔掉、当前没有默认路由都会走到这里。发现层是锦上添花，每 3 秒本来就会重试一次，
        // 所以这里只吞掉不影响主功能的发送失败，不写日志——网络真断了会变成每 3 秒一条刷屏。
        catch (Exception ex) when (ex is SocketException or FormatException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _socket.Dispose();
    }
}
