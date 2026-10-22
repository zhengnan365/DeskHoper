using MonitorSwitch.Models;

namespace MonitorSwitch.ViewModels;

/// 一张键鼠卡片。选它就等于选本机角色，所以只有名字可以改，含义由位置定死。
public sealed class InputModeViewModel : ObservableObject
{
    private readonly Action<InputModeViewModel> _onRenamed;
    private readonly Action<InputModeViewModel> _onSelected;
    private string _name;
    private bool _isSelected;
    private bool _isEditingName;
    private string _editableName = string.Empty;

    public InputModeViewModel(string name, int index, Action<InputModeViewModel> onRenamed, Action<InputModeViewModel> onSelected)
    {
        _name = name;
        Index = index;
        _onRenamed = onRenamed;
        _onSelected = onSelected;
        BeginRenameCommand = new AsyncRelayCommand(BeginRename);
        CommitRenameCommand = new AsyncRelayCommand(CommitRename);
        CancelRenameCommand = new AsyncRelayCommand(CancelRename);
    }

    public int Index { get; }
    public InputMode Mode => (InputMode)Index;
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) _onRenamed(this); } }
    public bool IsEditingName { get => _isEditingName; private set => SetProperty(ref _isEditingName, value); }
    public string EditableName { get => _editableName; set => SetProperty(ref _editableName, value); }
    public AsyncRelayCommand BeginRenameCommand { get; }
    public AsyncRelayCommand CommitRenameCommand { get; }
    public AsyncRelayCommand CancelRenameCommand { get; }

    public bool IsSelected { get => _isSelected; set { if (SetProperty(ref _isSelected, value) && value) _onSelected(this); } }

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
}
