using System.Runtime.InteropServices;

namespace MonitorSwitch.Native;

/// 键鼠转发要碰的两块原生能力：低级钩子（控制端拦截本机输入）和 SendInput（被控端注入）。
/// 钩子回调必须有人一直持有委托引用，否则会被 GC 掉，然后在非托管回调里炸。
internal static class UserInput
{
    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;
    internal const int HcSkipNext = -1;
    internal const int WmKeyDown = 0x0100;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmSysKeyUp = 0x0105;
    internal const int WmMouseMove = 0x0200;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;
    internal const int WmRButtonDown = 0x0204;
    internal const int WmRButtonUp = 0x0205;
    internal const int WmMButtonDown = 0x0207;
    internal const int WmMButtonUp = 0x0208;
    internal const int WmMouseWheel = 0x020A;
    internal const uint LlwkhfUp = 0x00000004;
    internal const uint LlwkhfExtended = 0x00000001;
    internal const uint LlmafExtended = 0x00000001;
    internal const uint MouseEventfMove = 0x0001;
    internal const uint MouseEventfLeftDown = 0x0002;
    internal const uint MouseEventfLeftUp = 0x0004;
    internal const uint MouseEventfRightDown = 0x0008;
    internal const uint MouseEventfRightUp = 0x0010;
    internal const uint MouseEventfMiddleDown = 0x0020;
    internal const uint MouseEventfMiddleUp = 0x0040;
    internal const uint MouseEventfWheel = 0x0800;
    internal const uint InputKeyboard = 1;
    internal const uint InputMouse = 0;
    internal const uint KeyboardEventfScancode = 0x0008;
    internal const uint KeyboardEventfExtendedKey = 0x0001;

    // 原始输入：直接拿设备位移，不经过任何坐标空间。低级钩子的 pt 是 per-monitor 物理坐标，
    // GetCursorPos 在 System-DPI 感知进程里是虚拟化坐标，两者相减在副屏上会得到垃圾值。
    internal const uint WmInput = 0x00FF;
    internal const uint RidInput = 0x10000003;   // 不是 0x100；RID_HEADER 才是 0x10000005
    internal const uint RidevInputsink = 0x00000100;
    internal const nint HwndMessage = -3;
    internal const ushort GenericAll = 0;
    internal const ushort UsagePageGeneric = 0x01;
    internal const ushort UsageMouse = 0x02;
    internal const int RawInputHeaderSize = 24;      // x64：DWORD+DWORD+ULONG_PTR+ULONG_PTR
    internal const int RawMouseFlagsOffset = 24;
    internal const int RawMouseLastXOffset = 36;     // USHORT + 补齐 + ULONG ulButtons + ULONG ulRawButtons
    internal const int RawMouseLastYOffset = 40;
    internal const int RawMouseExtraInfoOffset = 44;
    internal const ushort MouseMoveAbsolute = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        public ushort usagePage;
        public ushort usage;
        public uint flags;
        public nint window;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    internal delegate nint HookProc(int nCode, nint wParam, nint lParam);
    internal delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, InputEvent[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint RegisterClassEx(ref WndClassEx classEx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    internal static extern uint GetRawInputData(nint header, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseLlHookStruct
    {
        public int ptX;
        public int ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    /// x64 的 INPUT：type 占 0-3，联合体因为 ULONG_PTR 的 8 字节对齐从偏移 8 开始，整块 40 字节。
    /// MOUSEINPUT 和 KEYBDINPUT 字段宽度不同，所以只能按 Explicit 逐个摆偏移，不能靠嵌套结构猜。
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct InputEvent
    {
        [FieldOffset(0)] public uint type;

        [FieldOffset(8)] public int mouseDx;
        [FieldOffset(12)] public int mouseDy;
        [FieldOffset(16)] public uint mouseData;
        [FieldOffset(20)] public uint mouseFlags;
        [FieldOffset(24)] public uint mouseTime;
        [FieldOffset(32)] public nint mouseExtraInfo;

        [FieldOffset(8)] public ushort keyVk;
        [FieldOffset(10)] public ushort keyScan;
        [FieldOffset(12)] public uint keyFlags;
        [FieldOffset(16)] public uint keyTime;
        [FieldOffset(24)] public nint keyExtraInfo;

        /// dwExtraInfo 用来标记"这是本机注入进来的"，控制端的钩子看到它就放过，不会把自己转发的输入再转一遍。
        public const int InjectTag = 0x4D535731;

        public static InputEvent Keyboard(ushort vk, ushort scanCode, uint flags) => new()
        {
            type = InputKeyboard,
            keyVk = vk,
            keyScan = scanCode,
            keyFlags = flags,
            keyExtraInfo = (nint)InjectTag
        };

        public static InputEvent Mouse(int dx, int dy, uint flags, uint mouseData) => new()
        {
            type = InputMouse,
            mouseDx = dx,
            mouseDy = dy,
            mouseFlags = flags,
            mouseData = mouseData,
            mouseExtraInfo = (nint)InjectTag
        };
    }
}
