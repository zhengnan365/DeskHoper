using MonitorSwitch.Models;
using MonitorSwitch.Services;

namespace MonitorSwitch.ViewModels;

public sealed class ProfileViewModel : ObservableObject
{
    private readonly Func<SwitchProfile, Task> _execute;
    private readonly ProfileService? _profileService;
    private readonly Func<ProfileViewModel, Task>? _saveCurrent;
    private bool _isCurrent;
    private bool _isEditingName;
    private string _editableName;
    private string _lastSwitchResult = string.Empty;
    public ProfileViewModel(SwitchProfile profile, Func<SwitchProfile, Task> execute, ProfileService? profileService = null, Func<ProfileViewModel, Task>? saveCurrent = null) { Profile = profile; _execute = execute; _profileService = profileService; _saveCurrent = saveCurrent; _editableName = profile.Name; SwitchCommand = new AsyncRelayCommand(() => _execute(Profile)); SaveCurrentInputsCommand = new AsyncRelayCommand(() => _saveCurrent?.Invoke(this) ?? Task.CompletedTask); BeginRenameCommand = new AsyncRelayCommand(BeginRename); CommitRenameCommand = new AsyncRelayCommand(CommitRename); CancelRenameCommand = new AsyncRelayCommand(CancelRename); }
    public SwitchProfile Profile { get; private set; }
    public string Name => Profile.Name;
    public string DisplayName => Name;
    public string ConnectionSummary => string.Join(" + ", Profile.Monitors.Where(item => item.InputKind is not null).Select(item => item.InputKind switch { InputSourceKind.DisplayPort => "DP", InputSourceKind.UsbC => "Type-C", InputSourceKind.Hdmi => "HDMI", _ => "未知" }));
    public string EditableName { get => _editableName; set => SetProperty(ref _editableName, value); }
    public ProjectionMode? ProjectionMode => Profile.ProjectionMode;
    public string ShortcutDisplayText { get; init; } = "未设置";
    public bool IsCurrent { get => _isCurrent; internal set => SetProperty(ref _isCurrent, value); }
    public string LastSwitchResult { get => _lastSwitchResult; internal set => SetProperty(ref _lastSwitchResult, value); }
    public AsyncRelayCommand SwitchCommand { get; }
    public AsyncRelayCommand SaveCurrentInputsCommand { get; }
    public bool IsEditingName { get => _isEditingName; private set => SetProperty(ref _isEditingName, value); }
    public AsyncRelayCommand BeginRenameCommand { get; }
    public AsyncRelayCommand CommitRenameCommand { get; }
    public AsyncRelayCommand CancelRenameCommand { get; }
    private Task BeginRename() { EditableName = Name; IsEditingName = true; return Task.CompletedTask; }
    private async Task CommitRename() { if (!IsEditingName) return; var name = EditableName.Trim(); if (name.Length == 0) { EditableName = Name; IsEditingName = false; return; } var oldName = Profile.Name; Profile = new SwitchProfile { Id = Profile.Id, Name = name, Monitors = Profile.Monitors, ProjectionMode = Profile.ProjectionMode }; if (_profileService is not null) await _profileService.RenameAsync(oldName, Profile); IsEditingName = false; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayName)); }
    private Task CancelRename() { EditableName = Name; IsEditingName = false; return Task.CompletedTask; }
    internal void ReplaceProfile(SwitchProfile profile) { Profile = profile; OnPropertyChanged(nameof(ConnectionSummary)); }
    internal void UpdateProfile(SwitchProfile profile) { Profile = profile; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(ConnectionSummary)); OnPropertyChanged(nameof(ProjectionMode)); }
}
