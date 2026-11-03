using System.Windows.Forms;
using MonitorSwitch.Native;

namespace MonitorSwitch.Services;

/// 让系统不主动关显示器。Type-C 接的显示器一旦息屏，往往要先把输入切到 DP/HDMI 才认得回来，
/// 所以切换场景下需要它能保持亮着。
public sealed class KeepDisplayAwakeService : IDisposable
{
    private bool _enabled;
    private bool _holding;
    private bool _disposed;

    /// 纯判断，好测：0 是电池供电，1 是交流，255 是压根没有电池（台式机）。
    /// 把 255 当成没电的话，这功能在台式机上永远不可能生效。
    public static bool ShouldHold(bool enabled, byte acLineStatus) => enabled && acLineStatus != 0;

    /// 走 WinForms 的封装而不是自己声明 SYSTEM_POWER_STATUS：那个结构体是 6 个 BYTE，
    /// 手写布局一旦错位就是访问违例，而框架里已经有正确定义。
    public static byte ReadAcLineStatus() => SystemInformation.PowerStatus.PowerLineStatus switch
    {
        PowerLineStatus.Online => 1,
        PowerLineStatus.Offline => 0,
        _ => byte.MaxValue
    };

    /// 开关状态和生效状态是两回事：开着但用电池时，开关是"开"，实际不拦息屏。
    public bool IsHolding => _holding;

    public void Apply(bool enabled)
    {
        _enabled = enabled;
        ApplyCore();
    }

    /// 电源插拔没有现成事件可用，就靠界面那边每 10 秒调一次重算，够用了。
    public void Refresh() => ApplyCore();

    private void ApplyCore()
    {
        if (_disposed) return;
        var hold = ShouldHold(_enabled, ReadAcLineStatus());
        if (hold == _holding) return;
        _holding = hold;
        Power.SetThreadExecutionState(hold ? Power.EsContinuous | Power.EsDisplayRequired : Power.EsContinuous);
        _ = AppLogger.InfoAsync($"Keep display awake {(hold ? "engaged" : "released")}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_holding)
        {
            Power.SetThreadExecutionState(Power.EsContinuous);
            _holding = false;
        }
    }
}
