using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MonitorSwitch.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId;
    public HotkeyService(HwndSource source) { _source = source; _source.AddHook(WndProc); }
    public bool Register(ModifierKeys modifiers, uint key, Action action, out int id)
    {
        id = ++_nextId;
        if (!RegisterHotKey(_source.Handle, id, (uint)modifiers, key)) { id = 0; return false; }
        _actions[id] = action; return true;
    }
    public void Unregister(int id) { if (id == 0) return; UnregisterHotKey(_source.Handle, id); _actions.Remove(id); }
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) { if (msg == WmHotKey && _actions.TryGetValue((int)wParam, out var action)) { action(); handled = true; } return 0; }
    public void Dispose() { foreach (var id in _actions.Keys.ToArray()) Unregister(id); _source.RemoveHook(WndProc); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hWnd, int id);
    [Flags] public enum ModifierKeys : uint { Alt = 1, Control = 2, Shift = 4, Win = 8 }
}
