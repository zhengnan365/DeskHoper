using MonitorSwitch.Models;
using MonitorSwitch.Services;

namespace MonitorSwitch.ViewModels;

public sealed class MonitorViewModel : ObservableObject
{
    private MonitorDevice _device;
    private readonly IDdcCiService _ddcCiService;
    private MonitorInputMapping _inputMapping;
    private readonly Action<string> _reportStatus;
    private uint? _currentInput;
    private string _lastSwitchResult = string.Empty;
    private readonly MonitorPreferencesService? _preferencesService;
    private string? _customName;
    private bool _isEditingName;
    private string _renameOriginalName = string.Empty;

    public MonitorViewModel(MonitorDevice device, IDdcCiService ddcCiService, MonitorInputMapping inputMapping, Action<string> reportStatus, MonitorPreferencesService? preferencesService = null)
    {
        _device = device;
        _ddcCiService = ddcCiService;
        _inputMapping = inputMapping;
        _reportStatus = reportStatus;
        _preferencesService = preferencesService;
        _customName = device.CustomName;
        _currentInput = device.CurrentInput;
        ReadInputCommand = new AsyncRelayCommand(ReadInputAsync);
        SwitchToDisplayPortCommand = new AsyncRelayCommand(() => SetInputAsync(_inputMapping.DisplayPort, "DP"));
        SwitchToUsbCCommand = new AsyncRelayCommand(() => SetInputAsync(_inputMapping.UsbC, "Type-C"));
        SwitchToHdmiCommand = new AsyncRelayCommand(() => _inputMapping.Hdmi is { } hdmi ? SetInputAsync(hdmi, "HDMI") : Task.CompletedTask);
        LearnDisplayPortCommand = new AsyncRelayCommand(() => LearnAsync(InputSourceKind.DisplayPort));
        LearnUsbCCommand = new AsyncRelayCommand(() => LearnAsync(InputSourceKind.UsbC));
        LearnHdmiCommand = new AsyncRelayCommand(() => LearnAsync(InputSourceKind.Hdmi));
        ResetMappingCommand = new AsyncRelayCommand(ResetMappingAsync);
        BeginRenameCommand = new AsyncRelayCommand(() => { if (!IsEditingName) { _renameOriginalName = CustomName; EditableName = CustomName; IsEditingName = true; } return Task.CompletedTask; });
        CommitRenameCommand = new AsyncRelayCommand(CommitRenameAsync);
        CancelRenameCommand = new AsyncRelayCommand(() => { if (IsEditingName) { EditableName = _renameOriginalName; CustomName = _renameOriginalName; IsEditingName = false; } return Task.CompletedTask; });
    }

    public void UpdateFrom(MonitorDevice device, MonitorInputMapping mapping)
    {
        _device = device;
        _inputMapping = mapping;
        if (!IsEditingName && !string.Equals(_customName, device.CustomName, StringComparison.Ordinal)) _customName = device.CustomName;
        _currentInput = device.CurrentInput;
        foreach (var property in new[]
        {
            nameof(CustomName), nameof(Name), nameof(DisplayTitle), nameof(ModelSubtitle), nameof(Model), nameof(DisplayName), nameof(Identity),
            nameof(Input), nameof(InputDisplayName), nameof(CurrentInputValue), nameof(IsOnline), nameof(IsDdcSupported), nameof(DdcStatus), nameof(DisplayNumber), nameof(Bounds), nameof(EditableName),
            nameof(DisplayPortMapping), nameof(UsbCMapping), nameof(HdmiMapping), nameof(CanSwitchToDisplayPort), nameof(CanSwitchToUsbC), nameof(CanSwitchToHdmi),
            nameof(IsDisplayPortLearned), nameof(IsUsbCLearned), nameof(IsHdmiLearned)
        }) OnPropertyChanged(property);
    }

