using System.Collections.ObjectModel;
using MonitorSwitch.Models;

namespace MonitorSwitch.ViewModels;

public sealed record ProfileInputOption(string Label, InputSourceKind Kind, uint Input);

public sealed record ProfileProjectionOption(string Label, ProjectionMode? Mode);

public sealed class ProfileEditRow : ObservableObject
{
    private ProfileInputOption? _selectedOption;

    public ProfileEditRow(MonitorViewModel monitor, SwitchProfileMonitor? savedTarget)
    {
        Monitor = monitor;
        var options = new List<ProfileInputOption>();
        if (monitor.DisplayPortMapping != 0) options.Add(new ProfileInputOption("DisplayPort", InputSourceKind.DisplayPort, monitor.DisplayPortMapping));
        if (monitor.UsbCMapping != 0) options.Add(new ProfileInputOption("Type-C", InputSourceKind.UsbC, monitor.UsbCMapping));
        if (monitor.HdmiMapping is { } hdmi && hdmi != 0) options.Add(new ProfileInputOption("HDMI", InputSourceKind.Hdmi, hdmi));
        Options = options;
        SelectedOption = savedTarget is not null
            ? Options.FirstOrDefault(option => savedTarget.InputKind is { } kind && option.Kind == kind) ?? Options.FirstOrDefault(option => option.Input == savedTarget.Input) ?? Options.FirstOrDefault()
            : Options.FirstOrDefault(option => monitor.CurrentInputValue.HasValue && option.Input == monitor.CurrentInputValue.Value) ?? Options.FirstOrDefault();
    }

    public MonitorViewModel Monitor { get; }
    public string Title => Monitor.DisplayTitle;
    public IReadOnlyList<ProfileInputOption> Options { get; }
    public ProfileInputOption? SelectedOption { get => _selectedOption; set => SetProperty(ref _selectedOption, value); }

    /// 把这台显示器现在的输入源填进来，等价于原来右键的"保存当前配置"。
    public void SelectCurrent()
    {
        if (Monitor.CurrentInputValue is { } current)
            SelectedOption = Options.FirstOrDefault(option => option.Input == current) ?? SelectedOption;
    }
}

public sealed class ProfileEditViewModel : ObservableObject
{
    private string _name;
    private ProfileProjectionOption? _selectedProjection;

    public ProfileEditViewModel(SwitchProfile source, IReadOnlyList<MonitorViewModel> monitors)
    {
        ProfileId = source.Id;
        _name = source.Name;
        ProjectionOptions =
        [
            new("不修改投影模式", null),
            new("仅电脑屏幕", ProjectionMode.Internal),
            new("复制", ProjectionMode.Duplicate),
            new("扩展", ProjectionMode.Extend),
            new("仅第二屏幕", ProjectionMode.External)
        ];
        SelectedProjection = ProjectionOptions.FirstOrDefault(option => option.Mode == source.ProjectionMode) ?? ProjectionOptions[0];
        Rows = [.. monitors.Where(monitor => monitor.IsDdcSupported).Select(monitor => new ProfileEditRow(monitor, source.Monitors.FirstOrDefault(target => target.StableId == monitor.StableId)))];
        UseCurrentInputsCommand = new AsyncRelayCommand(() => { foreach (var row in Rows) row.SelectCurrent(); return Task.CompletedTask; });
    }

    /// 把每台显示器现在的输入源填进来，等价于原来右键菜单里的"保存当前配置"。
    public AsyncRelayCommand UseCurrentInputsCommand { get; }

    public string? ProfileId { get; }
    public string WindowTitle => "编辑切换按钮";
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(CanSave)); } }
    public IReadOnlyList<ProfileProjectionOption> ProjectionOptions { get; }
    public ProfileProjectionOption? SelectedProjection { get => _selectedProjection; set => SetProperty(ref _selectedProjection, value); }
    public IReadOnlyList<ProfileEditRow> Rows { get; }
    public bool CanSave => !string.IsNullOrWhiteSpace(Name) && Rows.Count > 0 && Rows.All(row => row.SelectedOption is not null);

    public SwitchProfile Build() => new SwitchProfile
    {
        Id = ProfileId ?? Guid.NewGuid().ToString("N"),
        Name = Name.Trim(),
        ProjectionMode = SelectedProjection?.Mode,
        Monitors = [.. Rows.Where(row => row.SelectedOption is { } option).Select(row => new SwitchProfileMonitor { StableId = row.Monitor.StableId, InputKind = row.SelectedOption!.Kind, Input = row.SelectedOption.Input })]
    };
}
