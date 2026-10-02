using System.Diagnostics;

namespace OpenBlade.Protocol;

// One initiating request, tied to the actual retained installed device lease.
// A failed callback stays revoked even if a later callback recovers.
internal sealed class CpuPmInstalledApproval(CpuInstalledBackendLease lease, Func<bool> current)
{
    private readonly long created = Stopwatch.GetTimestamp();
    private int consumed, revoked;
    internal CpuInstalledBackendLease Lease => lease;
    internal bool Consume(ICpuQueryWindowsPlatform platform) => Interlocked.Exchange(ref consumed, 1) == 0
        && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2)
        && lease.Matches(platform) && lease.IsCurrent && IsCurrent();
    internal bool IsCurrent()
    {
        if (Volatile.Read(ref revoked) != 0) { return false; }
        try { if (lease.LifetimeCurrent && current()) { return Volatile.Read(ref revoked) == 0; } }
        catch { /* Request evidence is unavailable. */ }
        Interlocked.Exchange(ref revoked, 1);
        return false;
    }
}

// Actual synchronous scope outcome, not interpretation or mutation authority.
internal sealed record CpuLivePowerNativeObservationResult(CpuPmObservationBatch Batch,
    bool WorkerTerminal, bool CleanupComplete);

internal sealed partial class WindowsRz090528CpuNativeSession
{
    internal CpuLivePowerNativeObservationResult ObservePowerInstalled(CpuPmInstalledApproval? approval,
        Func<DeviceIdentity?> readFreshUsbIdentity, IRz090528CpuLivePowerRangeMetadataReader? metadataReader,
        CancellationToken token = default) => WindowsRz090528CpuQueryAdapter.ObservePowerInstalledWithPlatform(
            approval, readFreshUsbIdentity, metadataReader, platform, token);

    // Developer observation only. The independently issued approval must retain
    // its exact attended profile and fresh run-owned lifetime. Merely finding an
    // installed/running driver cannot authorize this kernel-copy path.
    internal CpuLivePowerNativeObservationResult ObservePower(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> readFreshLifetime, IRz090528CpuLivePowerRangeMetadataReader? metadataReader,
        CancellationToken token = default) => WindowsRz090528CpuQueryAdapter.ObservePowerWithPlatform(
            approval, readFreshLifetime, metadataReader, platform, token);

    // The attended child owns this already-armed scope through its complete
    // experiment. Borrowing never registers or disposes its notifications.
    internal CpuLivePowerNativeObservationResult ObservePower(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> readFreshLifetime, IRz090528CpuLivePowerRangeMetadataReader? metadataReader,
        ICpuPmContinuityScope borrowedContinuity, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(borrowedContinuity);
        return WindowsRz090528CpuQueryAdapter.ObservePowerWithPlatform(
            approval, readFreshLifetime, metadataReader, platform, token, borrowedContinuity);
    }
}

internal sealed partial class WindowsRz090528CpuQueryAdapter
{
    internal static CpuLivePowerNativeObservationResult ObservePowerInstalledWithPlatform(CpuPmInstalledApproval? approval,
        Func<DeviceIdentity?> readFreshUsbIdentity, IRz090528CpuLivePowerRangeMetadataReader? metadataReader,
        ICpuQueryWindowsPlatform platform, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(readFreshUsbIdentity);
        if (metadataReader is null || platform is not ICpuPmWindowsPlatform pm)
        { return new(WindowsRz090528CpuPmObservationAdapter.Denied(profile: CpuPmObservationProfile.RangeSingleSample), true, true); }
        return ObservePmScope(null, null!, pm, new PmClock(), token, metadataReader,
            installedApproval: approval, readInstalledUsb: readFreshUsbIdentity);
    }

    internal static CpuLivePowerNativeObservationResult ObservePowerWithPlatform(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> lifetime, IRz090528CpuLivePowerRangeMetadataReader? metadataReader,
        ICpuQueryWindowsPlatform platform, CancellationToken token, ICpuPmContinuityScope? borrowedContinuity = null)
    {
        if (approval?.Profile != CpuPmObservationProfile.RangeSingleSample || metadataReader is null
            || platform is not ICpuPmWindowsPlatform pm)
        { return new(WindowsRz090528CpuPmObservationAdapter.Denied(profile: CpuPmObservationProfile.RangeSingleSample), true, true); }
        return ObservePmScope(approval, lifetime, pm, new PmClock(), token, metadataReader, borrowedContinuity);
    }
}
