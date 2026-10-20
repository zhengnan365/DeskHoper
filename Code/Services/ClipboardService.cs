using System.Windows;
using System.Windows.Interop;
using MonitorSwitch.Native;

namespace MonitorSwitch.Services;

/// 本机剪贴板的“看”和“写”。只在 UI 线程使用（WPF 剪贴板要求 STA）。
public sealed class ClipboardService : IDisposable
{
    private readonly HwndSource _source;
    private string? _lastKnownText;

    public ClipboardService(HwndSource source)
    {
        _source = source;
        source.AddHook(WndProc);
        User32.AddClipboardFormatListener(source.Handle);
    }

    public event Action<string>? LocalTextChanged;
    public event Action<string>? TextRejected;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == User32.WmClipboardUpdate) HandleClipboardUpdate();
        return nint.Zero;
    }

    /// 刚连上时把本机当前剪贴板当作“已知内容”，避免一建立连接就把上一次的旧内容互推一遍。
    public void CaptureBaseline() => _lastKnownText = ReadText();

    public void HandleClipboardUpdate()
    {
        var text = ReadText();
        if (!ClipboardSyncPolicy.HasChanged(text, _lastKnownText)) return;
        _lastKnownText = text;
        if (ClipboardSyncPolicy.IsTooLong(text))
        {
            TextRejected?.Invoke($"剪贴板内容超过 {ClipboardSyncPolicy.MaxTextLength / 1024} KB，没有同步。");
            return;
        }
        LocalTextChanged?.Invoke(text!);
    }

    public void ApplyRemoteText(string text)
    {
        _lastKnownText = text;
        if (!ClipboardSyncPolicy.HasChanged(ReadText(), text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { _ = AppLogger.ErrorAsync("写入剪贴板失败", ex); }
    }

    private static string? ReadText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (Exception ex)
        {
            _ = AppLogger.ErrorAsync("读取剪贴板失败", ex);
            return null;
        }
    }

    public void Dispose()
    {
        User32.RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
    }
}
