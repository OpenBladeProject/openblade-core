using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsRz090528CpuLivePowerObservationTests
{
    [Fact]
    public void ObtainingInstalledFacadeAndDeniedObservationCreatesNoAuthority()
    {
        var session = WindowsRz090528CpuQueryAdapter.GetInstalledNativeSession();
        var result = session.ObservePower(null, () => throw new InvalidOperationException(), new Metadata());
        Assert.False(result.Batch.Complete); Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
        Assert.Same(session, WindowsRz090528CpuQueryAdapter.GetInstalledNativeSession());
    }

    [Fact]
    public void WrongProfileOrMissingMetadataCannotRunLifetimeOrPlatformCallbacks()
    {
        var platform = new Platform(); var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        var wrong = CpuPmAttendedApproval.Acknowledge(true, true, platform.Lifetime);
        Assert.False(session.ObservePower(wrong, () => throw new InvalidOperationException(), new Metadata()).Batch.Complete);
        Assert.False(session.ObservePower(Approval(platform), () => throw new InvalidOperationException(), null).Batch.Complete);
        Assert.Equal(0, platform.Arms); Assert.True(wrong.Consume());
    }

    [Fact]
    public void LifetimeMismatchConsumesApprovalAndReplayCannotArmEvenWithMatchingLifetime()
    {
        var platform = new Platform(); var session = WindowsRz090528CpuNativeSession.ForPlatform(platform); var approval = Approval(platform);
        var result = session.ObservePower(approval, () => null, new Metadata());
        Assert.False(result.Batch.Complete); Assert.True(result.CleanupComplete); Assert.Equal(0, platform.Arms);
        Assert.False(session.ObservePower(approval, () => platform.Lifetime, new Metadata()).Batch.Complete);
        Assert.Equal(0, platform.Arms);
    }

    [Fact]
    public void UnknownArmCleanupReportsActualUncertaintyAndRetainsSameNativeLatch()
    {
        var platform = new Platform(); var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        var result = session.ObservePower(Approval(platform), () => platform.Lifetime, new Metadata());
        Assert.True(result.WorkerTerminal); Assert.False(result.CleanupComplete); Assert.Equal(1, platform.Arms);
        var replacement = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        Assert.Same(session, replacement);
        Assert.Equal(CpuPmObservationFailure.Busy, replacement.ObservePower(Approval(platform), () => platform.Lifetime, new Metadata()).Batch.Diagnostic!.Failure);
        Assert.Equal(CpuLiveQueryStatus.Busy, replacement.Query(CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true),
            () => throw new InvalidOperationException()).Status);
        Assert.Equal(1, platform.Arms);
    }

    private static CpuPmAttendedApproval Approval(Platform platform) =>
        CpuPmAttendedApproval.Acknowledge(true, true, platform.Lifetime, CpuPmObservationProfile.RangeSingleSample);
    private sealed class Metadata : IRz090528CpuLivePowerRangeMetadataReader
    {
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion> QueryVersion(Action checkpoint) => throw new InvalidOperationException();
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues> GetDValues(Action checkpoint) => throw new InvalidOperationException();
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax> GetMinMaxSteppingValues(Action checkpoint) => throw new InvalidOperationException();
    }
    private sealed class Platform : ICpuPmWindowsPlatform
    {
        internal readonly CpuPmRunOwnedDriverLifetime Lifetime = new(Guid.NewGuid(), Guid.NewGuid(), true);
        internal int Arms;
        public ICpuPmContinuityScope ArmContinuity(CpuPmRunOwnedDriverLifetime lifetime) { Arms++; throw new OperationCanceledException(); }
        public DeviceIdentity ReadUsb() => throw new InvalidOperationException();
        public CpuPmProcessorPackage ReadPackage() => throw new InvalidOperationException();
        public CpuPmServiceSnapshot ReadService() => throw new InvalidOperationException();
        public HostIdentityReadResult ReadHost() => throw new InvalidOperationException();
        public CpuQueryProcessorIdentity ReadProcessor() => throw new InvalidOperationException();
        public bool ControllersAbsent() => throw new InvalidOperationException();
        public InstalledCpuBackendSnapshot ReadBackend() => throw new InvalidOperationException();
        public IDisposable PinBackendFiles() => throw new InvalidOperationException();
        public ICpuQueryDevice OpenExistingDevice() => throw new InvalidOperationException();
        public ICpuQueryMutex CreateSharedMutex() => throw new InvalidOperationException();
    }
}
