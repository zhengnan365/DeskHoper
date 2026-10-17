using System.Collections.ObjectModel;
using MonitorSwitch.Models;
using MonitorSwitch.Services;

namespace MonitorSwitch.ViewModels;

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
    }
    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
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
            UpdateCurrentProfileFromInputs();
            Status = $"已切换到{profile.Name}";
        }
        finally { IsSwitching = false; }
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

    public async Task SaveProfileAsync(SwitchProfile profile)
    {
        var saved = await _profileService.LoadAsync();
        if (saved.Any(item => item.Id == profile.Id)) await _profileService.UpdateAsync(profile);
        else await _profileService.AddAsync(profile);
        MergeProfiles(await _profileService.LoadAsync());
        UpdateCurrentProfileFromInputs();
        UpdateSteadyStateStatus();
        Status = $"已保存：{profile.Name}";
    }

    public async Task DeleteProfileAsync(ProfileViewModel profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Profile.Id)) return;
        var name = profile.Name;
        await _profileService.DeleteAsync(profile.Profile.Id);
        MergeProfiles(await _profileService.LoadAsync());
        UpdateCurrentProfileFromInputs();
        UpdateSteadyStateStatus();
        Status = $"已删除：{name}";
    }

    private void UpdateCurrentProfileFromInputs()
    {
        var current = Monitors.Where(m => m.IsDdcSupported).ToDictionary(m => m.StableId, m => m.CurrentInputValue, StringComparer.Ordinal);
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
