using MonitorSwitch.Models;

namespace MonitorSwitch.ViewModels;

/// 一张键鼠卡片。名字是两台机器之间的约定，所以两边必须选同一张；角色各台自己选一次。
public sealed class InputModeViewModel : ObservableObject
{
    private readonly Action<InputModeViewModel> _onChanged;
    private string _name;
    private InputRole _role;
    private bool _isSelected;

    public InputModeViewModel(string name, InputRole role, int index, Action<InputModeViewModel> onChanged)
    {
        _name = name;
        _role = role;
        Index = index;
        _onChanged = onChanged;
        ChooseControllerCommand = new AsyncRelayCommand(() => { SetRole(InputRole.Controller); return Task.CompletedTask; });
        ChooseTargetCommand = new AsyncRelayCommand(() => { SetRole(InputRole.Target); return Task.CompletedTask; });
        BeginRenameCommand = new AsyncRelayCommand(BeginRename);
        CommitRenameCommand = new AsyncRelayCommand(CommitRename);
        CancelRenameCommand = new AsyncRelayCommand(CancelRename);
    }

    public int Index { get; }
    /// 第一张卡片是"各用各的"，没有角色可言。
    public bool IsOffMode => Index == 0;
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) _onChanged(this); } }
    private bool _isEditingName;
    private string _editableName = string.Empty;
    public bool IsEditingName { get => _isEditingName; private set => SetProperty(ref _isEditingName, value); }
    public string EditableName { get => _editableName; set => SetProperty(ref _editableName, value); }
    public AsyncRelayCommand BeginRenameCommand { get; }
    public AsyncRelayCommand CommitRenameCommand { get; }
    public AsyncRelayCommand CancelRenameCommand { get; }
    private Task BeginRename() { EditableName = Name; IsEditingName = true; return Task.CompletedTask; }
    private Task CancelRename() { EditableName = Name; IsEditingName = false; return Task.CompletedTask; }
    private Task CommitRename()
    {
        if (!IsEditingName) return Task.CompletedTask;
        IsEditingName = false;
        var trimmed = EditableName.Trim();
        if (trimmed.Length > 0) Name = trimmed;
        EditableName = Name;
        return Task.CompletedTask;
    }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public InputRole Role { get => _role; private set { if (_role == value) return; _role = value; OnPropertyChanged(nameof(Role)); OnPropertyChanged(nameof(RoleText)); OnPropertyChanged(nameof(IsController)); OnPropertyChanged(nameof(IsTarget)); OnPropertyChanged(nameof(NeedsRole)); _onChanged(this); } }
    public bool IsController => Role == InputRole.Controller;
    public bool IsTarget => Role == InputRole.Target;
    public bool NeedsRole => Role == InputRole.None;
    public string RoleText => Role switch
    {
        InputRole.Controller => "这台机器的键盘鼠标转给对方用",
        InputRole.Target => "这台机器接受对方的键盘鼠标",
        _ => IsOffMode ? "两台电脑各用各的，互不转发。" : "先选这台是控制端还是被控端"
    };
    public AsyncRelayCommand ChooseControllerCommand { get; }
    public AsyncRelayCommand ChooseTargetCommand { get; }

    internal void SetRole(InputRole role) => Role = role;
}
