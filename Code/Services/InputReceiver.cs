using System.Runtime.InteropServices;
using MonitorSwitch.Native;

namespace MonitorSwitch.Services;

/// 被控端：把对端发来的键鼠事件注入本机。
/// Windows 的硬限制要提前说清楚：安全桌面（UAC 提权弹窗）和以管理员身份运行的窗口，
/// 普通权限进程注入不进去，这时 SendInput 会返回 0，页面上要能看出来。
public static class InputReceiver
{
    private const uint KeyEventfKeyUp = 0x0002;
    private static readonly int EventSize = Marshal.SizeOf<UserInput.InputEvent>();

    public static bool Apply(KeyboardInput input)
    {
        var flags = input.IsExtended ? UserInput.KeyboardEventfExtendedKey : 0u;
        if (!input.IsDown) flags |= KeyEventfKeyUp;
        return Send(UserInput.InputEvent.Keyboard(input.VirtualKey, input.ScanCode, flags));
    }

    public static bool Apply(MouseInput input)
    {
        // 鼠标只用相对位移，绝不带 ABSOLUTE 标志：两台机器分辨率和缩放不同，绝对坐标会指到别处去。
        // 移动和滚轮可以并在一帧里，mouseData 就是滚轮格数。
        var flags = input.EventFlags;
        if (input.WheelDelta != 0) flags |= UserInput.MouseEventfWheel;
        return Send(UserInput.InputEvent.Mouse(input.DeltaX, input.DeltaY, flags, (uint)input.WheelDelta));
    }

    private static bool Send(UserInput.InputEvent input) => UserInput.SendInput(1, [input], EventSize) == 1;
}
