using System.Drawing;
using System.Windows.Forms;

namespace MonitorSwitch.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon = new() { Text = "MonitorSwitch", Visible = true, Icon = SystemIcons.Application };
    public TrayService(Action show, Action settings, Action exit, Action desktop, Action laptop)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("MonitorSwitch").Enabled = false;
        menu.Items.Add("切换到台式机", null, (_, _) => desktop());
        menu.Items.Add("切换到笔记本", null, (_, _) => laptop());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开主界面", null, (_, _) => show());
        menu.Items.Add("设置", null, (_, _) => settings());
        menu.Items.Add("退出", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => show();
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
