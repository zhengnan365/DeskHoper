using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Data;
using MonitorSwitch.Services;
using MonitorSwitch.ViewModels;

namespace MonitorSwitch.Views;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty DisplayStatusTextProperty = DependencyProperty.Register(nameof(DisplayStatusText), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
    private readonly MainViewModel _viewModel;
    private HotkeyService? _hotkeys;
    private Point _monitorDragStart;
    private MonitorViewModel? _draggedMonitor;
    private readonly List<string> _monitorOrder = [];
    private int _statusVersion;
    public string DisplayStatusText { get => (string)GetValue(DisplayStatusTextProperty); private set => SetValue(DisplayStatusTextProperty, value); }
    public MainWindow() { InitializeComponent(); _viewModel = new MainViewModel(new MonitorIdentifyPresenter()); DataContext = _viewModel; DisplayStatusText = _viewModel.CurrentStatusText; _viewModel.PropertyChanged += ViewModel_PropertyChanged; Loaded += async (_, _) => { await _viewModel.RefreshAsync(); ConfigureMonitorView(); }; SourceInitialized += OnSourceInitialized; Closing += OnClosing; }
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        _hotkeys = new HotkeyService(source);
        _hotkeys.Register(HotkeyService.ModifierKeys.Control | HotkeyService.ModifierKeys.Alt, 0x31, () => _viewModel.DesktopSwitchCommand.Execute(null), out _);
        _hotkeys.Register(HotkeyService.ModifierKeys.Control | HotkeyService.ModifierKeys.Alt, 0x32, () => _viewModel.LaptopSwitchCommand.Execute(null), out _);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _ = AppLogger.ShutdownAsync("Closing entered", stopwatch.ElapsedMilliseconds);
        try { _viewModel.RequestShutdown(); _ = AppLogger.ShutdownAsync("cancel background tasks / close identify windows", stopwatch.ElapsedMilliseconds); }
        catch (Exception ex) { _ = AppLogger.ErrorAsync("[Shutdown] request shutdown exception", ex); }
        try { _hotkeys?.Dispose(); _ = AppLogger.ShutdownAsync("unregister hotkeys", stopwatch.ElapsedMilliseconds); }
        catch (Exception ex) { _ = AppLogger.ErrorAsync("[Shutdown] unregister hotkeys exception", ex); }
        _ = AppLogger.ShutdownAsync("main window close allowed", stopwatch.ElapsedMilliseconds);
    }
    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
        if (e.ClickCount == 2 && sender is FrameworkElement element && element.DataContext is ProfileViewModel profile)
        { profile.BeginRenameCommand.Execute(null); e.Handled = true; }
    }
    private void ProfileNameEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not ProfileViewModel profile) return;
        if (e.Key == Key.Enter) { profile.CommitRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { profile.CancelRenameCommand.Execute(null); e.Handled = true; }
    }
    private void ProfileNameEditor_LostFocus(object sender, RoutedEventArgs e) { if (sender is TextBox editor && editor.DataContext is ProfileViewModel profile && Mouse.LeftButton != MouseButtonState.Pressed) profile.CommitRenameCommand.Execute(null); }
    private void MonitorName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement element)
        {
            var property = element.DataContext?.GetType().GetProperty("BeginRenameCommand");
            (property?.GetValue(element.DataContext) as System.Windows.Input.ICommand)?.Execute(null);
            e.Handled = true;
        }
    }
    private void MonitorNameEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not MonitorViewModel monitor) return;
        if (e.Key == Key.Enter) { monitor.CommitRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { monitor.CancelRenameCommand.Execute(null); e.Handled = true; }
    }
    private void MonitorNameEditor_LostFocus(object sender, RoutedEventArgs e)
    {
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
