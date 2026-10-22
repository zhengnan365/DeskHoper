using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using MonitorSwitch.Services;
using MonitorSwitch.ViewModels;

namespace MonitorSwitch.Views;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty DisplayStatusTextProperty = DependencyProperty.Register(nameof(DisplayStatusText), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
    private readonly MainViewModel _viewModel;
    private HotkeyService? _hotkeys;
    private ClipboardService? _clipboard;
    private Point _monitorDragStart;
    private MonitorViewModel? _draggedMonitor;
    private bool _monitorTitleDoubleClick;
    private readonly List<string> _monitorOrder = [];
    private int _statusVersion;
    private readonly SettingsService _appSettings = new();
    private TrayService? _tray;
    private bool _exitRequested;
    // 关闭行为在启动时读进内存：OnClosing 里绝对不能 await，
    // WPF 是在处理器同步返回后才看 e.Cancel 的，一 await 就等于放行了这次关闭。
    private bool _closeActionDecided;
    private bool _closeHidesToTray = true;
    private readonly DispatcherTimer _pairingTick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _inputStatsTick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _transferTick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    public string DisplayStatusText { get => (string)GetValue(DisplayStatusTextProperty); private set => SetValue(DisplayStatusTextProperty, value); }
    public MainWindow() { InitializeComponent(); _viewModel = new MainViewModel(new MonitorIdentifyPresenter()); DataContext = _viewModel; DisplayStatusText = _viewModel.CurrentStatusText; _viewModel.PropertyChanged += ViewModel_PropertyChanged; Loaded += async (_, _) => { await _viewModel.RefreshAsync(); ConfigureMonitorView(); await _viewModel.StartPeerIfNeededAsync(); await _viewModel.LoadPeerStateAsync(); }; SourceInitialized += OnSourceInitialized; Closing += OnClosing; Loaded += async (_, _) => { var app = await new SettingsService().LoadAsync(); _closeActionDecided = app.CloseActionDecided; _closeHidesToTray = app.MinimizeToTrayOnClose; _tray ??= new TrayService(ShowFromTray, ExitFromTray, () => _viewModel.DesktopSwitchCommand.Execute(null), () => _viewModel.LaptopSwitchCommand.Execute(null)); }; }
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        _hotkeys = new HotkeyService(source);
        _hotkeys.Register(HotkeyService.ModifierKeys.Control | HotkeyService.ModifierKeys.Alt, 0x31, () => _viewModel.DesktopSwitchCommand.Execute(null), out _);
        _hotkeys.Register(HotkeyService.ModifierKeys.Control | HotkeyService.ModifierKeys.Alt, 0x32, () => _viewModel.LaptopSwitchCommand.Execute(null), out _);
        _clipboard = new ClipboardService(source);
        _clipboard.LocalTextChanged += text => _ = _viewModel.SendClipboardTextAsync(text);
        _clipboard.TextRejected += message => _viewModel.ReportStatus(message);
        // 对端内容来自后台线程。这里不用 Progress<T>：它在 SourceInitialized 阶段抓到的同步上下文不可靠，
        // 实测回调会被静默丢掉；显式切回 Dispatcher 才保证在 UI 线程上执行。
        _viewModel.RemoteClipboardTextHandler = text => _ = Dispatcher.InvokeAsync(() => _clipboard?.ApplyRemoteText(text));
        _viewModel.RemoteClipboardFilesHandler = paths => _ = Dispatcher.InvokeAsync(() => _clipboard?.ApplyRemoteFiles(paths));
        _viewModel.PeersDiscoveredHandler = peers => _ = Dispatcher.InvokeAsync(() => _viewModel.ApplyDiscoveredPeers(peers));
        _viewModel.PeerOfferHandler = offer => Dispatcher.InvokeAsync(() => AskFileOffer(offer)).Task;
        _viewModel.PeerConnected += () => _clipboard.CaptureBaseline();
        _pairingTick.Tick += (_, _) => _viewModel.RefreshPairingCountdown();
        // 传输结果只在屏幕上停一小会儿，到点自己收回去；只有真有文案挂着才跑这一秒一跳。
        _transferTick.Tick += (_, _) => { _viewModel.RefreshTransferStatus(); if (!_viewModel.HasTransferStatus) _transferTick.Stop(); };
        // 只有停在这一页时才刷数字，离开就别白烧定时器。
        _inputStatsTick.Tick += (_, _) => { if (_viewModel.ActiveSection == AppSection.KeyboardMouse) _viewModel.RefreshInputStats(); };
        _inputStatsTick.Start();
    }
    private bool AskFileOffer(FileOffer offer)
    {
        if (_viewModel.AutoAcceptFiles) return true;
        var editor = new FileOfferViewModel(offer, _viewModel.ReceiveDirectory, autoAcceptEnabled: true);
        if (new FileReceiveWindow { Owner = this, DataContext = editor }.ShowDialog() != true) return false;
        if (editor.AutoAcceptChosen) _viewModel.AutoAcceptFiles = true;
        return true;
    }
    private void SendFiles_DragOver(object sender, DragEventArgs e)
    {
        var canDrop = _viewModel.IsPeerConnected && e.Data.GetDataPresent(DataFormats.FileDrop);
        // 重新拖进来就把上一次的"发送成功/失败"清掉，拖放区回到提示文案
        if (canDrop) _viewModel.ClearTransferStatus();
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void SendFiles_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
        // 只发真实存在的文件：拖进来一个已经移走的项，对面会拿到打不开的东西。
        var paths = dropped.Where(File.Exists).ToList();
        if (paths.Count == 0) { _viewModel.ReportStatus("拖进来的不是本机现有的文件，没有发送。"); return; }
        await _viewModel.SendClipboardFilesAsync(paths);
    }

    private void ChooseReceiveDirectory_Click(object sender, RoutedEventArgs e)    {
        var dialog = new OpenFolderDialog { Title = "选择收到文件的存放文件夹", Multiselect = false, InitialDirectory = _viewModel.ReceiveDirectory };
        if (dialog.ShowDialog(this) != true) return;
        _ = _viewModel.SetReceiveDirectoryAsync(dialog.FolderName);
    }
    /// <summary>托盘唤回：藏起来时窗口是 Hide()，这里反过来即可。Topmost 晃一下是为了压过别的窗口抢前台。</summary>
    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>点关闭到底收托盘还是退出：没定过就问一次，选完记住。返回 false 表示这次关闭作废。</summary>
    private bool ShouldHideToTray()
    {
        if (_closeActionDecided) return _closeHidesToTray;
        var choice = new CloseChoiceWindow { Owner = this };
        choice.ShowDialog();
        if (!choice.Answered) return false;
        if (choice.Remember)
        {
            _closeActionDecided = true;
            _closeHidesToTray = choice.ChoseTray;
            _ = SaveCloseActionAsync(choice.ChoseTray);
        }
        return choice.ChoseTray;
    }

    private async Task SaveCloseActionAsync(bool hideToTray)
    {
        try { await _appSettings.SaveAsync(await _appSettings.LoadAsync() with { CloseActionDecided = true, MinimizeToTrayOnClose = hideToTray }); }
        catch (Exception ex) { _ = AppLogger.ErrorAsync("Close action could not be saved", ex); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested && ShouldHideToTray())
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _tray?.Dispose();
        _tray = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _ = AppLogger.ShutdownAsync("Closing entered", stopwatch.ElapsedMilliseconds);
        try { _viewModel.RequestShutdown(); _ = AppLogger.ShutdownAsync("cancel background tasks / close identify windows", stopwatch.ElapsedMilliseconds); }
        catch (Exception ex) { _ = AppLogger.ErrorAsync("[Shutdown] request shutdown exception", ex); }
        try { _hotkeys?.Dispose(); _ = AppLogger.ShutdownAsync("unregister hotkeys", stopwatch.ElapsedMilliseconds); }
        catch (Exception ex) { _ = AppLogger.ErrorAsync("[Shutdown] unregister hotkeys exception", ex); }
        try { _clipboard?.Dispose(); _clipboard = null; }
        catch (Exception ex) { _ = AppLogger.ErrorAsync("[Shutdown] remove clipboard listener exception", ex); }
        _ = AppLogger.ShutdownAsync("main window close allowed", stopwatch.ElapsedMilliseconds);
    }
    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPairingCodeShown))
        {
            // 只在配对码真的显示时才走这一秒一次的倒计时，码一过期就自动停下。
            if (_viewModel.IsPairingCodeShown) _pairingTick.Start(); else _pairingTick.Stop();
        }
        if (e.PropertyName == nameof(MainViewModel.HasTransferStatus) && _viewModel.HasTransferStatus) _transferTick.Start();
        if (e.PropertyName != nameof(MainViewModel.CurrentStatusText)) return;
        var version = ++_statusVersion;
        var status = _viewModel.CurrentStatusText;
        const string marker = "配置：";
        var markerIndex = status.IndexOf(marker, StringComparison.Ordinal);
        if (!status.StartsWith("已保存", StringComparison.Ordinal) || markerIndex < 0) { DisplayStatusText = status; return; }
        DisplayStatusText = $"已保存：{status[(markerIndex + marker.Length)..]}";
        await Task.Delay(TimeSpan.FromSeconds(1.8));
        if (version != _statusVersion) return;
        DisplayStatusText = _viewModel.CurrentProfile is { } profile ? $"当前：{profile.Name}" : _viewModel.IsMixedProfileState ? "当前：混合输入" : _viewModel.Monitors.Count > 0 ? $"检测到 {_viewModel.Monitors.Count} 台显示器" : "已就绪";
    }
    private void ProfileName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement element || element.DataContext is not ProfileViewModel profile) return;
        ShowProfileEditor(profile);
        e.Handled = true;
    }
    private void ShowProfileEditor(ProfileViewModel profile)
    {
        var editor = new ProfileEditViewModel(profile.Profile, _viewModel.Monitors);
        if (editor.Rows.Count == 0) { MessageBox.Show(this, "没有可以通过 DDC/CI 控制输入源的显示器。", "无法编辑", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (new ProfileEditWindow { Owner = this, DataContext = editor }.ShowDialog() != true) return;
        _ = _viewModel.SaveProfileAsync(editor.Build());
    }
    private void LocalName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { _viewModel.BeginRenameLocalNameCommand.Execute(null); e.Handled = true; }
    }
    private void LocalNameRename_Click(object sender, RoutedEventArgs e) => _viewModel.BeginRenameLocalNameCommand.Execute(null);
    private void LocalNameEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _viewModel.CommitRenameLocalNameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _viewModel.CancelRenameLocalNameCommand.Execute(null); e.Handled = true; }
    }
    private void LocalNameEditor_LostFocus(object sender, RoutedEventArgs e) { if (Mouse.LeftButton != MouseButtonState.Pressed) _viewModel.CommitRenameLocalNameCommand.Execute(null); }
    private void InputModeName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement element && element.DataContext is InputModeViewModel mode) { mode.BeginRenameCommand.Execute(null); e.Handled = true; }
    }
    private void InputModeRename_Click(object sender, RoutedEventArgs e)
    {
        // 和切换按钮卡片一样：菜单项的 DataContext 继承不可靠，一律从 PlacementTarget 拿。
        if ((sender as MenuItem)?.Parent is ContextMenu menu && menu.PlacementTarget is FrameworkElement target && target.DataContext is InputModeViewModel mode)
            mode.BeginRenameCommand.Execute(null);
    }
    private void InputModeNameEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not InputModeViewModel mode) return;
        if (e.Key == Key.Enter) { mode.CommitRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { mode.CancelRenameCommand.Execute(null); e.Handled = true; }
    }
    private void InputModeNameEditor_LostFocus(object sender, RoutedEventArgs e) { if (sender is TextBox editor && editor.DataContext is InputModeViewModel mode && Mouse.LeftButton != MouseButtonState.Pressed) mode.CommitRenameCommand.Execute(null); }
    private void MonitorName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement element || element.DataContext is not MonitorViewModel monitor)
        {
            if (e.ClickCount == 1) _monitorTitleDoubleClick = false;
            return;
        }
        _monitorTitleDoubleClick = true;
        monitor.BeginRenameCommand.Execute(null);
        e.Handled = true;
    }
    private void MonitorNameEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not MonitorViewModel monitor) return;
        if (e.Key == Key.Enter) { monitor.CommitRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { monitor.CancelRenameCommand.Execute(null); e.Handled = true; }
    }
    private void MonitorNameEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        _monitorTitleDoubleClick = false;
        if (sender is TextBox editor && editor.DataContext is MonitorViewModel monitor && Mouse.LeftButton != MouseButtonState.Pressed)
            monitor.CommitRenameCommand.Execute(null);
    }
    private void LearnInput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is not null)
            new InputLearningWindow { Owner = this, DataContext = element.DataContext }.ShowDialog();
    }
    private void ExternalMonitorFilter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is MonitorViewModel monitor && monitor.IsDdcSupported;
    }
    private void ConfigureMonitorView()
    {
        foreach (var monitor in _viewModel.Monitors)
            if (!_monitorOrder.Contains(monitor.StableId, StringComparer.Ordinal)) _monitorOrder.Add(monitor.StableId);
        if (FindResource("ExternalMonitorsView") is CollectionViewSource source && source.View is ListCollectionView view)
            view.CustomSort = Comparer<object>.Create((left, right) => _monitorOrder.IndexOf(((MonitorViewModel)left).StableId).CompareTo(_monitorOrder.IndexOf(((MonitorViewModel)right).StableId)));
    }
    private void MonitorTitle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // 双击改名时手指一定会抖几像素，不拦的话拖拽排序会把这一下抢走，名字框就永远弹不出来。
        if (_monitorTitleDoubleClick) return;
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement element || element.DataContext is not MonitorViewModel monitor) return;
        if (_draggedMonitor is null) { _draggedMonitor = monitor; _monitorDragStart = e.GetPosition(this); }
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _monitorDragStart.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y - _monitorDragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
            DragDrop.DoDragDrop(element, monitor, DragDropEffects.Move);
    }
    private void MonitorCard_Drop(object sender, DragEventArgs e)
    {
        if (_draggedMonitor is null || sender is not FrameworkElement targetElement || targetElement.DataContext is not MonitorViewModel target || target == _draggedMonitor) return;
        var from = _monitorOrder.IndexOf(_draggedMonitor.StableId); var to = _monitorOrder.IndexOf(target.StableId);
        if (from >= 0 && to >= 0) { _monitorOrder.RemoveAt(from); _monitorOrder.Insert(to, _draggedMonitor.StableId); ConfigureMonitorView(); }
        _draggedMonitor = null;
    }
}
