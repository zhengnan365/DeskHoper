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

    private readonly Action<KeyboardInput> _sendKeyboard;
    private readonly Action<MouseInput> _sendMouse;
    private readonly Action _escapePressed;
    private readonly Channel<KeyboardInput> _keys = Channel.CreateUnbounded<KeyboardInput>();
    private readonly Channel<MouseInput> _mouse = Channel.CreateUnbounded<MouseInput>();
    private readonly UserInput.HookProc _keyboardProc;
    private readonly UserInput.HookProc _mouseProc;
    private readonly CancellationTokenSource _cancellation = new();
    private nint _keyboardHook;
    private nint _mouseHook;
    private uint _win32ThreadId;
    private int _started;
    private int _lastX;
    private int _lastY;
    private bool _hasLastPosition;

    public InputForwarder(Action<KeyboardInput> sendKeyboard, Action<MouseInput> sendMouse, Action escapePressed)
    {
        _sendKeyboard = sendKeyboard;
        _sendMouse = sendMouse;
        _escapePressed = escapePressed;
        // 委托必须被字段持有：非托管回调进来时托管侧要是已经把它 GC 掉，进程直接崩在回调里。
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    public bool IsRunning => Volatile.Read(ref _started) == 1;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var thread = new Thread(Run) { IsBackground = true, Name = "MonitorSwitch.InputForwarder" };
        thread.Start();
    }

    private void Run()
    {
        _win32ThreadId = GetCurrentThreadId();
        var instance = UserInput.GetModuleHandle(null);
        _keyboardHook = UserInput.SetWindowsHookEx(UserInput.WhKeyboardLl, _keyboardProc, instance, 0);
        _mouseHook = UserInput.SetWindowsHookEx(UserInput.WhMouseLl, _mouseProc, instance, 0);
        if (_keyboardHook == nint.Zero && _mouseHook == nint.Zero)
        {
            Volatile.Write(ref _started, 0);
            _ = AppLogger.ErrorAsync("Input hooks could not be installed", new InvalidOperationException("SetWindowsHookEx 两次都返回 0。"));
            return;
        }
        _ = PumpChannelsAsync(_cancellation.Token);
        // 低级钩子的回调是在装它的那个线程上派发的，所以这个线程必须有消息循环。
        while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        if (_keyboardHook != nint.Zero) UserInput.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != nint.Zero) UserInput.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = nint.Zero;
        Volatile.Write(ref _started, 0);
    }

    private Task PumpChannelsAsync(CancellationToken token) => Task.WhenAll(PumpKeysAsync(token), PumpMouseAsync(token));

    private async Task PumpKeysAsync(CancellationToken token)
    {
        try
        {
            while (await _keys.Reader.WaitToReadAsync(token))
                while (_keys.Reader.TryRead(out var key)) _sendKeyboard(key);
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
                        if (dx != 0 || dy != 0 || wheel != 0) _sendMouse(new MouseInput(dx, dy, UserInput.MouseEventfMove | (wheel != 0 ? UserInput.MouseEventfWheel : 0), wheel));
                        dx = dy = wheel = 0;
                        _sendMouse(item);
                        continue;
                    }
                    dx += item.DeltaX;
                    dy += item.DeltaY;
                    wheel += item.WheelDelta;
                }
                if (dx != 0 || dy != 0 || wheel != 0) _sendMouse(new MouseInput(dx, dy, UserInput.MouseEventfMove | (wheel != 0 ? UserInput.MouseEventfWheel : 0), wheel));
            }
        }
        catch (OperationCanceledException) { }
    }

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
        _keys.Writer.TryWrite(new KeyboardInput(isDown, (data.flags & UserInput.LlwkhfExtended) != 0, (ushort)data.vkCode, (ushort)data.scanCode));
        return 1;
    }

    private nint MouseCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0) return UserInput.CallNextHookEx(_mouseHook, code, wParam, lParam);
        var data = Marshal.PtrToStructure<MouseHookData>(lParam);
        // 被控端注入回来的输入钩子同样看得见。同机跑两个实例时不认这个标记就会无限回环。
        if ((data.flags & LlmhfInjected) != 0) return UserInput.CallNextHookEx(_mouseHook, code, wParam, lParam);
        var message = (int)wParam;
        switch (message)
        {
            case UserInput.WmMouseMove:
            {
                // 钩子给的是绝对坐标，对端要的是相对位移，不然两端分辨率一不同光标就飞了。
                var dx = _hasLastPosition ? data.ptX - _lastX : 0;
                var dy = _hasLastPosition ? data.ptY - _lastY : 0;
                _lastX = data.ptX;
                _lastY = data.ptY;
                _hasLastPosition = true;
                if (dx != 0 || dy != 0) _mouse.Writer.TryWrite(new MouseInput(dx, dy, UserInput.MouseEventfMove, 0));
                return 1;
            }
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
