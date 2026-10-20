namespace MonitorSwitch.Services;

/// 剪贴板要不要同步的判断集中在这里，方便单独测。
/// “和已知内容相同就不发”同时就是防回声的机制：收到对端内容时先把已知内容对齐成同一份，
/// 本机随后收到的那次 WM_CLIPBOARDUPDATE 就会被判为没有变化，不会再推回去。
public static class ClipboardSyncPolicy
{
    public const int MaxTextLength = 1024 * 1024;

    public static bool HasChanged(string? text, string? lastKnown)
        => !string.IsNullOrEmpty(text) && !string.Equals(text, lastKnown, StringComparison.Ordinal);

    public static bool IsTooLong(string? text) => text?.Length > MaxTextLength;
}
