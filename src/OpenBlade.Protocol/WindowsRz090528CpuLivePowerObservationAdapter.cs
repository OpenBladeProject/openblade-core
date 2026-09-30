namespace OpenBlade.Protocol;

// Actual synchronous scope outcome, not interpretation or mutation authority.
internal sealed record CpuLivePowerNativeObservationResult(CpuPmObservationBatch Batch,
    bool WorkerTerminal, bool CleanupComplete);

internal sealed partial class WindowsRz090528CpuNativeSession
{
    // Developer observation only. The independently issued approval must retain
    // its exact attended profile and fresh run-owned lifetime. Merely finding an
    // installed/running driver cannot authorize this kernel-copy path.
    internal CpuLivePowerNativeObservationResult ObservePower(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> readFreshLifetime, IRz090528CpuLivePowerRangeMetadataReader? metadataReader,
        CancellationToken token = default) => WindowsRz090528CpuQueryAdapter.ObservePowerWithPlatform(
            approval, readFreshLifetime, metadataReader, platform, token);
}

internal sealed partial class WindowsRz090528CpuQueryAdapter
{
    internal static CpuLivePowerNativeObservationResult ObservePowerWithPlatform(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> lifetime, IRz090528CpuLivePowerRangeMetadataReader? metadataReader,
        ICpuQueryWindowsPlatform platform, CancellationToken token)
    {
        if (approval?.Profile != CpuPmObservationProfile.RangeSingleSample || metadataReader is null
            || platform is not ICpuPmWindowsPlatform pm)
        { return new(WindowsRz090528CpuPmObservationAdapter.Denied(profile: CpuPmObservationProfile.RangeSingleSample), true, true); }
        return ObservePmScope(approval, lifetime, pm, new PmClock(), token, metadataReader);
    }
}
