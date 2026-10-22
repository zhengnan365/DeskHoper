using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MonitorSwitch.Services;

namespace MonitorSwitch;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "Global\\DeskHoper-7f12d6c1-5f33-4c6d-9e48-5f16c20e4d31", out var createdNew);
        if (!createdNew)
        {
            // 已经有一个在跑：藏到托盘时它没有任务栏按钮，光退出会让人以为双击没反应，
            // 所以按标题把那个实例的主窗口找出来唤回前台。
            WakeExistingInstance();
            Environment.Exit(0);
            return;
        }
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        _ = AppLogger.InfoAsync($"App start runtime={Environment.Version} os={Environment.OSVersion} processArchitecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    }

    private static void WakeExistingInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess().Id;
            foreach (var other in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                if (other.Id == current) continue;
                var handle = FindWindowOfProcess(other.Id);
                if (handle == IntPtr.Zero) continue;
                ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                return;
            }
        }
        catch (Exception ex) { _ = AppLogger.ErrorAsync("Could not wake the running instance", ex); }
    }

    private static IntPtr FindWindowOfProcess(int pid)
    {
        var found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var windowPid);
            if ((int)windowPid != pid) return true;
            var text = new StringBuilder(512);
            GetWindowText(handle, text, text.Capacity);
            if (text.ToString() != "DeskHoper") return true;
            found = handle;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private const int SwRestore = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    protected override void OnExit(ExitEventArgs e) { _singleInstanceMutex?.Dispose(); base.OnExit(e); }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) => _ = AppLogger.ErrorAsync("Dispatcher unhandled exception", e.Exception);
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) => _ = AppLogger.ErrorAsync("Unobserved task exception", e.Exception);
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) => _ = AppLogger.ErrorAsync("AppDomain unhandled exception", e.ExceptionObject as Exception);
}
