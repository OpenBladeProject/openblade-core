using OpenBlade.Contracts;

namespace OpenBlade.Service;

[Flags]
internal enum ThermalConvergenceContradiction
{
    None = 0,
    Performance = 1 << 0,
    FanTopology = 1 << 1,
}

internal sealed record ThermalConvergenceCandidate(
    string ActiveUserSid,
    long AdmissionGeneration,
    PowerSource Destination,
    string DeviceAuthorityEpoch,
    string PerformanceDesiredRevision,
    string FanDesiredRevision,
    ThermalConvergenceContradiction Contradiction)
{
    public ThermalConvergenceCandidate Validate()
    {
        if (string.IsNullOrWhiteSpace(ActiveUserSid))
        {
            throw new ArgumentException("An active-user SID is required.", nameof(ActiveUserSid));
        }
        if (AdmissionGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AdmissionGeneration));
        }
        if (!Enum.IsDefined(Destination) || Destination == PowerSource.ExternalUnknown)
        {
            throw new ArgumentOutOfRangeException(nameof(Destination));
        }
        if (string.IsNullOrWhiteSpace(DeviceAuthorityEpoch))
        {
            throw new ArgumentException(
                "A durable device-authority epoch is required.",
                nameof(DeviceAuthorityEpoch));
        }
        if (string.IsNullOrWhiteSpace(PerformanceDesiredRevision))
        {
            throw new ArgumentException(
                "A performance desired revision is required.",
                nameof(PerformanceDesiredRevision));
        }
        if (string.IsNullOrWhiteSpace(FanDesiredRevision))
        {
            throw new ArgumentException(
                "A fan desired revision is required.",
                nameof(FanDesiredRevision));
        }
        if (Contradiction == ThermalConvergenceContradiction.None
            || (Contradiction & ~(
                ThermalConvergenceContradiction.Performance
                | ThermalConvergenceContradiction.FanTopology)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Contradiction));
        }
        return this;
    }

    public bool SameNormalizedClass(ThermalConvergenceCandidate other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return AdmissionGeneration == other.AdmissionGeneration
            && Destination == other.Destination
            && string.Equals(ActiveUserSid, other.ActiveUserSid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                DeviceAuthorityEpoch,
                other.DeviceAuthorityEpoch,
                StringComparison.Ordinal)
            && (!Contradiction.HasFlag(ThermalConvergenceContradiction.Performance)
                || string.Equals(
                    PerformanceDesiredRevision,
                    other.PerformanceDesiredRevision,
                    StringComparison.Ordinal))
            && (!Contradiction.HasFlag(ThermalConvergenceContradiction.FanTopology)
                || string.Equals(
                    FanDesiredRevision,
                    other.FanDesiredRevision,
                    StringComparison.Ordinal))
            && Contradiction == other.Contradiction;
    }
}

internal enum ThermalConvergenceNominationResult
{
    Accepted,
    Merged,
    AdmissionClosed,
}

/// <summary>
/// Owns the one mergeable automatic-convergence slot. Hardware serialization
/// remains the responsibility of <see cref="ServiceControlRuntime"/>; this type
/// only prevents observations from becoming an unbounded writer queue.
/// </summary>
internal sealed class ThermalConvergenceArbiter : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim available = new(0, 1);
    private ThermalConvergenceCandidate? pending;
    private long admissionGeneration;
    private bool admissionOpen;
    private bool disposed;

    public long OpenAdmission()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            pending = null;
            admissionOpen = true;
            return ++admissionGeneration;
        }
    }

    public long EnsureAdmissionOpen()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!admissionOpen)
            {
                pending = null;
                admissionOpen = true;
                admissionGeneration++;
            }
            return admissionGeneration;
        }
    }

    public long CloseAdmission()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            admissionOpen = false;
            pending = null;
            return ++admissionGeneration;
        }
    }

    public long CurrentAdmissionGeneration
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return admissionGeneration;
            }
        }
    }

    public ThermalConvergenceNominationResult TryNominate(
        ThermalConvergenceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.Validate();
        bool signal = false;
        ThermalConvergenceNominationResult result;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!admissionOpen || candidate.AdmissionGeneration != admissionGeneration)
            {
                return ThermalConvergenceNominationResult.AdmissionClosed;
            }

            if (pending?.SameNormalizedClass(candidate) == true)
            {
                return ThermalConvergenceNominationResult.Merged;
            }

            signal = pending is null;
            pending = candidate;
            result = ThermalConvergenceNominationResult.Accepted;
        }

        if (signal && available.CurrentCount == 0)
        {
            available.Release();
        }
        return result;
    }

    public async Task RunAsync(
        Func<ThermalConvergenceCandidate, CancellationToken, Task> executeAsync,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        while (!stoppingToken.IsCancellationRequested)
        {
            await available.WaitAsync(stoppingToken).ConfigureAwait(false);
            ThermalConvergenceCandidate? candidate;
            lock (gate)
            {
                ThrowIfDisposed();
                candidate = pending;
                pending = null;
            }
            if (candidate is not null)
            {
                await executeAsync(candidate, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            pending = null;
        }
        available.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
