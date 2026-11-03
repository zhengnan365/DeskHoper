using System.Runtime.InteropServices;

namespace MonitorSwitch.Native;

public static class Power
{
    public const uint EsContinuous = 0x80000000;
    public const uint EsDisplayRequired = 0x00000002;

    /// 执行状态是**按线程**记录的，线程退出即失效，所以只能在长命的 UI 线程上调用。
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SetThreadExecutionState(uint esFlags);
}
