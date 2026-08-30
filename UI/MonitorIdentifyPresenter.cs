using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MonitorSwitch.Models;
using MonitorSwitch.Services;

namespace MonitorSwitch.Views;

public sealed class MonitorIdentifyPresenter : IMonitorIdentificationPresenter
{
    private readonly object _sync = new();
    private readonly List<Window> _windows = [];
    private CancellationTokenSource? _displayCancellation;

    public async Task ShowAsync(IReadOnlyList<MonitorIdentificationTarget> targets, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        CloseAll();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync) _displayCancellation = linked;
        try
        {
            foreach (var target in targets.Where(item => item.Bounds is { Width: > 0, Height: > 0 }))
            {
                var bounds = target.Bounds!;
                var window = CreateWindow(target.DisplayNumber, bounds);
                lock (_sync) _windows.Add(window);
                window.Show();
            }
            await Task.Delay(duration, linked.Token);
        }
        catch (OperationCanceledException) { }
        finally { CloseAll(); linked.Dispose(); }
    }

    public void Dispose() => CloseAll();

    private static Window CreateWindow(int number, DisplayBounds bounds)
    {
        const double width = 220;
        const double height = 150;
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Width = width,
            Height = height,
            Left = bounds.Left + (bounds.Width - width) / 2,
            Top = bounds.Top + (bounds.Height - height) / 2,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(232, 31, 41, 55)),
                CornerRadius = new CornerRadius(14),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45 },
                Child = new System.Windows.Controls.TextBlock { Text = number.ToString(), Foreground = Brushes.White, FontSize = 72, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center }
            }
        };
        window.SourceInitialized += (_, _) => { ConfigureNoActivate(window); ApplyDpiLayout(window, bounds); };
        window.IsHitTestVisible = false;
        return window;
    }

    private static void ConfigureNoActivate(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(handle, ExStyleIndex).ToInt64();
        SetWindowLongPtr(handle, ExStyleIndex, new nint(style | ToolWindowStyle | NoActivateStyle | TransparentStyle));
    }

    private static void ApplyDpiLayout(Window window, DisplayBounds bounds)
    {
        var dpi = GetDpiForWindow(new WindowInteropHelper(window).Handle);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        const double pixelWidth = 220;
        const double pixelHeight = 150;
        var width = pixelWidth / scale;
        var height = pixelHeight / scale;
        window.Width = width;
        window.Height = height;
        window.Left = bounds.Left / scale + (bounds.Width / scale - width) / 2.0;
        window.Top = bounds.Top / scale + (bounds.Height / scale - height) / 2.0;
    }

    private void CloseAll()
    {
        CancellationTokenSource? cancellation;
        Window[] windows;
        lock (_sync) { cancellation = _displayCancellation; _displayCancellation = null; windows = _windows.ToArray(); _windows.Clear(); }
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var window in windows)
        {
            try { if (window.Dispatcher.CheckAccess()) window.Close(); else window.Dispatcher.BeginInvoke(window.Close); } catch { }
        }
    }

    private const int ExStyleIndex = -20;
    private const long ToolWindowStyle = 0x00000080;
    private const long NoActivateStyle = 0x08000000;
    private const long TransparentStyle = 0x00000020;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hWnd);
}
