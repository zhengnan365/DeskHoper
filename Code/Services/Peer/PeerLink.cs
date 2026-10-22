using System.Collections.Concurrent;
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
    private const int MaxChunkBytes = 256 * 1024;
    private const long MaxTotalFileBytes = 512L * 1024 * 1024;
    private const int MaxFileCount = 200;
    private const int OfferTimeoutSeconds = 90;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _awaitingDecision = new();
    private FileReceiveContext? _incoming;

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
    /// 配对码剩余有效期。没在配对时为 null，界面用它做倒计时并在到点时自己收起。
    public TimeSpan? PairingTimeLeft => LocalPairingCode is null ? null : _pairingExpiresAt - DateTimeOffset.UtcNow;
    public int Port { get; private set; }
    public bool IsConnected => _session is not null;
    public event Action? StateChanged;
    public event Action<string>? TextReceived;
    /// 对端要发文件时由界面决定收不收（后台线程调用，实现自己负责切回 UI 线程）。
    public Func<FileOffer, Task<bool>>? OfferHandler { get; set; }
    public event Action<IReadOnlyList<string>>? FilesReceived;
    /// 发送端进行中的进度，只报"等待对端接收…"和整数百分比（每个 256KB 块都刷一次太吵）。
    /// 终态不在这里发，由调用方拿到返回值后自己写，失败原因走 LastError。
    public event Action<string>? SendProgress;
    /// 接收端进度，和 SendProgress 对称：等待确认没有文案，一有字节进来就报百分比，收完报总量。
    public event Action<string>? ReceiveProgress;
    private int _receivedPercent;
    public event Action<KeyboardInput>? KeyboardReceived;
    public event Action<MouseInput>? MouseReceived;
    /// 对端请求接管输入，带它选的键鼠模式。本机决定放不放行。
    public event Action<int>? InputArmRequested;
    public event Action<int>? InputAcked;
    public event Action<string>? InputRejected;
    public event Action? InputStopped;
    /// 对端换了键鼠卡片。选"共用本端"会把这台切到"共用远端"，反之亦然，两台就不用各点一次。
    /// 换模式本身不接管输入：真正开始屏蔽本机键盘仍要点"接管"，并且要对面回一个确认。
    public event Action<int>? InputModeChanged;
    /// 只有本机确实被选成被控端才置位，配对成功本身不等于能把输入注到这台机器上。
    public bool AcceptingInput { get; private set; }
    public void SetAcceptingInput(bool accepting) => AcceptingInput = accepting;
    private string _receiveDirectory = AppPaths.ReceivedFilesDirectory;
    public string ReceiveDirectory => _receiveDirectory;
    public static string ResolveReceiveDirectory(string? directory) => string.IsNullOrWhiteSpace(directory) ? AppPaths.ReceivedFilesDirectory : directory.Trim();
    /// 界面上改了接收目录就立刻生效，不用等重开程序。
    public void ApplyReceiveDirectory(string? directory) => _receiveDirectory = ResolveReceiveDirectory(directory);

    public async Task StartAsync()
    {
        if (_listener is not null) return;
        var settings = await _settingsService.LoadAsync();
        LocalName = settings.LocalName;
        Port = settings.Port;
        ApplyReceiveDirectory(settings.ReceiveDirectory);
        _lifetime = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, settings.Port);
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            // 端口被占用时必须只报告不能崩：界面此时已经显示出来了。
            _listener = null;
            LastError = $"端口 {settings.Port} 无法监听：{ex.Message}";
            SetState(PeerLinkState.Failed);
            await AppLogger.ErrorAsync("Peer listener failed to start", ex);
            return;
        }
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
            client = new TcpClient { NoDelay = true };   // .NET 默认 Nagle 开着，几十字节的键鼠帧会等上一帧 ACK 才发，手感直接变成拖拽
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
            client.NoDelay = true;   // 接受到的 socket 不会继承监听器的设置，两边都得各自关掉 Nagle
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
                        if (Read<TextPayload>(frame.Value.Payload)?.Text is { } text)
                        {
                            _ = AppLogger.InfoAsync($"Text frame received: {text.Length} chars");
                            TextReceived?.Invoke(text);
                        }
                        break;
                    case FrameType.Rejected:
                        LastError = Read<RejectPayload>(frame.Value.Payload)?.Reason ?? "对端关闭了连接。";
                        return;
                    case FrameType.FileHeader:
                        if (Read<FileOfferPayload>(frame.Value.Payload) is { } offer) _ = HandleFileOfferAsync(session, offer);
                        break;
                    case FrameType.FileChunk:
                        if (_incoming is not { } receiving) break;
                        receiving.Write(frame.Value.Payload);
                        // 读循环在后台线程，界面接的是简单属性，WPF 会自己切回 UI 线程。
                        if (receiving.Percent != _receivedPercent)
                        {
                            _receivedPercent = receiving.Percent;
                            ReceiveProgress?.Invoke($"接收中 {_receivedPercent}%");
                        }
                        break;
                    case FrameType.FileEnd:
                        if (_incoming is not { } incoming) break;
                        var paths = incoming.Complete();
                        var receivedBytes = incoming.ReceivedBytes;
                        incoming.Dispose();
                        _incoming = null;
                        ReceiveProgress?.Invoke($"已接收 {paths.Count} 个文件 · {receivedBytes / 1024.0 / 1024.0:0.##} MB");
                        FilesReceived?.Invoke(paths);
                        break;
                    case FrameType.FileDone:
                        if (Read<FileDecisionPayload>(frame.Value.Payload) is { } verdict && _awaitingDecision.TryRemove(verdict.TransferId, out var pending))
                            pending.TrySetResult(verdict.Accepted);
                        break;
                    case FrameType.InputArm:
                        if (Read<InputSignalPayload>(frame.Value.Payload) is { } arm) InputArmRequested?.Invoke(arm.Mode);
                        break;
                    case FrameType.InputAck:
                        if (Read<InputSignalPayload>(frame.Value.Payload) is { } acked) InputAcked?.Invoke(acked.Mode);
                        break;
                    case FrameType.InputReject:
                        if (Read<InputSignalPayload>(frame.Value.Payload) is { } rejected) InputRejected?.Invoke(rejected.Reason ?? "对端没有放行输入接管。");
                        break;
                    case FrameType.InputMode:
                        if (Read<InputSignalPayload>(frame.Value.Payload) is { } switched) InputModeChanged?.Invoke(switched.Mode);
                        break;
                    case FrameType.InputStop:
                        AcceptingInput = false;
                        InputStopped?.Invoke();
                        break;
                    case FrameType.Keyboard when AcceptingInput:
                        try { KeyboardReceived?.Invoke(InputPayload.ReadKeyboard(frame.Value.Payload)); }
                        catch (InvalidDataException) { }
                        break;
                    case FrameType.Mouse when AcceptingInput:
                        try { MouseReceived?.Invoke(InputPayload.ReadMouse(frame.Value.Payload)); }
                        catch (InvalidDataException) { }
                        break;
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
        _incoming?.Dispose();
        _incoming = null;
        // 链路一断必须马上停止接收注入，否则控制端那边看起来还在"接管"，本机却已经没人管了。
        if (AcceptingInput) { AcceptingInput = false; InputStopped?.Invoke(); }
        if (!ReferenceEquals(_session, session)) return;
        _session = null;
        RemoteName = null;
        RemoteFingerprint = null;
        SetState(PeerLinkState.Unpaired);
    }

    public Task<bool> SendKeyboardAsync(KeyboardInput input) => SendInputFrameAsync(FrameType.Keyboard, InputPayload.Write(input));
    public Task<bool> SendMouseAsync(MouseInput input) => SendInputFrameAsync(FrameType.Mouse, InputPayload.Write(input));
    public Task<bool> SendInputArmAsync(int mode) => SendAsync(FrameType.InputArm, Body(new InputSignalPayload(mode, null)));
    public Task<bool> SendInputAckAsync(int mode) => SendAsync(FrameType.InputAck, Body(new InputSignalPayload(mode, null)));
    public Task<bool> SendInputRejectAsync(string reason) => SendAsync(FrameType.InputReject, Body(new InputSignalPayload(0, reason)));
    public Task<bool> SendInputModeAsync(int mode) => SendAsync(FrameType.InputMode, Body(new InputSignalPayload(mode, null)));
    public Task<bool> SendInputStopAsync() => SendAsync(FrameType.InputStop, EmptyBody);

    private Task<bool> SendInputFrameAsync(FrameType type, byte[] body) => SendAsync(type, body);

    private Task<bool> SendAsync(FrameType type, byte[] body)
    {
        var session = _session;
        if (session is null) return Task.FromResult(false);
        return SendAsync(session);

        async Task<bool> SendAsync(PeerSession target)
        {
            try { await WriteFrameAsync(target.Stream, type, body, target.CancellationToken); return true; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return false; }
        }
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

    public async Task<bool> SendFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null) return false;
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) return false;
        if (files.Count > MaxFileCount) { LastError = $"一次最多发送 {MaxFileCount} 个文件。"; return false; }
        var sizes = files.Select(path => new FileInfo(path).Length).ToList();
        var total = sizes.Sum();
        if (total <= 0) return false;
        if (total > MaxTotalFileBytes) { LastError = $"文件总量 {total / 1024 / 1024} MB 超过 512 MB 上限，没有发送。"; return false; }

        var transferId = Guid.NewGuid().ToString("N");
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _awaitingDecision[transferId] = decision;
        try
        {
            var offer = new FileOfferPayload(transferId, files.Select((path, index) => new FileOfferItemPayload(Path.GetFileName(path), sizes[index])).ToList());
            await WriteFrameAsync(session.Stream, FrameType.FileHeader, Body(offer), session.CancellationToken);
            SendProgress?.Invoke("等待对端接收…");
            // 先等对端点头再传字节：没同意就不该往对方硬盘里写任何东西。
            var finished = await Task.WhenAny(decision.Task, Task.Delay(TimeSpan.FromSeconds(OfferTimeoutSeconds), cancellationToken));
            await AppLogger.InfoAsync($"File offer decision wait finished: gotDecision={finished == decision.Task} accepted={(finished == decision.Task ? decision.Task.Result : false)}");
            if (finished != decision.Task) { LastError = $"等对端确认超过 {OfferTimeoutSeconds} 秒，没有发送。"; return false; }
            if (!decision.Task.Result) { LastError = "对端拒绝了这些文件。"; return false; }
            var done = 0L;
            var lastReported = -1;
            SendProgress?.Invoke("发送中 0%");
            foreach (var path in files)
            {
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[MaxChunkBytes];
                int read;
                while ((read = await source.ReadAsync(buffer, session.CancellationToken)) > 0)
                {
                    await WriteFrameAsync(session.Stream, FrameType.FileChunk, read == buffer.Length ? buffer : buffer[..read], session.CancellationToken);
                    done += read;
                    var percent = (int)(done * 100 / total);
                    if (percent == lastReported) continue;
                    lastReported = percent;
                    SendProgress?.Invoke($"发送中 {percent}%");
                }
            }
            await WriteFrameAsync(session.Stream, FrameType.FileEnd, Body(new FileEndPayload(transferId)), session.CancellationToken);
            await AppLogger.InfoAsync($"Sent {files.Count} files, {total / 1024} KB");
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
        finally { _awaitingDecision.TryRemove(transferId, out _); }
    }

    private async Task HandleFileOfferAsync(PeerSession session, FileOfferPayload offer)
    {
        var files = offer.Files.Select(item => new FileOfferFile(FileNameSafety.Sanitize(item.Name), item.Size)).ToList();
        var accepted = false;
        try
        {
            var total = files.Sum(file => file.Size);
            if (files.Count > 0 && files.Count <= MaxFileCount && total > 0 && total <= MaxTotalFileBytes && OfferHandler is { } handler)
            {
                await AppLogger.InfoAsync($"File offer asking UI: {files.Count} files, {total / 1024} KB");
                accepted = await handler(new FileOffer(files));
                await AppLogger.InfoAsync($"File offer answered: accepted={accepted}");
            }
        }
        catch (Exception ex) { await AppLogger.ErrorAsync("处理文件接收请求失败", ex); }
        if (accepted)
        {
            _receivedPercent = 0;
            _incoming = new FileReceiveContext(new FileOffer(files), ReceiveDirectory);
        }
        await WriteFrameAsync(session.Stream, FrameType.FileDone, Body(new FileDecisionPayload(offer.TransferId, accepted)), session.CancellationToken);
        await AppLogger.InfoAsync($"File offer decision sent to peer: accepted={accepted}");
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
    private sealed record InputSignalPayload(int Mode, string? Reason);

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
