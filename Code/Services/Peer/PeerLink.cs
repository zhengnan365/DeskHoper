using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public enum PeerLinkState { Unpaired, WaitingForPairing, Connecting, Connected, Failed }

/// 两台机器之间的加密链路。TLS 负责加密，身份由“自签证书 + 挑战签名 + 指纹 pin”在应用层确认，
/// 第一次配对靠屏幕上显示的 6 位码绑定（TOFU）。
public sealed class PeerLink : IAsyncDisposable
{
    private static readonly byte[] EmptyBody = [];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int HeartbeatIntervalMs = 5000;
    private const int HeartbeatTimeoutMs = 15000;
    private const int PairingCodeLifetimeMinutes = 5;
    private const int MaxPairingAttempts = 3;
    private const int ConnectTimeoutMs = 5000;

    private readonly PeerSettingsService _settingsService;
    private readonly PeerIdentity _identity;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private volatile PeerSession? _session;
    private string? _pairingCode;
    private DateTimeOffset _pairingExpiresAt = DateTimeOffset.MinValue;
    private int _pairingAttempts;

    public PeerLink(PeerSettingsService settingsService, PeerIdentity identity)
    {
        _settingsService = settingsService;
        _identity = identity;
    }

    public PeerLinkState State { get; private set; } = PeerLinkState.Unpaired;
    public string LocalName { get; private set; } = Environment.MachineName;
    public string? RemoteName { get; private set; }
    public string? RemoteFingerprint { get; private set; }
    public string? LastError { get; private set; }
    public string? LocalPairingCode => _pairingExpiresAt > DateTimeOffset.UtcNow ? _pairingCode : null;
    public int Port { get; private set; }
    public bool IsConnected => _session is not null;
    public event Action? StateChanged;
    public event Action<string>? TextReceived;

