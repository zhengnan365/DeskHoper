using System.Windows.Input;
using MonitorSwitch.Services;

namespace MonitorSwitch.ViewModels;

public sealed class AsyncRelayCommand(Func<Task> execute) : ICommand
{
    private readonly Func<object?, Task> _execute = _ => execute();
    private bool _isRunning;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_isRunning;
    public async void Execute(object? parameter)
    {
        if (_isRunning) return;
        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _execute(parameter); }
        catch (Exception ex) { await AppLogger.ErrorAsync("Unhandled command exception", ex); }
        finally { _isRunning = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }

    public Task ExecuteAsync(object? parameter = null) => _execute(parameter);

    public AsyncRelayCommand(Func<object?, Task> execute) : this(() => Task.CompletedTask) => _execute = execute;
}
