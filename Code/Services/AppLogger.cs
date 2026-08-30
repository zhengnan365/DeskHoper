using System.Diagnostics;
using System.IO;

namespace MonitorSwitch.Services;

public static class AppLogger
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string DirectoryPath = AppPaths.LogsDirectory;

    public static Task InfoAsync(string message) => WriteAsync("INF", message, null);
    public static Task ErrorAsync(string message, Exception? exception = null) => WriteAsync("ERR", message, exception);
    public static Task PerfAsync(string message) => WriteAsync("PERF", message, null);
    public static Task ShutdownAsync(string step, long elapsedMs) => WriteAsync("SHUTDOWN", $"{step} elapsedMs={elapsedMs}", null);

    private static async Task WriteAsync(string level, string message, Exception? exception)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} [{level}] {message}{(exception is null ? string.Empty : $" | {exception}")}{Environment.NewLine}";
            Debug.Write(line);
            await Gate.WaitAsync();
            try { Directory.CreateDirectory(DirectoryPath); await File.AppendAllTextAsync(Path.Combine(DirectoryPath, $"monitor-switch-{DateTime.Today:yyyyMMdd}.log"), line); }
            finally { Gate.Release(); }
        }
        catch { Debug.WriteLine($"[MonitorSwitch logging failed] {message}"); }
    }
}