    public async Task StartAsync()
    {
        if (_listener is not null) return;
        var settings = await _settingsService.LoadAsync();
        LocalName = settings.LocalName;
        Port = settings.Port;
        _lifetime = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, settings.Port);
        _listener.Start();
        _ = AcceptLoopAsync(_lifetime.Token);
        if (settings.AutoReconnect && !string.IsNullOrWhiteSpace(settings.LastPeerHost) && settings.PinnedPeers.Count > 0)
            _ = ReconnectLoopAsync(settings.LastPeerHost!, _lifetime.Token);
        await AppLogger.InfoAsync($"Peer link listening on port {settings.Port} as {LocalName}");
    }

    public string BeginPairing()
    {
        _pairingCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _pairingAttempts = 0;
        _pairingExpiresAt = DateTimeOffset.UtcNow.AddMinutes(PairingCodeLifetimeMinutes);
        SetState(PeerLinkState.WaitingForPairing);
        return _pairingCode;
    }

    public void CancelPairing()
    {
        _pairingCode = null;
        _pairingExpiresAt = DateTimeOffset.MinValue;
        SetState(IsConnected ? PeerLinkState.Connected : PeerLinkState.Unpaired);
    }

    public Task<bool> ConnectAsync(string host, string? code, int? port = null) => ConnectCoreAsync(host, code, rememberHost: true, port);

    private async Task<bool> ConnectCoreAsync(string host, string? code, bool rememberHost, int? port = null)
    {
        if (_session is not null) { LastError = "已经有一个连接在用。"; SetState(PeerLinkState.Failed); return false; }
        SetState(PeerLinkState.Connecting);
        TcpClient? client = null;
        SslStream? ssl = null;
        try
        {
            var settings = await _settingsService.LoadAsync();
            client = new TcpClient();
            using (var timeout = new CancellationTokenSource(ConnectTimeoutMs))
            {
                await client.ConnectAsync(host, port ?? settings.Port, timeout.Token);
                ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = SslProtocols.None }, timeout.Token);
            }
            var session = await HandshakeAsync(client, ssl, asServer: false, code);
            if (session is null) { SetState(PeerLinkState.Failed); return false; }
            // 必须重新读一次：握手过程中 PinAsync 已经把对端指纹写进来了，用握手前的旧对象回写会把 pin 抹掉。
            if (rememberHost) await _settingsService.SaveSettingsAsync((await _settingsService.LoadAsync()) with { LastPeerHost = host });
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ssl?.Dispose();
            client?.Dispose();
            SetState(PeerLinkState.Failed);
            return false;
        }
    }

    private async Task ReconnectLoopAsync(string host, CancellationToken cancellationToken)
    {
        var delay = 1000;
        while (!cancellationToken.IsCancellationRequested && !IsConnected)
        {
            try { await Task.Delay(delay, cancellationToken); } catch (OperationCanceledException) { return; }
            if (await ConnectCoreAsync(host, code: null, rememberHost: false)) return;
            delay = Math.Min(delay * 2, 30000);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { LastError = ex.Message; continue; }
            _ = HandleIncomingAsync(client, cancellationToken);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken cancellationToken)
    {
        SslStream? ssl = null;
        try
        {
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _identity.Certificate, ClientCertificateRequired = false, EnabledSslProtocols = SslProtocols.None }, cancellationToken);
            await HandshakeAsync(client, ssl, asServer: true, code: null);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ssl?.Dispose();
            client.Dispose();
        }
    }

    private async Task<PeerSession?> HandshakeAsync(TcpClient client, SslStream ssl, bool asServer, string? code)
    {
        var cancellationToken = _lifetime?.Token ?? CancellationToken.None;
        // 广播的名字必须来自配置；纯客户端一侧不会走 StartAsync，所以这里补一次读取。
        LocalName = (await _settingsService.LoadAsync(cancellationToken)).LocalName;
        var nonce = RandomNumberGenerator.GetBytes(32);
        await WriteFrameAsync(ssl, FrameType.Identity, Body(new IdentityPayload(LocalName, Convert.ToBase64String(_identity.CertificateDer), Convert.ToBase64String(nonce))), cancellationToken);
        X509Certificate2? peerCertificate = null;
        var peerName = "对端";
        var peerNonce = string.Empty;
        var outgoingCode = code;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadAsync(ssl, cancellationToken);
                if (frame is null) { LastError = "对端在握手完成前断开。"; break; }
                switch (frame.Value.Type)
                {
                    case FrameType.Identity:
                    {
                        var payload = Read<IdentityPayload>(frame.Value.Payload);
                        if (payload is null) { await RejectAsync(ssl, client, "握手数据损坏。"); return null; }
                        peerCertificate = new X509Certificate2(Convert.FromBase64String(payload.Certificate));
                        peerName = payload.Name;
                        peerNonce = payload.Nonce;
                        await WriteFrameAsync(ssl, FrameType.Proof, Body(new ProofPayload(Convert.ToBase64String(_identity.Sign(Convert.FromBase64String(peerNonce))), outgoingCode)), cancellationToken);
                        outgoingCode = null;
                        break;
                    }
                    case FrameType.Proof:
                    {
                        if (peerCertificate is null) { await RejectAsync(ssl, client, "顺序错误：还没拿到对端证书。"); return null; }
                        var payload = Read<ProofPayload>(frame.Value.Payload);
                        var signature = payload is null ? null : Convert.FromBase64String(payload.Signature);
                        // 对端的 Proof 是对“我发出去的 nonce”签名，不是对它自己的 nonce；用错就永远验不过。
                        if (signature is null || !PeerIdentity.Verify(peerCertificate.RawData, nonce, signature))
                        { await RejectAsync(ssl, client, "对端证书私钥校验失败。"); return null; }
                        var fingerprint = PeerIdentity.FingerprintOf(peerCertificate.RawData);
                        if (asServer)
                        {
                            var settings = await _settingsService.LoadAsync(cancellationToken);
                            var pinned = settings.PinnedPeers.Any(peer => peer.Fingerprint == fingerprint);
                            var codeMatches = _pairingCode is { } expected && payload?.Code == expected && _pairingExpiresAt > DateTimeOffset.UtcNow;
                            if (codeMatches)
                            {
                                _pairingCode = null;
                                _pairingExpiresAt = DateTimeOffset.MinValue;
                                await _settingsService.PinAsync(fingerprint, peerName, cancellationToken);
                            }
                            else if (payload?.Code is not null)
                            {
                                _pairingAttempts++;
                                if (_pairingAttempts >= MaxPairingAttempts) { _pairingCode = null; _pairingExpiresAt = DateTimeOffset.MinValue; }
                            }
                            if (!pinned && !codeMatches) { await RejectAsync(ssl, client, "配对码不正确或已过期。"); return null; }
                            // 先 Adopt 再回 Paired：否则对端收到 Paired 时本机状态还没切过来，会短暂出现“已连接但发文本失败”。
                            Adopt(new PeerSession(client, ssl, peerName, fingerprint));
                            await WriteFrameAsync(ssl, FrameType.Paired, EmptyBody, cancellationToken);
                            return _session;
                        }
                        await WriteFrameAsync(ssl, FrameType.Paired, EmptyBody, cancellationToken);
                        break;
                    }
                    case FrameType.Paired:
                    {
                        if (peerCertificate is null) continue;
                        var fingerprint = PeerIdentity.FingerprintOf(peerCertificate.RawData);
                        await _settingsService.PinAsync(fingerprint, peerName, cancellationToken);
                        Adopt(new PeerSession(client, ssl, peerName, fingerprint));
                        return _session;
                    }
                    case FrameType.Rejected:
                    {
                        LastError = Read<RejectPayload>(frame.Value.Payload)?.Reason ?? "对端拒绝了配对。";
                        Close(ssl, client);
                        SetState(PeerLinkState.Failed);
                        return null;
                    }
                    case FrameType.Ping:
                        await WriteFrameAsync(ssl, FrameType.Pong, EmptyBody, cancellationToken);
                        break;
                }
            }
        }
        catch (Exception ex) { LastError = ex.Message; }
        Close(ssl, client);
        return null;
    }

    private async Task RejectAsync(SslStream ssl, TcpClient client, string reason)
    {
        LastError = reason;
        try
        {
            // 必须先 flush + ShutdownAsync：直接 dispose 会把拒绝帧留在 TLS 缓冲区里，对端只能看到一个突然断开。
            await WriteFrameAsync(ssl, FrameType.Rejected, Body(new RejectPayload(reason)), CancellationToken.None);
            await ssl.FlushAsync(CancellationToken.None);
            await ssl.ShutdownAsync();
        } catch { }
        Close(ssl, client);
        SetState(PeerLinkState.Failed);
        await AppLogger.InfoAsync($"Pairing rejected: {reason}");
    }

    private static void Close(SslStream ssl, TcpClient client)
    {
        try { ssl.Dispose(); } catch { }
        try { client.Dispose(); } catch { }
    }

    private void Adopt(PeerSession session)
    {
        var previous = _session;
        if (previous is not null && !ReferenceEquals(previous, session)) previous.Close();
        _session = session;
        RemoteName = session.PeerName;
        RemoteFingerprint = session.PeerFingerprint;
        LastError = null;
        SetState(PeerLinkState.Connected);
        _ = ReadLoopAsync(session);
        _ = HeartbeatLoopAsync(session);
        _ = AppLogger.InfoAsync($"Peer connected: {session.PeerName} ({session.PeerFingerprint[..Math.Min(8, session.PeerFingerprint.Length)]})");
    }

    private async Task ReadLoopAsync(PeerSession session)
    {
        try
        {
            while (!session.CancellationToken.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadAsync(session.Stream, session.CancellationToken);
                if (frame is null) break;
                session.LastSeenAt = DateTimeOffset.UtcNow;
                switch (frame.Value.Type)
                {
                    case FrameType.Ping:
                        await WriteFrameAsync(session.Stream, FrameType.Pong, EmptyBody, session.CancellationToken);
                        break;
                    case FrameType.Text:
                        if (Read<TextPayload>(frame.Value.Payload)?.Text is { } text) TextReceived?.Invoke(text);
                        break;
                    case FrameType.Rejected:
                        LastError = Read<RejectPayload>(frame.Value.Payload)?.Reason ?? "对端关闭了连接。";
                        return;
                }
            }
        }
        catch { }
        finally { RetireIfCurrent(session); }
    }

    private async Task HeartbeatLoopAsync(PeerSession session)
    {
        while (!session.CancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatIntervalMs, session.CancellationToken); } catch (OperationCanceledException) { return; }
            if (DateTimeOffset.UtcNow - session.LastSeenAt > TimeSpan.FromMilliseconds(HeartbeatTimeoutMs))
            {
                LastError = "对端长时间没有响应，已断开。";
                session.Close();
                return;
            }
            try { await WriteFrameAsync(session.Stream, FrameType.Ping, EmptyBody, session.CancellationToken); }
            catch { session.Close(); return; }
        }
    }

    private void RetireIfCurrent(PeerSession session)
    {
        session.Close();
        if (!ReferenceEquals(_session, session)) return;
        _session = null;
        RemoteName = null;
        RemoteFingerprint = null;
        SetState(PeerLinkState.Unpaired);
    }

    public Task<bool> SendTextAsync(string text)
    {
        var session = _session;
        if (session is null) return Task.FromResult(false);
        return SendAsync(session, FrameType.Text, Body(new TextPayload(text)));

        async Task<bool> SendAsync(PeerSession target, FrameType type, byte[] body)
        {
            try { await WriteFrameAsync(target.Stream, type, body, target.CancellationToken); return true; }
            catch { return false; }
        }
    }

    public async Task DisconnectAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            try { await WriteFrameAsync(session.Stream, FrameType.Rejected, Body(new RejectPayload("本机主动断开。")), CancellationToken.None); } catch { }
            session.Close();
        }
        RemoteName = null;
        RemoteFingerprint = null;
        SetState(PeerLinkState.Unpaired);
    }

    private async Task WriteFrameAsync(Stream stream, FrameType type, byte[] body, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try { await FrameCodec.WriteAsync(stream, type, body, cancellationToken); }
        finally { _sendGate.Release(); }
    }

    private static byte[] Body<T>(T payload) => JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

    private static T? Read<T>(byte[] payload)
    {
        try { return JsonSerializer.Deserialize<T>(payload, JsonOptions); } catch { return default; }
    }

    private void SetState(PeerLinkState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime?.Cancel();
        _session?.Close();
        try { _listener?.Stop(); } catch { }
        _identity.Dispose();
        await Task.CompletedTask;
    }

    private sealed record IdentityPayload(string Name, string Certificate, string Nonce);
    private sealed record ProofPayload(string Signature, string? Code);
    private sealed record RejectPayload(string Reason);
    private sealed record TextPayload(string Text);

    private sealed class PeerSession
    {
        private readonly CancellationTokenSource _lifetime = new();
        private int _closed;

        public PeerSession(TcpClient client, SslStream stream, string peerName, string peerFingerprint)
        {
            Client = client;
            Stream = stream;
            PeerName = peerName;
            PeerFingerprint = peerFingerprint;
            LastSeenAt = DateTimeOffset.UtcNow;
        }

        public TcpClient Client { get; }
        public SslStream Stream { get; }
        public string PeerName { get; }
        public string PeerFingerprint { get; }
        public DateTimeOffset LastSeenAt { get; set; }
        public CancellationToken CancellationToken => _lifetime.Token;

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _lifetime.Cancel();
            try { Stream.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
        }
    }
}
