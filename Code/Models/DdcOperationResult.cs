namespace MonitorSwitch.Models;

public sealed record DdcOperationResult(bool Succeeded, int Attempts, int LastError, TimeSpan Elapsed);
