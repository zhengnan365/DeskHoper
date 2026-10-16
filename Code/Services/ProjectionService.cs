using System.Diagnostics;
using System.IO;
using MonitorSwitch.Models;
using MonitorSwitch.Native;

namespace MonitorSwitch.Services;

public sealed class ProjectionService : IProjectionService
{
    public ProjectionMode? CurrentMode { get; private set; }

    public async Task<ProjectionMode?> RefreshCurrentModeAsync(CancellationToken cancellationToken = default)
    {
        CurrentMode = await Task.Run(() => TakeSnapshot(cancellationToken).Mode, cancellationToken);
        await AppLogger.InfoAsync($"QueryDisplayConfig topology={(CurrentMode?.ToString() ?? "Unknown")}");
        return CurrentMode;
    }

    public Task<TopologySnapshot> GetTopologySnapshotAsync(CancellationToken cancellationToken = default) => Task.Run(() => TakeSnapshot(cancellationToken), cancellationToken);

    public async Task<ProjectionResult> ApplyAsync(ProjectionMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == ProjectionMode.Unchanged) return ProjectionResult.NoChange;
        var argument = GetDisplaySwitchArgument(mode)!;
        try
        {
            await AppLogger.InfoAsync($"Projection request mode={mode} argument={argument}");
            var executable = Path.Combine(Environment.SystemDirectory, "DisplaySwitch.exe");
            using var process = Process.Start(new ProcessStartInfo(executable, argument) { UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return new ProjectionResult(false, null, "无法启动 Windows 投影切换程序。");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                await AppLogger.ErrorAsync($"Projection failed mode={mode} exitCode={process.ExitCode}");
                return new ProjectionResult(false, null, $"Windows 投影切换返回错误码 {process.ExitCode}。");
            }
            CurrentMode = await RefreshCurrentModeAsync(cancellationToken);
            await AppLogger.InfoAsync($"Projection result requested={mode} detected={(CurrentMode?.ToString() ?? "Unknown")}");
            return new ProjectionResult(true, CurrentMode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { await AppLogger.ErrorAsync($"Projection exception mode={mode}", ex); return new ProjectionResult(false, null, ex.Message); }
    }

    public static string? GetDisplaySwitchArgument(ProjectionMode mode) => mode switch
    {
        ProjectionMode.Internal => "/internal",
        ProjectionMode.Duplicate => "/clone",
        ProjectionMode.Extend => "/extend",
        ProjectionMode.External => "/external",
        ProjectionMode.Unchanged => null,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static TopologySnapshot TakeSnapshot(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DisplayConfig.GetDisplayConfigBufferSizes(DisplayConfig.QueryDatabaseCurrent, out var pathCount, out var modeCount) != 0) return TopologySnapshot.Unknown;
        var paths = new DisplayConfig.PathInfo[pathCount];
        var modes = new DisplayConfig.ModeInfo[modeCount];
        for (var index = 0; index < modeCount; index++) modes[index] = new DisplayConfig.ModeInfo { Details = new byte[48] };
        if (DisplayConfig.QueryDisplayConfig(DisplayConfig.QueryDatabaseCurrent, ref pathCount, paths, ref modeCount, modes, out var topologyId) != 0) return TopologySnapshot.Unknown;
        var mode = topologyId switch
        {
            1 => ProjectionMode.Internal,
            2 => ProjectionMode.Duplicate,
            4 => ProjectionMode.Extend,
            8 => ProjectionMode.External,
            _ => (ProjectionMode?)null
        };
        return new TopologySnapshot(mode, paths.Take((int)pathCount).Count(path => (path.Flags & DisplayConfig.PathIsActive) != 0));
    }
}
