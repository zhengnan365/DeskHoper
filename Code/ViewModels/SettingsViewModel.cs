using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using MonitorSwitch.Models;
using MonitorSwitch.Services;

namespace MonitorSwitch.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = new();
    private AppSettings _settings = new();
    private string _statusMessage = string.Empty;
    public ObservableCollection<MonitorDevice> Monitors { get; } = [];
    public ObservableCollection<SwitchProfile> Profiles { get; } = [];
    public bool StartWithWindows { get => _settings.StartWithWindows; set { _settings = _settings with { StartWithWindows = value }; OnPropertyChanged(); } }
    public bool StartMinimized { get => _settings.StartMinimized; set { _settings = _settings with { StartMinimized = value }; OnPropertyChanged(); } }
    public bool ShowSuccessNotifications { get => _settings.ShowSuccessNotifications; set { _settings = _settings with { ShowSuccessNotifications = value }; OnPropertyChanged(); } }
    public bool MinimizeToTrayOnClose { get => _settings.MinimizeToTrayOnClose; set { _settings = _settings with { MinimizeToTrayOnClose = value }; OnPropertyChanged(); } }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand OpenLogsCommand { get; }
    public AsyncRelayCommand TestDdcCommand { get; }
    public SettingsViewModel() { SaveCommand = new AsyncRelayCommand(SaveAsync); OpenLogsCommand = new AsyncRelayCommand(OpenLogsAsync); TestDdcCommand = new AsyncRelayCommand(TestDdcAsync); }
    public async Task LoadAsync(IReadOnlyCollection<MonitorDevice>? monitors = null, IReadOnlyCollection<SwitchProfile>? profiles = null)
    {
        _settings = await _settingsService.LoadAsync();
        if (monitors is not null) { Monitors.Clear(); foreach (var monitor in monitors) Monitors.Add(monitor); }
        if (profiles is not null) { Profiles.Clear(); foreach (var profile in profiles) Profiles.Add(profile); }
        OnPropertyChanged(nameof(StartWithWindows)); OnPropertyChanged(nameof(StartMinimized)); OnPropertyChanged(nameof(ShowSuccessNotifications)); OnPropertyChanged(nameof(MinimizeToTrayOnClose));
    }
    private async Task SaveAsync() { await _settingsService.SaveAsync(_settings); StatusMessage = "设置已保存"; }
    private Task OpenLogsAsync() { var path = AppPaths.LogsDirectory; Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); return Task.CompletedTask; }
    private Task TestDdcAsync() { StatusMessage = Monitors.Count == 0 ? "尚未检测到显示器" : "DDC 检测已请求"; return Task.CompletedTask; }
}
