using System.Collections.Specialized;
using System.IO;
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
    public void CaptureBaseline()
    {
        _lastKnownText = ReadText();
    }

    public void HandleClipboardUpdate()
    {
        // 只跟文本：本机复制文件不再自动发给对面，那会让"自己拷个文件"变成一次误发。
        // 对面传来的文件仍然会写进本机剪贴板，所以过去之后能直接粘。
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
        if (!ClipboardSyncPolicy.HasChanged(ReadText(), text))
        {
            _ = AppLogger.InfoAsync($"Clipboard from peer already present, skipped write ({text.Length} chars)");
            return;
        }
        SetWithRetry(() => Clipboard.SetText(text), "写入剪贴板失败");
    }

    public void ApplyRemoteFiles(IReadOnlyList<string> paths)
    {
        _lastKnownText = null;
        var collection = new StringCollection();
        collection.AddRange(paths.ToArray());
        SetWithRetry(() => Clipboard.SetFileDropList(collection), "写入剪贴板文件列表失败");
    }

    /// 剪贴板是全局互斥资源，别的进程正打开时 OpenClipboard 会直接抛 CLIPBRD_E_CANT_OPEN。
    /// 这里必须在 UI 线程上调用，所以重试次数和间隔要克制：太短会偶发同步不上，太长会卡住界面。
    private const int WriteAttempts = 12;
    private const int WriteRetryDelayMs = 80;

    private void SetWithRetry(Action set, string failureMessage)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { set(); return; }
            catch (Exception ex)
            {
                if (attempt >= WriteAttempts)
                {
                    _ = AppLogger.ErrorAsync($"{failureMessage}（已重试 {attempt} 次）", ex);
                    TextRejected?.Invoke("剪贴板正被其他程序占用，这次没有同步过去。");
                    return;
                }
                Thread.Sleep(WriteRetryDelayMs);
            }
        }
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
