using System.Runtime.InteropServices;
using System.Threading.Channels;
using MonitorSwitch.Native;

namespace MonitorSwitch.Services;

/// 控制端：装低级钩子，把本机键鼠拦下来转发出去，并让本机不响应。
/// 三条铁律：
/// 1. 钩子回调里绝不能 await 网络。回调跑在专用线程的消息循环上，一卡住本机就是真的敲不动键盘，
///    所以只往 Channel 里塞，由另一个线程慢慢发。
/// 2. 按键的 up 不能丢。丢了 up，对端就是一个一直按住的键。所以键盘通道无界，只对鼠标做合并。
/// 3. Ctrl+Alt+Q 必须先放行再交还控制权，否则接管中连"退出接管"都打不出来。
public sealed class InputForwarder : IDisposable
{
    internal const int EscapeVirtualKey = 0x51; // Q
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const uint LlmhfInjected = 0x00000001;
    private const uint WmQuit = 0x0012;

    private readonly Func<KeyboardInput, Task> _sendKeyboard;
    private readonly Func<MouseInput, Task> _sendMouse;
    private readonly Action _escapePressed;
    private readonly Func<bool> _shareKeyboard;
    private readonly Func<bool> _shareMouse;
    private readonly Channel<KeyboardInput> _keys = Channel.CreateUnbounded<KeyboardInput>();
    private readonly Channel<MouseInput> _mouse = Channel.CreateUnbounded<MouseInput>();
    private readonly UserInput.HookProc _keyboardProc;
    private readonly UserInput.HookProc _mouseProc;
    private readonly UserInput.WndProc _rawProc;
    private const int RawBufferBytes = 512;
    private nint _rawWindow;
    private nint _rawBuffer;
    private readonly CancellationTokenSource _cancellation = new();
    private nint _keyboardHook;
    private nint _mouseHook;
    private uint _win32ThreadId;
    private int _started;

    /// 发送委托要 await 回来：不等待的话泵会跑到网络前面，把帧堆在队列里，
    /// 一旦有次抖动就变成"攒一批再放出去"的橡皮筋手感。同时最多一帧在飞。
    public InputForwarder(Func<KeyboardInput, Task> sendKeyboard, Func<MouseInput, Task> sendMouse, Action escapePressed, Func<bool> shareKeyboard, Func<bool> shareMouse)
    {
        _sendKeyboard = sendKeyboard;
        _sendMouse = sendMouse;
        _escapePressed = escapePressed;
        _shareKeyboard = shareKeyboard;
        _shareMouse = shareMouse;
        // 委托必须被字段持有：非托管回调进来时托管侧要是已经把它 GC 掉，进程直接崩在回调里。
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        _rawProc = RawWindowProc;
    }

