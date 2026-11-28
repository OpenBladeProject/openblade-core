namespace OpenBlade.Setup;

// The callback reads only the already-retained original handle. This bounded
// monotonic wait never looks up a PID and supplies no transport drain evidence.
internal static class SetupRetainedOriginalExit
{
    internal static async Task WaitAsync(Func<bool> exited, TimeSpan budget, CancellationToken token, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        long started = clock.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan remaining = budget - clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { throw new TimeoutException("Original process exit was not confirmed within the stop budget."); }
            bool signaled = exited();
            token.ThrowIfCancellationRequested();
            remaining = budget - clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { throw new TimeoutException("Original process exit observation exceeded the stop budget."); }
            if (signaled) { return; }
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(20) ? remaining : TimeSpan.FromMilliseconds(20), clock, token)
                .ConfigureAwait(false);
        }
    }
}
