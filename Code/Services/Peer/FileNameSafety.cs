using System.IO;

namespace MonitorSwitch.Services;

/// 对端传来的文件名一律视为不可信：必须收敛成“只能落在接收目录里”的单个文件名字。
public static class FileNameSafety
{
    private const int MaxNameLength = 120;
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();
    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "CLOCK$", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    public static string Sanitize(string? name, string fallback = "未命名文件")
    {
        var candidate = (name ?? string.Empty).Trim();
        // 不管对方给的是相对路径、UNC 还是带盘符的绝对路径，都只取最后一段。
        var separator = candidate.LastIndexOfAny(['/', '\\']);
        if (separator >= 0) candidate = candidate[(separator + 1)..];
        var cleaned = new string(candidate.Select(character => Invalid.Contains(character) || character == ':' || character == '*' || character == '?' ? '_' : character).ToArray()).Trim();
        cleaned = cleaned.TrimStart('.').Trim();
        if (cleaned.Length == 0) return fallback;
        if (cleaned.Length > MaxNameLength) cleaned = cleaned[..80] + "_" + cleaned[^36..];
        var stem = cleaned.Split('.')[0];
        return Reserved.Any(reserved => string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase)) ? "_" + cleaned : cleaned;
    }

    /// 同名不覆盖：必要时加 (1) (2) …
    public static string UniquePath(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, index == 1 ? fileName : $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