    public bool IsRunning => Volatile.Read(ref _started) == 1;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var thread = new Thread(Run) { IsBackground = true, Name = "DeskHoper.InputForwarder" };
        thread.Start();
    }

    private void Run()
    {
        _win32ThreadId = GetCurrentThreadId();
        var instance = UserInput.GetModuleHandle(null);
        _rawBuffer = Marshal.AllocHGlobal(RawBufferBytes);
        StartRawMouse(instance);
        _keyboardHook = UserInput.SetWindowsHookEx(UserInput.WhKeyboardLl, _keyboardProc, instance, 0);
        _mouseHook = UserInput.SetWindowsHookEx(UserInput.WhMouseLl, _mouseProc, instance, 0);
        if (_keyboardHook == nint.Zero && _mouseHook == nint.Zero)
        {
            Volatile.Write(ref _started, 0);
            _ = AppLogger.ErrorAsync("Input hooks could not be installed", new InvalidOperationException("SetWindowsHookEx 两次都返回 0。"));
            return;
        }
        _ = PumpChannelsAsync(_cancellation.Token);
        // 低级钩子的回调是在装它的那个线程上派发的，所以这个线程必须有消息循环。WM_INPUT 也走同一个循环。
        while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        if (_keyboardHook != nint.Zero) UserInput.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != nint.Zero) UserInput.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = nint.Zero;
        if (_rawWindow != nint.Zero) UserInput.DestroyWindow(_rawWindow);
        _rawWindow = nint.Zero;
        if (_rawBuffer != nint.Zero) Marshal.FreeHGlobal(_rawBuffer);
        _rawBuffer = nint.Zero;
        Volatile.Write(ref _started, 0);
    }

    /// 鼠标位移改由原始输入提供。注册到一块消息窗口上，RIDEV_INPUTSINK 保证程序在后台也收得到。
    private void StartRawMouse(nint instance)
    {
        var className = "DeskHoper.RawInput";
        var classEx = new UserInput.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<UserInput.WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_rawProc),
            hInstance = instance,
            lpszClassName = className,
        };
        if (UserInput.RegisterClassEx(ref classEx) == 0 && Marshal.GetLastWin32Error() != 141)   // 141 = 类已注册过
        {
            _ = AppLogger.ErrorAsync("Raw input window class could not be registered", new InvalidOperationException($"RegisterClassEx 失败，错误码 {Marshal.GetLastWin32Error()}。"));
            return;
        }
        _rawWindow = UserInput.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, UserInput.HwndMessage, nint.Zero, instance, nint.Zero);
        if (_rawWindow == nint.Zero)
        {
            _ = AppLogger.ErrorAsync("Raw input window could not be created", new InvalidOperationException($"CreateWindowEx 失败，错误码 {Marshal.GetLastWin32Error()}。"));
            return;
        }
        var device = new UserInput.RawInputDevice
        {
            usagePage = UserInput.UsagePageGeneric,
            usage = UserInput.UsageMouse,
            flags = UserInput.RidevInputsink,
            window = _rawWindow,
        };
        if (!UserInput.RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<UserInput.RawInputDevice>()))
            _ = AppLogger.ErrorAsync("Raw mouse could not be registered", new InvalidOperationException($"RegisterRawInputDevices 失败，错误码 {Marshal.GetLastWin32Error()}。"));
    }

    private nint RawWindowProc(nint hwnd, uint message, nint wParam, nint lParam)
        => message == UserInput.WmInput ? ReadRawMouse(lParam) : UserInput.DefWindowProc(hwnd, message, wParam, lParam);

    private nint ReadRawMouse(nint header)
    {
        uint size = 0;
        if (UserInput.GetRawInputData(header, UserInput.RidInput, nint.Zero, ref size, UserInput.RawInputHeaderSize) == 0xFFFFFFFF) return 0;
        // 比预想的大就当没看见：平板、触摸板、摇杆的载荷结构不一样，硬解析会读到别的东西上去。
        if (size == 0 || size > RawBufferBytes) return 0;
        if (UserInput.GetRawInputData(header, UserInput.RidInput, _rawBuffer, ref size, UserInput.RawInputHeaderSize) == 0xFFFFFFFF) return 0;
        if (Marshal.ReadInt32(_rawBuffer, 0) != 0) return 0;   // RAWINPUTHEADER.type，0 才是鼠标
        // 本机注入出去的移动同样会变成原始输入。两台实例同机跑、或者哪台两边都开了转发时，
        // 不认这个标记就会把自己发的再发一遍，光标直接失控。
        if (Marshal.ReadIntPtr(_rawBuffer, UserInput.RawMouseExtraInfoOffset) == UserInput.InputEvent.InjectTag) return 0;
        if (((ushort)Marshal.ReadInt16(_rawBuffer, UserInput.RawMouseFlagsOffset) & UserInput.MouseMoveAbsolute) != 0) return 0;   // 远程桌面/平板给的是绝对坐标，不是设备位移
        var dx = Marshal.ReadInt32(_rawBuffer, UserInput.RawMouseLastXOffset);
        var dy = Marshal.ReadInt32(_rawBuffer, UserInput.RawMouseLastYOffset);
        if ((dx != 0 || dy != 0) && _shareMouse()) _mouse.Writer.TryWrite(new MouseInput(dx, dy, UserInput.MouseEventfMove, 0));
        return 0;
    }

    private Task PumpChannelsAsync(CancellationToken token) => Task.WhenAll(PumpKeysAsync(token), PumpMouseAsync(token));

    private async Task PumpKeysAsync(CancellationToken token)
    {
        try
        {
            while (await _keys.Reader.WaitToReadAsync(token))
                while (_keys.Reader.TryRead(out var key)) await _sendKeyboard(key);
        }
        catch (OperationCanceledException) { }
    }

    private async Task PumpMouseAsync(CancellationToken token)
    {
        try
        {
            while (await _mouse.Reader.WaitToReadAsync(token))
            {
                int dx = 0, dy = 0, wheel = 0;
                while (_mouse.Reader.TryRead(out var item))
                {
                    if (item.EventFlags != UserInput.MouseEventfMove)
                    {
                        // 按下/抬起不能和移动混进同一帧，先把攒下的移动发出去。
                        if (dx != 0 || dy != 0 || wheel != 0) await SendMouseFrameAsync(dx, dy, wheel);
                        dx = dy = wheel = 0;
                        await _sendMouse(item);
                        continue;
                    }
                    dx += item.DeltaX;
                    dy += item.DeltaY;
                    wheel += item.WheelDelta;
                }
                if (dx != 0 || dy != 0 || wheel != 0) await SendMouseFrameAsync(dx, dy, wheel);
            }
        }
        catch (OperationCanceledException) { }
    }

    private Task SendMouseFrameAsync(int dx, int dy, int wheel)
        => _sendMouse(new MouseInput(dx, dy, UserInput.MouseEventfMove | (wheel != 0 ? UserInput.MouseEventfWheel : 0), wheel));

    private nint KeyboardCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0) return UserInput.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
        var message = (int)wParam;
        var isDown = message is UserInput.WmKeyDown or UserInput.WmSysKeyDown;
        var isUp = message is UserInput.WmKeyUp or UserInput.WmSysKeyUp;
        if (isDown && (ushort)data.vkCode == EscapeVirtualKey && IsControlDown && IsAltDown)
        {
            _escapePressed();
            return UserInput.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }
        if (!isDown && !isUp) return UserInput.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        // 没勾"共享键盘"就完全不碰键盘：既不转发也不屏蔽，本机照常打字。
        if (!_shareKeyboard()) return UserInput.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        _keys.Writer.TryWrite(new KeyboardInput(isDown, (data.flags & UserInput.LlwkhfExtended) != 0, (ushort)data.vkCode, (ushort)data.scanCode));
        return 1;
    }

    private nint MouseCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0) return UserInput.CallNextHookEx(_mouseHook, code, wParam, lParam);
        var data = Marshal.PtrToStructure<MouseHookData>(lParam);
        // 被控端注入回来的输入钩子同样看得见。同机跑两个实例时不认这个标记就会无限回环。
        if ((data.flags & LlmhfInjected) != 0) return UserInput.CallNextHookEx(_mouseHook, code, wParam, lParam);
        // 和键盘同理：没勾"共享鼠标"就不拦也不发。
        if (!_shareMouse()) return UserInput.CallNextHookEx(_mouseHook, code, wParam, lParam);
        var message = (int)wParam;
        switch (message)
        {
            case UserInput.WmMouseMove:
                // 只负责把本机的移动吞掉，让控制端光标停在原地；真正的位移由原始输入给。
                return 1;
            case UserInput.WmMouseWheel:
                _mouse.Writer.TryWrite(new MouseInput(0, 0, UserInput.MouseEventfWheel, unchecked((short)((data.mouseData >> 16) & 0xFFFF))));
                return 1;
            case UserInput.WmLButtonDown:
            case UserInput.WmLButtonUp:
            case UserInput.WmRButtonDown:
            case UserInput.WmRButtonUp:
            case UserInput.WmMButtonDown:
            case UserInput.WmMButtonUp:
                _mouse.Writer.TryWrite(new MouseInput(0, 0, ButtonFlags(message), 0));
                return 1;
            default:
                return UserInput.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }
    }

    private static uint ButtonFlags(int message) => message switch
    {
        UserInput.WmLButtonDown => UserInput.MouseEventfLeftDown,
        UserInput.WmLButtonUp => UserInput.MouseEventfLeftUp,
        UserInput.WmRButtonDown => UserInput.MouseEventfRightDown,
        UserInput.WmRButtonUp => UserInput.MouseEventfRightUp,
        UserInput.WmMButtonDown => UserInput.MouseEventfMiddleDown,
        _ => UserInput.MouseEventfMiddleUp
    };

    private static bool IsControlDown => (UserInput.GetAsyncKeyState(VkControl) & 0x8000) != 0;
    private static bool IsAltDown => (UserInput.GetAsyncKeyState(VkMenu) & 0x8000) != 0;

    public void Dispose()
    {
        _cancellation.Cancel();
        _keys.Writer.TryComplete();
        _mouse.Writer.TryComplete();
        if (_win32ThreadId != 0) PostThreadMessage(_win32ThreadId, WmQuit, 0, 0);
        Volatile.Write(ref _started, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int ptX;
        public int ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Msg message, nint hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, int wParam, int lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
