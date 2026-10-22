using System.Collections.ObjectModel;
using System.IO;
using System.Net.Sockets;
using MonitorSwitch.Models;
using MonitorSwitch.Services;

namespace MonitorSwitch.ViewModels;

public enum AppSection { ScreenSwitch, Sharing, KeyboardMouse }

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DdcCiService _ddcCiService = new();
    private readonly ProfileService _profileService = new();
    private readonly MonitorPreferencesService _monitorPreferencesService = new();
    private readonly IProjectionService _projectionService = new ProjectionService();
    private readonly ProfileExecutionService _profileExecutionService;
    private readonly MonitorDiscoveryService _discoveryService;
    private readonly MonitorInputMapping _defaultInputMapping = new();
    private readonly Dictionary<string, MonitorInputMapping> _activeMappings = new(StringComparer.Ordinal);
    private string _status = "准备扫描显示器";
    private bool _isSwitching;
    private ProfileViewModel? _currentProfile;
    private ProjectionMode? _currentProjectionMode;
    private bool _isRefreshing;
    private readonly IMonitorIdentificationPresenter? _identificationPresenter;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    public MainViewModel(IMonitorIdentificationPresenter? identificationPresenter = null)
    {
        _identificationPresenter = identificationPresenter;
        _discoveryService = new MonitorDiscoveryService(_ddcCiService);
        _profileExecutionService = new ProfileExecutionService(_ddcCiService, _projectionService, _monitorPreferencesService);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ProjectionModes = [new(ProjectionMode.Internal, "仅电脑屏幕", () => SetProjectionAsync(ProjectionMode.Internal)), new(ProjectionMode.Duplicate, "复制", () => SetProjectionAsync(ProjectionMode.Duplicate)), new(ProjectionMode.Extend, "扩展", () => SetProjectionAsync(ProjectionMode.Extend), true), new(ProjectionMode.External, "仅第二屏幕", () => SetProjectionAsync(ProjectionMode.External))];
        SetInternalProjectionCommand = new AsyncRelayCommand(() => SetProjectionAsync(ProjectionMode.Internal));
        SetDuplicateProjectionCommand = new AsyncRelayCommand(() => SetProjectionAsync(ProjectionMode.Duplicate));
        SetExtendProjectionCommand = new AsyncRelayCommand(() => SetProjectionAsync(ProjectionMode.Extend));
        SetExternalProjectionCommand = new AsyncRelayCommand(() => SetProjectionAsync(ProjectionMode.External));
        DesktopSwitchCommand = new AsyncRelayCommand(() => SwitchDefaultProfileAsync("desktop", "台式机"));
        LaptopSwitchCommand = new AsyncRelayCommand(() => SwitchDefaultProfileAsync("laptop", "笔记本"));
        SwitchProfileCommand = new AsyncRelayCommand(parameter => parameter is ProfileViewModel profile ? ExecuteProfileAsync(profile) : Task.CompletedTask);
        SetMonitorInputCommand = new AsyncRelayCommand(parameter => parameter is MonitorInputRequest request ? request.Monitor.SetInputValueAsync(request.Value) : Task.CompletedTask);
        SetProjectionModeCommand = new AsyncRelayCommand(parameter => parameter is ProjectionMode mode ? SetProjectionAsync(mode) : Task.CompletedTask);
        IdentifyMonitorsCommand = new AsyncRelayCommand(IdentifyMonitorsAsync);
        SelectSectionCommand = new AsyncRelayCommand(parameter => { if (parameter is string name && Enum.TryParse<AppSection>(name, out var section)) { ActiveSection = section; if (section == AppSection.Sharing) { EnsureLocalIpAddressDetected(); _ = StartDiscoveryIfNeededAsync(); } } return Task.CompletedTask; });
        GeneratePairingCodeCommand = new AsyncRelayCommand(GeneratePairingCodeAsync);
        CancelPairingCodeCommand = new AsyncRelayCommand(CancelPairingCodeAsync);
        ConnectPeerCommand = new AsyncRelayCommand(ConnectPeerAsync);
        DisconnectPeerCommand = new AsyncRelayCommand(DisconnectPeerAsync);
        SelectPeerCommand = new AsyncRelayCommand(parameter => { if (parameter is LanPeer peer) PeerHost = peer.Address; return Task.CompletedTask; });
        BeginRenameLocalNameCommand = new AsyncRelayCommand(BeginRenameLocalName);
        CommitRenameLocalNameCommand = new AsyncRelayCommand(CommitRenameLocalName);
        CancelRenameLocalNameCommand = new AsyncRelayCommand(CancelRenameLocalName);
        WireInputCommands();
    }
    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand GeneratePairingCodeCommand { get; }
    public AsyncRelayCommand CancelPairingCodeCommand { get; }
    public AsyncRelayCommand ConnectPeerCommand { get; }
    public AsyncRelayCommand DisconnectPeerCommand { get; }
    public AsyncRelayCommand SelectPeerCommand { get; }
    public AsyncRelayCommand SetInternalProjectionCommand { get; }
    public AsyncRelayCommand SetDuplicateProjectionCommand { get; }
    public AsyncRelayCommand SetExtendProjectionCommand { get; }
    public AsyncRelayCommand SetExternalProjectionCommand { get; }
    public AsyncRelayCommand DesktopSwitchCommand { get; }
    public AsyncRelayCommand LaptopSwitchCommand { get; }
    public AsyncRelayCommand SwitchProfileCommand { get; }
    public AsyncRelayCommand SetMonitorInputCommand { get; }
    public AsyncRelayCommand SetProjectionModeCommand { get; }
    public AsyncRelayCommand IdentifyMonitorsCommand { get; }
    public ObservableCollection<ProjectionModeViewModel> ProjectionModes { get; }
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) { OnPropertyChanged(nameof(StatusMessage)); OnPropertyChanged(nameof(CurrentStatusText)); } } }
    public string CurrentProjectionLabel => ProjectionModes.FirstOrDefault(x => x.IsSelected)?.Title ?? "模式未知";
    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];
    public ProfileViewModel? CurrentProfile { get => _currentProfile; private set { if (SetProperty(ref _currentProfile, value)) { foreach (var profile in Profiles) profile.IsCurrent = profile == value; } } }
    public ProjectionMode? CurrentProjectionMode { get => _currentProjectionMode; private set { if (SetProperty(ref _currentProjectionMode, value)) OnPropertyChanged(nameof(IsProjectionModeKnown)); } }
    public bool IsProjectionModeKnown => CurrentProjectionMode.HasValue;
    public bool IsSwitching { get => _isSwitching; private set => SetProperty(ref _isSwitching, value); }
    public string StatusMessage => Status;
    public string CurrentStatusText => Status;
    public bool IsRefreshing { get => _isRefreshing; private set => SetProperty(ref _isRefreshing, value); }
    private void UpdateSteadyStateStatus() { Status = CurrentProfile is not null ? $"当前：{CurrentProfile.DisplayName}" : Monitors.Any(m => m.IsDdcSupported && m.CurrentInputValue.HasValue) ? "当前：自定义组合" : "已就绪"; }
    private readonly PeerSettingsService _peerSettingsService = new();
    private PeerLink? _peerLink;
    /// 对端推来的剪贴板文本。由界面赋值，实现自己负责切回 UI 线程。
    public Action<string>? RemoteClipboardTextHandler { get; set; }
    /// 对端推来的文件列表（已落地），同样由界面赋值并切回 UI 线程。
    public Action<IReadOnlyList<string>>? RemoteClipboardFilesHandler { get; set; }
    /// 询问收不收文件。由界面赋值，会在后台线程被调用，实现自己负责切回 UI 线程。
    public Func<FileOffer, Task<bool>>? PeerOfferHandler { get; set; }
    public event Action? PeerConnected;

    private PeerLink EnsureLink()
    {
        if (_peerLink is not null) return _peerLink;
        _peerLink = new PeerLink(_peerSettingsService, PeerIdentity.LoadOrCreate($"DeskHoper Peer {Environment.MachineName}"));
        _peerLink.StateChanged += OnPeerStateChanged;
        _peerLink.TextReceived += text => RemoteClipboardTextHandler?.Invoke(text);
        _peerLink.OfferHandler = offer => PeerOfferHandler is { } handler ? handler(offer) : Task.FromResult(false);
        _peerLink.InputArmRequested += mode => _ = HandleInputArmAsync(mode);
        _peerLink.InputAcked += mode => StartForwarding(mode);
        _peerLink.InputRejected += reason => { StopForwarding(); InputErrorText = reason; };
        _peerLink.InputStopped += () => { StopForwarding(); IsReceivingInput = false; };
        _peerLink.InputModeChanged += mode => OnPeerInputModeChanged(mode);
        // 进行中的进度不能带超时，不然传到一半文案就没了；每来一条新的先把到期时间清掉。
        _peerLink.SendProgress += text => { _transferStatusUntil = default; SendProgressText = text; };
        _peerLink.ReceiveProgress += text => { _transferStatusUntil = default; ReceiveProgressText = text; };
        // 收完的终态走这里：ReceiveProgress 刚报完"已接收…"，紧跟着就是这个，顺序不能反。
        _peerLink.FilesReceived += paths =>
        {
            _transferStatusUntil = DateTime.Now + TransferStatusHold;
            RemoteClipboardFilesHandler?.Invoke(paths);
        };
        _peerLink.KeyboardReceived += key => RecordInjected(InputReceiver.Apply(key));
        _peerLink.MouseReceived += mouse => RecordInjected(InputReceiver.Apply(mouse));
        return _peerLink;
    }

    public async Task SendClipboardFilesAsync(IReadOnlyList<string> paths)
    {
        var link = _peerLink;
        if (link is null || !link.IsConnected) return;
        if (await link.SendFilesAsync(paths))
        {
            var megabytes = paths.Where(File.Exists).Sum(path => new FileInfo(path).Length) / 1024.0 / 1024.0;
            SendProgressText = $"发送成功，{paths.Count} 个文件 · {megabytes:0.##} MB";
        }
        else SendProgressText = link.LastError ?? "文件发送失败";
        Status = SendProgressText;
        _transferStatusUntil = DateTime.Now + TransferStatusHold;
    }


    private void OnPeerStateChanged()
    {
        OnPropertyChanged(nameof(IsPeerConnected));
        RaisePeerStatus();
        OnPropertyChanged(nameof(LocalPairingCode));
        OnPropertyChanged(nameof(IsPairingCodeShown));
        // 链路一断必须立刻交回：钩子还挂着而发不出去的话，用户面对的就是一台敲不动的电脑。
        if (_peerLink?.IsConnected != true) { StopForwarding(); IsReceivingInput = false; }
        if (_peerLink?.IsConnected != true) return;
        // 链路刚建立就补一次：模式是"共用本端"的话不用人再去点一下。
        _ = RequestInputArmAsync();
        PeerConnected?.Invoke();
    }

    public void ReportStatus(string message) => Status = message;

    private AppSection _activeSection = AppSection.ScreenSwitch;
    private bool _autoAcceptFiles;
    private string _peerLocalName = Environment.MachineName;
    private string _peerHost = string.Empty;
    private string _peerCode = string.Empty;
    private string? _peerConnectError;
    private string _receiveDirectory = AppPaths.ReceivedFilesDirectory;
    private string _pairingCountdownText = string.Empty;
    private string _localIpAddress = string.Empty;
    private string? _sendProgressText;
    private string? _receiveProgressText;
    /// 当前显示哪一页：屏幕切换 / 文件共享 / 键鼠共享。
    public AppSection ActiveSection { get => _activeSection; private set => SetProperty(ref _activeSection, value); }
    public AsyncRelayCommand SelectSectionCommand { get; private set; } = null!;
    public bool AutoAcceptFiles { get => _autoAcceptFiles; set { if (SetProperty(ref _autoAcceptFiles, value)) _ = SaveAutoAcceptFilesAsync(value); } }
    public string PeerLocalName { get => _peerLocalName; private set => SetProperty(ref _peerLocalName, value); }
    private bool _isEditingLocalName;
    private string _editableLocalName = string.Empty;
    public bool IsEditingLocalName { get => _isEditingLocalName; private set => SetProperty(ref _isEditingLocalName, value); }
    public string EditableLocalName { get => _editableLocalName; set => SetProperty(ref _editableLocalName, value); }
    public AsyncRelayCommand BeginRenameLocalNameCommand { get; private set; } = null!;
    public AsyncRelayCommand CommitRenameLocalNameCommand { get; private set; } = null!;
    public AsyncRelayCommand CancelRenameLocalNameCommand { get; private set; } = null!;
    private Task BeginRenameLocalName() { EditableLocalName = PeerLocalName; IsEditingLocalName = true; return Task.CompletedTask; }
    private Task CancelRenameLocalName() { EditableLocalName = PeerLocalName; IsEditingLocalName = false; return Task.CompletedTask; }
    private async Task CommitRenameLocalName()
    {
        if (!IsEditingLocalName) return;
        IsEditingLocalName = false;
        var name = EditableLocalName.Trim();
        if (name.Length == 0 || name == PeerLocalName) { EditableLocalName = PeerLocalName; return; }
        var settings = await _peerSettingsService.LoadAsync();
        await _peerSettingsService.SaveSettingsAsync(settings with { LocalName = name });
        PeerLocalName = name;
        ReportStatus($"本机名称已改成 {name}");
        await AppLogger.InfoAsync($"Local peer name changed to {name}");
    }
    /// 对端要往哪台机器上连，所以这一页得把本机 IP 显示出来。第一次进这一页时才查，不占启动时间。
    public string LocalIpAddressText { get => _localIpAddress; private set => SetProperty(ref _localIpAddress, value); }

    /// 拖放区在传输进行中显示的文案；null 表示没在传，文案回到"把文件拖到这里"。
    /// 发和收共用同一块显示位，所以谁开始传就把另一个的清掉，不然旧的"发送成功"会盖住新的"接收中"。
    public string? SendProgressText
    {
        get => _sendProgressText;
        private set
        {
            if (value is not null) ClearReceive();
            if (SetProperty(ref _sendProgressText, value)) RaiseTransferStatus();
        }
    }
    public bool IsSendingFiles => !string.IsNullOrEmpty(_sendProgressText);

    /// 对面发过来的东西正在落盘时的文案，显示在同一块拖放区里。
    public string? ReceiveProgressText
    {
        get => _receiveProgressText;
        private set
        {
            if (value is not null) ClearSend();
            if (SetProperty(ref _receiveProgressText, value)) RaiseTransferStatus();
        }
    }
    public bool IsReceivingFiles => !string.IsNullOrEmpty(_receiveProgressText);

    private void RaiseTransferStatus()
    {
        OnPropertyChanged(nameof(IsSendingFiles));
        OnPropertyChanged(nameof(IsReceivingFiles));
        OnPropertyChanged(nameof(HasTransferStatus));
    }

    private void ClearSend()
    {
        if (_sendProgressText is null) return;
        _sendProgressText = null;
        RaiseTransferStatus();
    }

    private void ClearReceive()
    {
        if (_receiveProgressText is null) return;
        _receiveProgressText = null;
        RaiseTransferStatus();
    }

    /// <summary>
    /// 传输结果文案的停留时间。进行中的百分比不受它约束，只有终态（发送成功 / 已接收 / 失败）会自己收回去。
    /// </summary>
    private static readonly TimeSpan TransferStatusHold = TimeSpan.FromSeconds(8);
    private DateTime _transferStatusUntil;

    public bool HasTransferStatus => IsSendingFiles || IsReceivingFiles;

    /// 由界面上那个 1 秒 tick 驱动：到点了就把终态收回去，拖放区恢复成提示文案。
    public void RefreshTransferStatus()
    {
        if (_transferStatusUntil == default || DateTime.Now < _transferStatusUntil) return;
        ClearTransferStatus();
    }

    /// 下一次拖进来时把上一次的终态清掉，拖放区回到"把文件拖到这里"。
    public void ClearTransferStatus()
    {
        _transferStatusUntil = default;
        ClearSend();
        ClearReceive();
    }
    /// 每次进共享页都重读一遍：换网络、DHCP 续租之后，屏幕上留着旧地址只会把人带偏。
    private void EnsureLocalIpAddressDetected()
    {
        // 手填过对端就用对端，其次用发现到的第一台设备——只有"到它的路由"能说明该报哪个段的地址
        var probeTarget = !string.IsNullOrWhiteSpace(_peerHost) ? _peerHost.Trim()
            : _discoveredPeers.Count > 0 ? _discoveredPeers[0].Address : null;
        var detected = PhysicalNetwork.BestAddress(probeTarget) ?? "未识别，请手动查本机 IP";
        if (!string.Equals(_localIpAddress, detected, StringComparison.Ordinal)) LocalIpAddressText = detected;
    }

    public string ReceiveDirectory { get => _receiveDirectory; private set => SetProperty(ref _receiveDirectory, value); }
    public bool IsReceiveDirectoryCustom => !string.Equals(_receiveDirectory, AppPaths.ReceivedFilesDirectory, StringComparison.OrdinalIgnoreCase);

    private async Task SaveAutoAcceptFilesAsync(bool enabled)
    {
        var settings = await _peerSettingsService.LoadAsync();
        if (settings.AutoAcceptFiles == enabled) return;
        await _peerSettingsService.SaveSettingsAsync(settings with { AutoAcceptFiles = enabled });
        await AppLogger.InfoAsync($"File auto-accept {(enabled ? "enabled" : "disabled")}");
    }

    /// 界面上选的接收文件夹，传 null 或空表示回到默认目录。选完立刻生效，不用重开程序。
    public async Task SetReceiveDirectoryAsync(string? directory)
    {
        var settings = await _peerSettingsService.LoadAsync();
        if (string.Equals(settings.ReceiveDirectory?.Trim(), directory?.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        await _peerSettingsService.SaveSettingsAsync(settings with { ReceiveDirectory = directory });
        _peerLink?.ApplyReceiveDirectory(directory);
        ReceiveDirectory = PeerLink.ResolveReceiveDirectory(directory);
        OnPropertyChanged(nameof(IsReceiveDirectoryCustom));
        ReportStatus($"收到的文件保存到 {ReceiveDirectory}");
        await AppLogger.InfoAsync($"Receive directory changed to {ReceiveDirectory}");
    }

    public bool IsPeerConnected => _peerLink?.IsConnected == true;

    /// 直接显示在页面上的 6 位配对码，过期后自动变 null。
    public string? LocalPairingCode => _peerLink?.LocalPairingCode;
    public bool IsPairingCodeShown => LocalPairingCode is not null;
    public string PairingCountdownText { get => _pairingCountdownText; private set => SetProperty(ref _pairingCountdownText, value); }
    public string PeerHost { get => _peerHost; set { if (SetProperty(ref _peerHost, value)) { OnPropertyChanged(nameof(CanConnectPeer)); PeerConnectError = null; } } }
    public string PeerCode { get => _peerCode; set { if (SetProperty(ref _peerCode, value)) { OnPropertyChanged(nameof(CanConnectPeer)); PeerConnectError = null; } } }
    public bool CanConnectPeer => !string.IsNullOrWhiteSpace(_peerHost) && _peerCode.Trim().Length == 6 && _peerCode.Trim().All(char.IsDigit);
    public string? PeerConnectError { get => _peerConnectError; private set { if (SetProperty(ref _peerConnectError, value)) OnPropertyChanged(nameof(ShowConnectError)); } }
    public bool ShowConnectError => !string.IsNullOrEmpty(_peerConnectError);

    public string PeerStatusText => _peerLink switch
    {
        null => "共享未启用",
        _ when _peerLink.State == PeerLinkState.Connected => $"已连接 {_peerLink.RemoteName}",
        _ when LocalPairingCode is not null => "等待配对",
        _ when _peerLink.State == PeerLinkState.Connecting => "正在连接对端…",
        _ when _peerLink.State == PeerLinkState.Failed => "连接失败",
        _ => "未配对"
    };

    /// 状态行只放短句，细节挂在这上面。配对码在「对方连接我」那行已经大字显示，这里不重复占宽。
    /// 状态行和它的 ToolTip 是同一份判断的两个视图，必须一起通知，不能只刷一个。
    private void RaisePeerStatus()
    {
        OnPropertyChanged(nameof(PeerStatusText));
        OnPropertyChanged(nameof(PeerStatusDetail));
    }

    public string PeerStatusDetail => _peerLink switch
    {
        null => "共享未启用：还没生成过配对码，也没连过对端。",
        _ when LocalPairingCode is { } code => $"配对码 {code}，5 分钟内有效。",
        _ when _peerLink.State == PeerLinkState.Failed => _peerLink.LastError ?? "未知原因",
        _ => PeerStatusText
    };

    /// 界面每秒调一次：刷新倒计时。配对码一过期，页面上的码自己收回去。
    public void RefreshPairingCountdown()
    {
        var left = _peerLink?.PairingTimeLeft;
        PairingCountdownText = left is null ? string.Empty : $"剩 {(int)left.Value.TotalMinutes}:{left.Value.Seconds:00} 有效";
        OnPropertyChanged(nameof(LocalPairingCode));
        OnPropertyChanged(nameof(IsPairingCodeShown));
        RaisePeerStatus();
    }

    /// 没有配对过就既不建证书也不监听端口，避免第一次启动就悄悄往用户证书存储里写东西。
    public async Task LoadPeerStateAsync()
    {
        var settings = await _peerSettingsService.LoadAsync();
        PeerLocalName = settings.LocalName;
        EditableLocalName = settings.LocalName;
        _autoAcceptFiles = settings.AutoAcceptFiles;
        OnPropertyChanged(nameof(AutoAcceptFiles));
        ReceiveDirectory = PeerLink.ResolveReceiveDirectory(settings.ReceiveDirectory);
        OnPropertyChanged(nameof(IsReceiveDirectoryCustom));
        if (string.IsNullOrEmpty(_peerHost)) PeerHost = settings.LastPeerHost ?? string.Empty;
        _shareKeyboard = settings.ShareKeyboard;
        _shareMouse = settings.ShareMouse;
        OnPropertyChanged(nameof(ShareKeyboard));
        OnPropertyChanged(nameof(ShareMouse));
        LoadInputModes(settings);
        RefreshInputStats();
    }

    public async Task StartPeerIfNeededAsync()
    {
        var settings = await _peerSettingsService.LoadAsync();
        if (settings.PinnedPeers.Count == 0) return;
        await EnsureLink().StartAsync();
        RaisePeerStatus();
    }

    private LanDiscovery? _discovery;
    private IReadOnlyList<LanPeer> _discoveredPeers = [];
    /// 发现的设备列表在后台线程到达，由界面负责切回 UI 线程——和剪贴板那两条回调同一个套路。
    public Action<IReadOnlyList<LanPeer>>? PeersDiscoveredHandler { get; set; }
    public IReadOnlyList<LanPeer> DiscoveredPeers { get => _discoveredPeers; private set => SetProperty(ref _discoveredPeers, value); }
    public bool HasDiscoveredPeers => _discoveredPeers.Count > 0;
    public void ApplyDiscoveredPeers(IReadOnlyList<LanPeer> peers)
    {
        DiscoveredPeers = peers;
        OnPropertyChanged(nameof(HasDiscoveredPeers));
    }

    /// 真的打开这一页才开始收发广播：不碰共享的人不应该被 UDP 的防火墙弹窗打扰。
    private async Task StartDiscoveryIfNeededAsync()
    {
        if (_discovery is not null) return;
        var settings = await _peerSettingsService.LoadAsync();
        var discovery = new LanDiscovery(settings.Port + 1, () => PeerLocalName, settings.Port);
        discovery.PeersChanged += peers => PeersDiscoveredHandler?.Invoke(peers);
        _discovery = discovery;
        try { await discovery.StartAsync(); }
        catch (SocketException ex)
        {
            // 端口被别的程序占了就只能退回手输地址，共享本身还能用。
            _discovery = null;
            await AppLogger.ErrorAsync("LAN discovery could not bind its UDP port", ex);
        }
    }

    private readonly ObservableCollection<InputModeViewModel> _inputModes = [];
    private InputForwarder? _forwarder;
    private InputModeViewModel? _activeInputMode;
    private bool _isForwarding;
    private bool _isReceivingInput;
    private string _inputStatusText = "各用各的键鼠。";
    private string? _inputErrorText;

    public IReadOnlyList<InputModeViewModel> InputModes => _inputModes;
    public bool IsForwarding { get => _isForwarding; private set { if (SetProperty(ref _isForwarding, value)) RefreshInputStatus(); } }
    public bool IsReceivingInput { get => _isReceivingInput; private set { if (SetProperty(ref _isReceivingInput, value)) RefreshInputStatus(); } }
    public string InputStatusText { get => _inputStatusText; private set => SetProperty(ref _inputStatusText, value); }
    public string? InputErrorText { get => _inputErrorText; private set { if (SetProperty(ref _inputErrorText, value)) OnPropertyChanged(nameof(ShowInputError)); } }
    public bool ShowInputError => !string.IsNullOrEmpty(_inputErrorText);
    public AsyncRelayCommand ReleaseInputCommand { get; private set; } = null!;

    private void RefreshInputStatus()
    {
        InputStatusText = (_isForwarding, _isReceivingInput) switch
        {
            (true, _) => "已接管：这台的键盘鼠标正转给对端，按 Ctrl + Alt + Q 交回。",
            (false, true) => "已交出：对端正在用它的键盘鼠标操作这台。",
            _ => _activeInputMode?.Mode switch
            {
                InputMode.LocalControlsPeer => "共用本端键鼠：链路一连上就自动接管。",
                InputMode.PeerControlsLocal => "共用远端键鼠：等对面开始。",
                _ => "各用各的键鼠。"
            }
        };
    }

    private void WireInputCommands()
    {
        ReleaseInputCommand = new AsyncRelayCommand(ReleaseInputAsync);
    }

    /// 卡片本身就是单选按钮，选中状态由界面写进来。
    private void OnInputModeSelected(InputModeViewModel mode) => _ = ApplyInputModeAsync(mode.Index, announce: true);

    private async Task ApplyInputModeAsync(int index, bool announce)
    {
        if (_activeInputMode?.Index == index) return;
        var target = _inputModes.FirstOrDefault(candidate => candidate.Index == index);
        if (target is null) return;
        // 换卡片必须先交回，否则上一张卡片的钩子还挂在机器上。
        await ReleaseInputAsync();
        // 先定当前再刷 IsSelected：IsSelected 会回调进这个方法，先定下来就不会递归。
        _activeInputMode = target;
        foreach (var candidate in _inputModes) candidate.IsSelected = ReferenceEquals(candidate, target);
        RefreshInputStatus();
        await SaveInputModesAsync();
        if (_peerLink is { IsConnected: true } link)
        {
            if (announce) await link.SendInputModeAsync(index);
            if (index == (int)InputMode.LocalControlsPeer) await RequestInputArmAsync();
        }
    }

    /// 选完卡片就自动开始转发，不用再点一次：两台机器都是同一个人，中间那步确认只会变成麻烦。
    /// 链路连上时也会自动补一次，所以这个模式设好之后每次开机都直接生效。
    private async Task RequestInputArmAsync()
    {
        var link = _peerLink;
        if (link is null || !link.IsConnected) return;
        if (_activeInputMode?.Mode != InputMode.LocalControlsPeer || _isForwarding) return;
        InputErrorText = null;
        if (!await link.SendInputArmAsync((int)InputMode.LocalControlsPeer)) InputErrorText = "接管请求发不出去，链路可能已经断了。";
    }

    /// 对面选"共用本端"，这台自动切到"共用远端"，反过来也一样。对面不用再点一次。
    /// 但自动切的只是卡片，不是接管：真正开始屏蔽键盘仍要对面点"接管"并拿到这台确认。
    private void OnPeerInputModeChanged(int peerIndex) => _ = ApplyInputModeAsync(peerIndex switch
    {
        (int)InputMode.LocalControlsPeer => (int)InputMode.PeerControlsLocal,
        (int)InputMode.PeerControlsLocal => (int)InputMode.LocalControlsPeer,
        _ => (int)InputMode.Off
    }, announce: false);

    private async Task HandleInputArmAsync(int requested)
    {
        var link = _peerLink;
        if (link is null) return;
        if (_activeInputMode?.Mode != InputMode.PeerControlsLocal)
        {
            InputErrorText = "这台没选「共用远端键鼠」，不能接收对端的键盘鼠标。";
            await link.SendInputRejectAsync(InputErrorText);
            return;
        }
        link.SetAcceptingInput(true);
        IsReceivingInput = true;
        await link.SendInputAckAsync(requested);
        await AppLogger.InfoAsync("Accepting injected input from the peer");
    }

    private void StartForwarding(int mode)
    {
        if (_forwarder is not null) return;
        var link = _peerLink;
        if (link is null) return;
        ResetInputStats();
        _forwarder = new InputForwarder(
            async key => { Interlocked.Increment(ref _framesSent); _lastInputActivity = DateTimeOffset.Now; await link.SendKeyboardAsync(key); },
            async mouse => { Interlocked.Increment(ref _framesSent); _lastInputActivity = DateTimeOffset.Now; await link.SendMouseAsync(mouse); },
            () => _ = ReleaseInputAsync(),
            () => _shareKeyboard,
            () => _shareMouse);
        _forwarder.Start();
        IsForwarding = true;
        _ = AppLogger.InfoAsync($"Input forwarding armed, mode {mode}");
    }

    private void StopForwarding()
    {
        var forwarder = _forwarder;
        if (forwarder is null) return;
        _forwarder = null;
        forwarder.Dispose();
        IsForwarding = false;
    }

    private async Task ReleaseInputAsync()
    {
        StopForwarding();
        var link = _peerLink;
        if (link is { AcceptingInput: true })
        {
            link.SetAcceptingInput(false);
            await link.SendInputStopAsync();
        }
        IsReceivingInput = false;
        RefreshInputStatus();
    }

    private async Task SaveInputModesAsync()
    {
        var settings = await _peerSettingsService.LoadAsync();
        var names = _inputModes.Select(mode => mode.Name).ToList();
        var active = _activeInputMode?.Index ?? 0;
        if (settings.KeyboardMouseActive == active && settings.KeyboardMouseNames.SequenceEqual(names)) return;
        await _peerSettingsService.SaveSettingsAsync(settings with { KeyboardMouseNames = names, KeyboardMouseActive = active });
    }

    private void LoadInputModes(PeerSettings settings)
    {
        _inputModes.Clear();
        var names = settings.KeyboardMouseNames.Count == 3 ? settings.KeyboardMouseNames : ["各用各的键鼠", "共用本端键鼠", "共用远端键鼠"];
        for (var index = 0; index < names.Count; index++)
            _inputModes.Add(new InputModeViewModel(names[index], index, renamed => { _ = SaveInputModesAsync(); }, OnInputModeSelected));
        // 先定住当前卡片再打勾：IsSelected 会回调进换卡片逻辑，先定下来启动时就不会白白交回一次。
        _activeInputMode = _inputModes[Math.Clamp(settings.KeyboardMouseActive, 0, _inputModes.Count - 1)];
        foreach (var candidate in _inputModes) candidate.IsSelected = ReferenceEquals(candidate, _activeInputMode);
        RefreshInputStatus();
    }

    private bool _shareKeyboard = true;
    private bool _shareMouse = true;
    private long _framesSent;
    private long _framesApplied;
    private long _injectFailures;
    private DateTimeOffset? _lastInputActivity;
    private string _inputStatsText = "还没有转发过输入。";

    public bool ShareKeyboard { get => _shareKeyboard; set { if (SetProperty(ref _shareKeyboard, value)) _ = SaveShareFlagsAsync(); } }
    public bool ShareMouse { get => _shareMouse; set { if (SetProperty(ref _shareMouse, value)) _ = SaveShareFlagsAsync(); } }
    public string InputStatsText { get => _inputStatsText; private set => SetProperty(ref _inputStatsText, value); }

    private async Task SaveShareFlagsAsync()
    {
        var settings = await _peerSettingsService.LoadAsync();
        if (settings.ShareKeyboard == _shareKeyboard && settings.ShareMouse == _shareMouse) return;
        await _peerSettingsService.SaveSettingsAsync(settings with { ShareKeyboard = _shareKeyboard, ShareMouse = _shareMouse });
    }

    /// 界面每秒刷一次。转发中的时候人最需要的就是"到底有没有东西过去"这个数字。
    public void RefreshInputStats()
    {
        var sent = Interlocked.Read(ref _framesSent);
        var applied = Interlocked.Read(ref _framesApplied);
        var failed = Interlocked.Read(ref _injectFailures);
        var devices = $"键盘{(_shareKeyboard ? "开" : "关")} · 鼠标{(_shareMouse ? "开" : "关")}";
        var last = _lastInputActivity is { } when ? $" · 最近 {when.ToLocalTime():HH:mm:ss}" : string.Empty;
        InputStatsText = _activeInputMode?.Mode switch
        {
            InputMode.LocalControlsPeer => $"这台发出 {sent} 帧 · {devices}{last}",
            InputMode.PeerControlsLocal => $"这台注入 {applied} 帧 · 失败 {failed} 次 · {devices}{last}",
            _ => "还没有转发过输入。"
        };
    }

    private void RecordInjected(bool succeeded)
    {
        Interlocked.Increment(ref succeeded ? ref _framesApplied : ref _injectFailures);
        _lastInputActivity = DateTimeOffset.Now;
    }

    private void ResetInputStats()
    {
        Interlocked.Exchange(ref _framesSent, 0);
        Interlocked.Exchange(ref _framesApplied, 0);
        Interlocked.Exchange(ref _injectFailures, 0);
        _lastInputActivity = null;
        RefreshInputStats();
    }

    private async Task GeneratePairingCodeAsync()    {
        var link = EnsureLink();
        await link.StartAsync();
        link.BeginPairing();
        RefreshPairingCountdown();
        ReportStatus("把配对码告诉对方，5 分钟内有效");
    }

    private Task CancelPairingCodeAsync()
    {
        _peerLink?.CancelPairing();
        RefreshPairingCountdown();
        return Task.CompletedTask;
    }

    private async Task ConnectPeerAsync()
    {
        if (!CanConnectPeer) { PeerConnectError = "请填写对端 IP 和 6 位配对码。"; return; }
        var link = EnsureLink();
        await link.StartAsync();
        if (!await link.ConnectAsync(PeerHost.Trim(), PeerCode.Trim()))
        {
            PeerConnectError = link.LastError ?? "连不上对端，请确认两台电脑在同一局域网。";
            return;
        }
        PeerConnectError = null;
        ReportStatus("剪贴板共享已连接");
    }

    private async Task DisconnectPeerAsync()
    {
        if (_peerLink is null) return;
        await _peerLink.DisconnectAsync();
        RaisePeerStatus();
    }

    public async Task SendClipboardTextAsync(string text)
    {
        var link = _peerLink;
        if (link is null || !link.IsConnected) return;
        if (await link.SendTextAsync(text)) await AppLogger.InfoAsync($"Clipboard synced to peer: {text.Length} chars");
    }

    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        Status = "正在检测显示器与 DDC/CI 状态…";
        try
        {
            var savedPreferences = await _monitorPreferencesService.LoadAsync();
            _activeMappings.Clear();
            _scannedKeys.Clear();
            var scanned = await _discoveryService.ScanAsync(new Progress<MonitorDevice>(device => MergeMonitor(device, savedPreferences)));
            for (var index = Monitors.Count - 1; index >= 0; index--) if (!_scannedKeys.Contains(Monitors[index].StableId)) Monitors.RemoveAt(index);
            MergeProfiles(await _profileService.LoadOrCreateDefaultsAsync(scanned));
            await RefreshProjectionModeAsync();
            UpdateCurrentProfileFromInputs();
            UpdateSteadyStateStatus();
        }
        catch (Exception ex) { Status = $"检测失败：{ex.Message}"; _ = AppLogger.ErrorAsync("Refresh failed", ex); }
        finally { IsRefreshing = false; }
    }

    private readonly HashSet<string> _scannedKeys = new(StringComparer.Ordinal);

    // 每台显示器读完就上报，卡片逐台出现；合并时保留 ViewModel 实例，避免整表 Clear 让 ItemsControl 重建所有卡片。
    private void MergeMonitor(MonitorDevice device, IReadOnlyList<MonitorPreferences> savedPreferences)
    {
        var key = device.StableId ?? device.Id;
        var pref = savedPreferences.FirstOrDefault(item => item.StableId == device.StableId);
        var mapping = pref?.InputMappings ?? _defaultInputMapping;
        _activeMappings[device.Id] = mapping;
        var enriched = device with { CustomName = pref?.CustomName ?? device.CustomName };
        if (Monitors.FirstOrDefault(item => item.StableId == key) is { } existing) existing.UpdateFrom(enriched, mapping);
        else Monitors.Add(new MonitorViewModel(enriched, _ddcCiService, mapping, message => Status = message, _monitorPreferencesService));
        _scannedKeys.Add(key);
    }

    private void MergeProfiles(IReadOnlyList<SwitchProfile> profiles)
    {
        var existing = new Dictionary<string, ProfileViewModel>(StringComparer.Ordinal);
        for (var index = 0; index < Profiles.Count; index++) existing[Profiles[index].Profile.Id ?? Profiles[index].Profile.Name] = Profiles[index];
        var alive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            var key = profile.Id ?? profile.Name;
            if (!alive.Add(key)) continue;
            if (existing.TryGetValue(key, out var viewModel)) viewModel.UpdateProfile(profile);
            else Profiles.Add(new ProfileViewModel(profile, ExecuteProfileAsync, _profileService, SaveCurrentInputsToProfileAsync) { ShortcutDisplayText = profile.Name == "台式机" ? "Ctrl + Alt + 1" : profile.Name == "笔记本" ? "Ctrl + Alt + 2" : "未设置" });
        }
        for (var index = Profiles.Count - 1; index >= 0; index--) if (!alive.Contains(Profiles[index].Profile.Id ?? Profiles[index].Profile.Name)) Profiles.RemoveAt(index);
    }
    private async Task SetProjectionAsync(ProjectionMode mode)
    {
        Status = $"正在切换到{GetProjectionName(mode)}…";
        var result = await _projectionService.ApplyAsync(mode);
        if (result.Succeeded)
        {
            if (result.AppliedMode is { } detectedMode) { SelectProjectionMode(detectedMode); Status = $"已切换到{GetProjectionName(detectedMode)}"; }
            else { SelectProjectionMode(ProjectionMode.Unchanged); Status = $"Windows 已执行{GetProjectionName(mode)}，但当前模式未能检测"; }
        }
        else Status = $"投影模式切换失败：{result.ErrorMessage}";
    }
    private async Task RefreshProjectionModeAsync()
    {
        var mode = await _projectionService.RefreshCurrentModeAsync();
        CurrentProjectionMode = mode;
        foreach (var item in ProjectionModes) item.IsSelected = item.Mode == mode;
        OnPropertyChanged(nameof(CurrentProjectionLabel));
    }
    private void SelectProjectionMode(ProjectionMode mode) { foreach (var item in ProjectionModes) item.IsSelected = item.Mode == mode; OnPropertyChanged(nameof(CurrentProjectionLabel)); }
    private Task ExecuteProfileAsync(SwitchProfile profile) => ExecuteProfileAsync(Profiles.FirstOrDefault(item => item.Profile == profile) ?? new ProfileViewModel(profile, ExecuteProfileAsync));

    private async Task ExecuteProfileAsync(ProfileViewModel profile)
    {
        if (IsSwitching) return;
        IsSwitching = true;
        try
        {
            Status = $"正在切换到{profile.Name}…";
            var result = await _profileExecutionService.ApplyAsync(profile.Profile);
            foreach (var monitor in Monitors)
                if (result.InputResults.TryGetValue(monitor.Id, out var input) && input.Succeeded)
                    await monitor.ReadInputCommand.ExecuteAsync();
            profile.LastSwitchResult = result.InputResults.Values.All(x => x.Succeeded) ? "切换成功" : "部分显示器切换失败";
            // 刚切完那一下，显示器常常还在报过渡值，读出来是"未知输入源"，
            // 拿输入值去反推当前是哪个按钮必然推不出来，选中态就掉了。先按"我刚切到它"标上。
            CurrentProfile = profile;
            Status = $"已切换到{profile.Name}";
            _ = RevalidateAfterSwitchAsync();
        }
        finally { IsSwitching = false; }
    }

    /// 等显示器把新输入源稳定报出来之后再看一眼，顺手把"未知输入源"刷成真实值。
    private async Task RevalidateAfterSwitchAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetimeCancellation.Token);
            foreach (var monitor in Monitors.Where(m => m.IsDdcSupported)) await monitor.ReadInputCommand.ExecuteAsync();
            UpdateCurrentProfileFromInputs();
            UpdateSteadyStateStatus();
        }
        catch (OperationCanceledException) { }
    }

    private Task SwitchDefaultProfileAsync(string id, string legacyName) => Profiles.FirstOrDefault(p => p.Profile.Id == id) is { } profile ? ExecuteProfileAsync(profile) : Profiles.FirstOrDefault(p => p.Name == legacyName) is { } legacy ? ExecuteProfileAsync(legacy) : Task.CompletedTask;
    private async Task SaveCurrentInputsToProfileAsync(ProfileViewModel profile)
    {
        var candidates = Monitors.Where(m => m.IsDdcSupported && !string.IsNullOrWhiteSpace(m.StableId)).OrderBy(m => m.DisplayNumber).ToArray();
        var targets = new List<SwitchProfileMonitor>();
        foreach (var monitor in candidates)
        {
            var value = await monitor.ReadCurrentInputValueAsync();
            if (value is null || !_activeMappings.TryGetValue(monitor.Id, out var mapping))
            { Status = $"无法保存 {profile.Name}：{monitor.DisplayTitle} 当前输入未识别，请先学习输入源"; return; }
            var source = mapping.ToSource(value.Value);
            if (source == MonitorInputSource.Unknown) { Status = $"无法保存 {profile.Name}：{monitor.DisplayTitle} 当前输入未识别，请先学习输入源"; return; }
            var kind = source switch { MonitorInputSource.DisplayPort => InputSourceKind.DisplayPort, MonitorInputSource.UsbC => InputSourceKind.UsbC, MonitorInputSource.Hdmi => InputSourceKind.Hdmi, _ => throw new InvalidOperationException() };
            targets.Add(new SwitchProfileMonitor { StableId = monitor.StableId, InputKind = kind, Input = value.Value });
        }
        var updated = new SwitchProfile { Id = profile.Profile.Id, Name = profile.Name, ProjectionMode = profile.ProjectionMode, Monitors = targets };
        await _profileService.UpdateAsync(updated);
        profile.ReplaceProfile(updated);
        UpdateCurrentProfileFromInputs();
        Status = $"已保存 {profile.Name} 配置：{profile.ConnectionSummary}";
    }

    /// 只改已有的按钮：新建入口已经去掉了，所以这里只会命中 UpdateAsync。
    public async Task SaveProfileAsync(SwitchProfile profile)
    {
        await _profileService.UpdateAsync(profile);
        MergeProfiles(await _profileService.LoadAsync());
        UpdateCurrentProfileFromInputs();
        UpdateSteadyStateStatus();
        Status = $"已保存：{profile.Name}";
    }

    private void UpdateCurrentProfileFromInputs()
    {
        var current = Monitors.Where(m => m.IsDdcSupported).ToDictionary(m => m.StableId, m => m.CurrentInputValue, StringComparer.Ordinal);
        // 一台都读不出输入源时不清空选中态：这时候"没匹配上"只是因为没证据，不是因为真的切到了别的按钮。
        if (current.Values.Any(value => value is not null))
            CurrentProfile = Profiles.FirstOrDefault(profile => ProfileStateMatcher.Matches(profile.Profile, current, _activeMappings));
    }
    public bool IsMixedProfileState => false;
    public IReadOnlyList<MonitorIdentificationTarget> IdentificationTargets => Monitors.Select(m => new MonitorIdentificationTarget(m.DisplayNumber, m.StableId, m.DisplayName, m.Bounds)).ToList();
    private async Task IdentifyMonitorsAsync() { if (_identificationPresenter is null) { Status = "已生成显示器编号"; return; } await _identificationPresenter.ShowAsync(IdentificationTargets, TimeSpan.FromSeconds(1.8)); Status = "显示器标识已完成"; }
    public AsyncRelayCommand RefreshMonitorsCommand => RefreshCommand;
    private async Task SwitchAllAsync(string target, Func<MonitorViewModel, Task> switchMonitor)
    {
        if (Monitors.Count == 0) { Status = "尚未检测到显示器"; return; }
        Status = $"正在切换到{target}…";
        await Task.WhenAll(Monitors.Select(switchMonitor));
        Status = $"已完成{target}切换请求";
    }
    private static string GetProjectionName(ProjectionMode mode) => mode switch { ProjectionMode.Internal => "仅电脑屏幕", ProjectionMode.Duplicate => "复制", ProjectionMode.Extend => "扩展", ProjectionMode.External => "仅第二屏幕", _ => "不修改" };
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        _lifetimeCancellation.Cancel();
        if (_identificationPresenter is IDisposable presenter) presenter.Dispose();
        StopForwarding();
        if (_discovery is not null) { var discovery = _discovery; _discovery = null; await discovery.DisposeAsync(); }
        if (_peerLink is not null) { var link = _peerLink; _peerLink = null; await link.DisposeAsync(); }
        await _ddcCiService.ShutdownAsync(timeout);
    }
    public void RequestShutdown()
    {
        _lifetimeCancellation.Cancel();
        if (_identificationPresenter is IDisposable presenter) presenter.Dispose();
        _ddcCiService.RequestShutdown();
    }
    public async ValueTask DisposeAsync() { await ShutdownAsync(TimeSpan.FromMilliseconds(350)); _lifetimeCancellation.Dispose(); }
}

public sealed record MonitorInputRequest(MonitorViewModel Monitor, uint Value);
