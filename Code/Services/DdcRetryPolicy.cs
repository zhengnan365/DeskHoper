namespace MonitorSwitch.Services;

public sealed record RetryExecutionResult(bool Succeeded, int Attempts);

public static class DdcRetryPolicy
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250)];

    public static async Task<RetryExecutionResult> ExecuteAsync(Func<int, Task<bool>> attempt, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken = default)
    {
        for (var index = 1; index <= 3; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await attempt(index)) return new RetryExecutionResult(true, index);
            if (index < 3) await delay(Delays[index - 1], cancellationToken);
        }
        return new RetryExecutionResult(false, 3);
    }

    public static Task<RetryExecutionResult> ExecuteAsync(Func<int, Task<bool>> attempt, CancellationToken cancellationToken = default) =>
        ExecuteAsync(attempt, Task.Delay, cancellationToken);
}
