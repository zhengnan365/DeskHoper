using MonitorSwitch.Models;

namespace MonitorSwitch.ViewModels;

public sealed class ProjectionModeViewModel(ProjectionMode mode, string title, Func<Task> apply, bool isRecommended = false) : ObservableObject
{
    private bool _isSelected;
    public ProjectionMode Mode { get; } = mode;
    public string Title { get; } = title;
    public bool IsRecommended { get; } = isRecommended;
    public AsyncRelayCommand ApplyCommand { get; } = new(apply);
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