    public string Id => _device.Id;
    public string StableId => _device.StableId ?? _device.Id;
    public int DisplayNumber => _device.DisplayNumber;
    public DisplayBounds? Bounds => _device.Bounds;
    public string Name => string.IsNullOrWhiteSpace(_customName) ? _device.FriendlyName : _customName.Trim();
    public string CustomName { get => _customName ?? string.Empty; private set { if (SetProperty(ref _customName, value)) { OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayTitle)); OnPropertyChanged(nameof(ModelSubtitle)); } } }
    public string EditableName { get => CustomName; set { if (CustomName != value) CustomName = value; } }
    public string DisplayTitle => string.IsNullOrWhiteSpace(_customName) ? Model : _customName.Trim();
    public string ModelSubtitle => string.IsNullOrWhiteSpace(_customName) || string.Equals(_customName.Trim(), Model.Trim(), StringComparison.OrdinalIgnoreCase) ? string.Empty : Model;
    public string Model => _device.Model ?? "未知型号";
    public string DisplayName => _device.WindowsDisplayName;
    public string Identity => _device.StableId is null ? "硬件身份未关联" : $"{_device.Manufacturer} · {_device.Model} · { _device.SerialNumber}";
    public string Input => _currentInput is not { } value ? "读取失败" : $"{ToFriendlyInput(value)} ({value})";
    public string InputDisplayName => _currentInput is { } input ? ToFriendlyInput(input) : "未知输入源";
    public uint? CurrentInputValue => _currentInput;
    public bool IsOnline => _currentInput.HasValue || _device.IsDdcSupported;
    public bool IsDdcSupported => _device.IsDdcSupported;
    public bool CanSwitchToDisplayPort => _device.IsDdcSupported;
    public bool CanSwitchToUsbC => _device.IsDdcSupported;
    public bool CanSwitchToHdmi => _device.IsDdcSupported && _inputMapping.Hdmi.HasValue;
    public bool IsDisplayPortLearned => _inputMapping.DisplayPort != 15;
    public bool IsUsbCLearned => _inputMapping.UsbC != 16;
    public bool IsHdmiLearned => _inputMapping.Hdmi is not 17 and not null;
    public uint DisplayPortMapping => _inputMapping.DisplayPort;
    public uint UsbCMapping => _inputMapping.UsbC;
    public uint? HdmiMapping => _inputMapping.Hdmi;
    public bool IsEditingName { get => _isEditingName; private set => SetProperty(ref _isEditingName, value); }
    public string LastSwitchResult { get => _lastSwitchResult; private set => SetProperty(ref _lastSwitchResult, value); }
    public string DdcStatus => _device.DdcStatus;
    public AsyncRelayCommand ReadInputCommand { get; }
    public AsyncRelayCommand SwitchToDisplayPortCommand { get; }
    public AsyncRelayCommand SwitchToUsbCCommand { get; }
    public AsyncRelayCommand SwitchToHdmiCommand { get; }
    public AsyncRelayCommand LearnDisplayPortCommand { get; }
    public AsyncRelayCommand LearnUsbCCommand { get; }
    public AsyncRelayCommand LearnHdmiCommand { get; }
    public AsyncRelayCommand ResetMappingCommand { get; }
    public AsyncRelayCommand BeginRenameCommand { get; }
    public AsyncRelayCommand CommitRenameCommand { get; }
    public AsyncRelayCommand CancelRenameCommand { get; }

    public Task SwitchToDisplayPortAsync() => SetInputAsync(_inputMapping.DisplayPort, "DP");
    public Task SwitchToUsbCAsync() => SetInputAsync(_inputMapping.UsbC, "Type-C");
    public Task SwitchToHdmiAsync() => _inputMapping.Hdmi is { } hdmi ? SetInputAsync(hdmi, "HDMI") : Task.CompletedTask;
    public Task SetInputValueAsync(uint value) => SetInputAsync(value, ToFriendlyInput(value));
    public async Task<uint?> ReadCurrentInputValueAsync()
    {
        var value = await _ddcCiService.ReadInputAsync(Id);
        if (value is { } current) { _currentInput = current; OnPropertyChanged(nameof(Input)); OnPropertyChanged(nameof(InputDisplayName)); OnPropertyChanged(nameof(CurrentInputValue)); }
        return value;
    }
    private async Task CommitRenameAsync() { if (!IsEditingName) return; var name = EditableName.Trim(); CustomName = name; if (_preferencesService is not null) await _preferencesService.RenameAsync(StableId, name); _renameOriginalName = name; IsEditingName = false; }
    private async Task LearnAsync(InputSourceKind kind) { if (_preferencesService is null) return; var result = await new InputLearningService(_ddcCiService, _preferencesService).LearnInputSourceAsync(StableId, kind); if (result.Success) { _inputMapping = (await _preferencesService.GetAsync(StableId))?.InputMappings ?? _inputMapping; OnPropertyChanged(nameof(DisplayPortMapping)); OnPropertyChanged(nameof(UsbCMapping)); OnPropertyChanged(nameof(HdmiMapping)); OnPropertyChanged(nameof(IsDisplayPortLearned)); OnPropertyChanged(nameof(IsUsbCLearned)); OnPropertyChanged(nameof(IsHdmiLearned)); OnPropertyChanged(nameof(CanSwitchToHdmi)); } _reportStatus(result.Success ? $"{Name} 已学习 {kind}={result.LearnedValue}" : result.ErrorMessage ?? "输入源学习失败"); }
    private async Task ResetMappingAsync() { if (_preferencesService is null) return; _inputMapping = await _preferencesService.ResetMappingAsync(StableId, Model); OnPropertyChanged(nameof(DisplayPortMapping)); OnPropertyChanged(nameof(UsbCMapping)); OnPropertyChanged(nameof(HdmiMapping)); OnPropertyChanged(nameof(IsDisplayPortLearned)); OnPropertyChanged(nameof(IsUsbCLearned)); OnPropertyChanged(nameof(IsHdmiLearned)); OnPropertyChanged(nameof(CanSwitchToHdmi)); _reportStatus($"{Name} 输入映射已恢复默认"); }

    private async Task ReadInputAsync()
    {
        _reportStatus($"正在读取 {Name} 的输入源…");
        _currentInput = await _ddcCiService.ReadInputAsync(Id);
        OnPropertyChanged(nameof(Input));
        OnPropertyChanged(nameof(InputDisplayName));
        OnPropertyChanged(nameof(CurrentInputValue));
        _reportStatus(_currentInput is null ? $"{Name} 输入源读取失败" : $"{Name} 当前输入：{Input}");
    }

    private async Task SetInputAsync(uint value, string name)
    {
        _reportStatus($"正在将 {Name} 切换到 {name}…");
        var result = await _ddcCiService.SetInputAsync(Id, value);
        if (result.Succeeded)
        {
            _currentInput = value;
            OnPropertyChanged(nameof(Input));
            OnPropertyChanged(nameof(InputDisplayName));
            OnPropertyChanged(nameof(CurrentInputValue));
            _reportStatus($"{Name} 已切换到 {name}（第 {result.Attempts} 次尝试成功）");
            LastSwitchResult = $"成功：{name}";
        }
        else { LastSwitchResult = $"失败：{name}（Win32 {result.LastError}）"; _reportStatus($"{Name} 切换到 {name} 失败（尝试 {result.Attempts} 次，Win32 错误 {result.LastError}）"); }
    }

    private string ToFriendlyInput(uint value) => _inputMapping.ToSource(value) switch
    {
        MonitorInputSource.DisplayPort => "DisplayPort",
        MonitorInputSource.UsbC => "Type-C",
        MonitorInputSource.Hdmi => "HDMI",
        _ => "未知输入源"
    };
}
