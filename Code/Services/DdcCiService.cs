using System.ComponentModel;
using System.Runtime.InteropServices;
using MonitorSwitch.Models;
using MonitorSwitch.Native;

namespace MonitorSwitch.Services;

public sealed class DdcCiService : IDdcCiService
{
    private readonly MonitorIdentityService _identityService = new();
    private readonly Dictionary<string, PhysicalMonitorLease> _leases = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;

    public async Task<IReadOnlyList<MonitorDevice>> RefreshAsync(IProgress<MonitorDevice>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try { return await Task.Run(() => RefreshCore(progress, cancellationToken), cancellationToken); }
        finally { _operationGate.Release(); }
    }

    public async Task<uint?> ReadInputAsync(string monitorId, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try { return await Task.Run(() => ReadInputCore(monitorId, cancellationToken), cancellationToken); }
        finally { _operationGate.Release(); }
    }

    public async Task<DdcOperationResult> SetInputAsync(string monitorId, uint inputValue, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!_leases.TryGetValue(monitorId, out var lease)) return new DdcOperationResult(false, 0, 0, stopwatch.Elapsed);
            var lastError = 0;
            var result = await DdcRetryPolicy.ExecuteAsync(async attempt =>
            {
                var nativeResult = await Task.Run(() =>
                {
                    var succeeded = Dxva2.SetVCPFeature(lease.Handle, Dxva2.InputSelectVcpCode, inputValue);
                    return (Succeeded: succeeded, LastError: succeeded ? 0 : Marshal.GetLastWin32Error());
                }, cancellationToken);
                lastError = nativeResult.LastError;
                await AppLogger.InfoAsync($"DDC write monitor={monitorId} vcp=0x60 value={inputValue} attempt={attempt} succeeded={nativeResult.Succeeded} lastError={lastError} elapsedMs={stopwatch.ElapsedMilliseconds}");
                return nativeResult.Succeeded;
            }, cancellationToken);
            return new DdcOperationResult(result.Succeeded, result.Attempts, lastError, stopwatch.Elapsed);
        }
        finally { _operationGate.Release(); }
    }

    private IReadOnlyList<MonitorDevice> RefreshCore(IProgress<MonitorDevice>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ThrowIfDisposed();
        DisposeLeases();
        _identityService.BeginScan();
            var results = new List<MonitorDevice>();
            var handles = new List<nint>();
            if (!User32.EnumDisplayMonitors(0, 0, (monitor, _, _, _) => { handles.Add(monitor); return true; }, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors 失败。");

            foreach (var hMonitor in handles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new User32.MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<User32.MonitorInfoEx>(), szDevice = string.Empty };
                var displayName = User32.GetMonitorInfo(hMonitor, ref info) ? info.szDevice : "未知显示器";
                var identity = _identityService.TryGetIdentity(displayName);
                if (!Dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0) continue;
                var physicals = new Dxva2.PhysicalMonitor[count];
                if (!Dxva2.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals)) continue;
                var adopted = new bool[physicals.Length];
                try
                {
                    for (var i = 0; i < physicals.Length; i++)
                    {
                        var physical = physicals[i];
                        var id = identity?.StableId ?? $"runtime|{displayName}|{physical.szPhysicalMonitorDescription}|{i}";
                        var lease = new PhysicalMonitorLease(physical);
                        _leases[id] = lease;
                        adopted[i] = true;
                        var bounds = new DisplayBounds(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top);
                        var canRead = TryReadInput(lease.Handle, out var input);
                        var settled = BuildDevice(id, identity, physical, displayName, hMonitor, bounds, results.Count + 1, canRead ? input : null, canRead, canRead ? "DDC/CI 可读取" : "DDC/CI 不可读取或当前不可用");
                        results.Add(settled);
                        if (results.Count == 1) _ = AppLogger.PerfAsync($"First monitor readyMs={stopwatch.ElapsedMilliseconds} display={displayName}");
                        progress?.Report(settled);
                        _ = AppLogger.InfoAsync($"Monitor scan windowsDisplay={displayName} physicalName={physical.szPhysicalMonitorDescription} stableId={(identity?.StableId ?? "UNRESOLVED")} devicePath={(identity?.DevicePath ?? "UNRESOLVED")} ddcReadable={canRead} vcp60={(canRead ? input : 0)}");
                    }
                }
                catch
                {
                    for (var i = 0; i < physicals.Length; i++) if (!adopted[i]) _ = Dxva2.DestroyPhysicalMonitor(physicals[i].hPhysicalMonitor);
                    throw;
                }
            }
        _ = AppLogger.PerfAsync($"Refresh totalMs={stopwatch.ElapsedMilliseconds} physicalMonitorCount={results.Count}");
        _ = AppLogger.InfoAsync($"Monitor scan complete physicalMonitorCount={results.Count}");
        return results;
    }

    private static MonitorDevice BuildDevice(string id, MonitorHardwareIdentity? identity, Dxva2.PhysicalMonitor physical, string displayName, nint hMonitor, DisplayBounds bounds, int displayNumber, uint? currentInput, bool isDdcSupported, string ddcStatus) => new MonitorDevice
    {
        Id = id,
        StableId = identity?.StableId,
        FriendlyName = identity?.Model ?? physical.szPhysicalMonitorDescription,
        Manufacturer = identity?.Manufacturer,
        Model = identity?.Model ?? physical.szPhysicalMonitorDescription,
        SerialNumber = identity?.SerialNumber,
        DevicePath = identity?.DevicePath,
        WindowsDisplayName = displayName,
        HMonitor = hMonitor,
        PhysicalMonitorHandle = physical.hPhysicalMonitor,
        CurrentInput = currentInput,
        IsDdcSupported = isDdcSupported,
        DdcStatus = ddcStatus,
        DisplayNumber = displayNumber,
        Bounds = bounds
    };

    private uint? ReadInputCore(string monitorId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        uint value = 0;
        var read = _leases.TryGetValue(monitorId, out var lease) && TryReadInput(lease.Handle, out value);
        _ = AppLogger.InfoAsync($"DDC read monitor={monitorId} vcp=0x60 succeeded={read} value={(read ? value : 0)} lastError={(read ? 0 : Marshal.GetLastWin32Error())}");
        return read ? value : null;
    }

    private static bool TryReadInput(nint handle, out uint value)
    {
        return Dxva2.GetVCPFeatureAndVCPFeatureReply(handle, Dxva2.InputSelectVcpCode, out _, out value, out _);
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync();
        try { if (!_disposed) { DisposeLeases(); _disposed = true; await AppLogger.InfoAsync("DDC physical monitor handles disposed."); } }
        finally { _operationGate.Release(); }
    }
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        _disposed = true;
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await _operationGate.WaitAsync(timeoutCts.Token);
            try { DisposeLeases(); await AppLogger.InfoAsync("DDC physical monitor handles disposed during shutdown."); }
            finally { _operationGate.Release(); }
        }
        catch (OperationCanceledException) { await AppLogger.InfoAsync("[Shutdown] release DDC handles timeout; process exit will release remaining native handles."); }
    }
    public void RequestShutdown() => _disposed = true;
    private void DisposeLeases() { foreach (var lease in _leases.Values) lease.Dispose(); _leases.Clear(); }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class PhysicalMonitorLease(Dxva2.PhysicalMonitor physical) : IDisposable
    {
        private Dxva2.PhysicalMonitor[]? _physical = [physical];
        public nint Handle => _physical?[0].hPhysicalMonitor ?? 0;
        public void Dispose() { var item = Interlocked.Exchange(ref _physical, null); if (item is not null) _ = Dxva2.DestroyPhysicalMonitor(item[0].hPhysicalMonitor); }
    }
}
