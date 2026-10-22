using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace MonitorSwitch.Services;

/// <summary>
/// 托盘图标。收进托盘只是把主窗口 Hide()，热键和剪贴板监听仍挂在主窗口的 HwndSource 上，
/// 所以共享照常工作；真正退出走"退出"菜单，那条路才会跑完整的清理。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Icon? _ownedIcon;
    private readonly NotifyIcon _notify;

    public TrayService(Action show, Action exit, Action desktop, Action laptop)
    {
        _ownedIcon = TryLoadAppIcon();
        _notify = new NotifyIcon { Text = "DeskHoper", Icon = _ownedIcon ?? SystemIcons.Application, Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("切换到台式机", null, (_, _) => desktop());
        menu.Items.Add("切换到笔记本", null, (_, _) => laptop());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开主界面", null, (_, _) => show());
        menu.Items.Add("退出", null, (_, _) => exit());
        _notify.ContextMenuStrip = menu;
        // 单击也唤回，比双击好找；菜单留给右键。
        _notify.Click += (_, _) => show();
    }

    /// 取 exe 自己内嵌的图标（csproj 里设了 ApplicationIcon），省得往发布包里再塞一个 .ico。
    /// 取不到就退回系统应用图标，但那份是共享的 GDI 资源，不能 Dispose。
    private static Icon? TryLoadAppIcon()
    {
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrEmpty(path) ? null : Icon.ExtractAssociatedIcon(path);
        }
        catch (Exception ex)
        {
            _ = AppLogger.ErrorAsync("Tray icon could not be extracted from the exe", ex);
            return null;
        }
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _ownedIcon?.Dispose();
    }
}
