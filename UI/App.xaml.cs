using System.Windows;
using System.Windows.Threading;
using MonitorSwitch.Services;

namespace MonitorSwitch;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "Global\\MonitorSwitcher-7f12d6c1-5f33-4c6d-9e48-5f16c20e4d31", out var createdNew);
        if (!createdNew) { Environment.Exit(0); return; }
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        _ = AppLogger.InfoAsync($"App start runtime={Environment.Version} os={Environment.OSVersion} processArchitecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    }

    protected override void OnExit(ExitEventArgs e) { _singleInstanceMutex?.Dispose(); base.OnExit(e); }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) => _ = AppLogger.ErrorAsync("Dispatcher unhandled exception", e.Exception);
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) => _ = AppLogger.ErrorAsync("Unobserved task exception", e.Exception);
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) => _ = AppLogger.ErrorAsync("AppDomain unhandled exception", e.ExceptionObject as Exception);
}
