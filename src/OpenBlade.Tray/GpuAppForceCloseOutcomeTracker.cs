namespace OpenBlade.Tray;

internal readonly record struct GpuAppProcessIdentity(
    int ProcessId,
    long StartTimeUtcTicks,
    string Name);

internal sealed class GpuAppProcessIdentityComparer : IEqualityComparer<GpuAppProcessIdentity>
{
    internal static GpuAppProcessIdentityComparer Instance { get; } = new();

    private GpuAppProcessIdentityComparer()
    {
    }

    public bool Equals(GpuAppProcessIdentity x, GpuAppProcessIdentity y) =>
        x.ProcessId == y.ProcessId
        && x.StartTimeUtcTicks == y.StartTimeUtcTicks
        && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(GpuAppProcessIdentity obj) => HashCode.Combine(
        obj.ProcessId,
        obj.StartTimeUtcTicks,
        StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
}

internal static class GpuAppProcessIdentityFilter
{
    internal static T[] MatchTargets<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> targets,
        Func<T, GpuAppProcessIdentity> identitySelector) =>
        Filter(current, targets, identitySelector, includeMatches: true);

    internal static T[] ExceptTargets<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> targets,
        Func<T, GpuAppProcessIdentity> identitySelector) =>
        Filter(current, targets, identitySelector, includeMatches: false);

    private static T[] Filter<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> targets,
        Func<T, GpuAppProcessIdentity> identitySelector,
        bool includeMatches)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(identitySelector);

        HashSet<GpuAppProcessIdentity> identities = targets
            .Select(identitySelector)
            .ToHashSet(GpuAppProcessIdentityComparer.Instance);
        return current
            .Where(item => identities.Contains(identitySelector(item)) == includeMatches)
            .ToArray();
    }
}

internal sealed class GpuAppForceCloseOutcomeTracker
{
    private readonly HashSet<GpuAppProcessIdentity> approvedTargets = new(
        GpuAppProcessIdentityComparer.Instance);
    private readonly List<(GpuAppProcessIdentity Target, string Message)> failures = [];

    internal bool HasApprovedTargets => approvedTargets.Count > 0;

    internal void Approve(GpuAppProcessIdentity target) => approvedTargets.Add(target);

    internal bool WasApproved(GpuAppProcessIdentity target) => approvedTargets.Contains(target);

    internal void RecordFailure(GpuAppProcessIdentity target, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        failures.Add((target, message));
    }

    internal string[] ReconcileFailures(
        IEnumerable<GpuAppProcessIdentity> survivingTargets)
    {
        ArgumentNullException.ThrowIfNull(survivingTargets);
        HashSet<GpuAppProcessIdentity> survivors = survivingTargets.ToHashSet(
            GpuAppProcessIdentityComparer.Instance);
        return failures
            .Where(failure => survivors.Contains(failure.Target))
            .Select(failure => failure.Message)
            .ToArray();
    }
}

internal static class GpuAppCleanupPolicy
{
    internal static bool ShouldAbortGracefulClose(bool requestFailed, int resultCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resultCount);
        return requestFailed && resultCount == 0;
    }
}

internal static class GpuAppForceCloseVerification
{
    internal static async Task<TResponse> VerifyAsync<TResponse>(
        Func<CancellationToken, Task<TResponse>> query,
        Func<TResponse, bool> shouldRetry,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeProvider timeProvider,
        TimeSpan settleBudget,
        TimeSpan retryInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(shouldRetry);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(settleBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryInterval, TimeSpan.Zero);

        long startedAt = timeProvider.GetTimestamp();
        while (true)
        {
            TResponse response = await query(cancellationToken).ConfigureAwait(false);
            TimeSpan elapsed = timeProvider.GetElapsedTime(startedAt);
            if (!shouldRetry(response) || elapsed >= settleBudget)
            {
                return response;
            }

            TimeSpan remaining = settleBudget - elapsed;
            TimeSpan nextDelay = retryInterval <= remaining
                ? retryInterval
                : remaining;
            await delay(nextDelay, cancellationToken).ConfigureAwait(false);
        }
    }
}
