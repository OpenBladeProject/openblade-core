using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsRz090528CpuPmObservationAdapterTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CommandEndFailureRetainsRawCapEvidenceWithoutPostAdmission(bool capabilityCommand, bool cheapGuard)
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        bool current = true;
        fake.AfterRegisterRead = (address, command) =>
        {
            if (capabilityCommand ? address == 0x03B10A88 && command == 0x82 : address == CpuLivePowerFusedCap.RegisterA0B)
            { current = false; }
        };
        var result = session.ObservePowerInstalled(held!.CreateGetterPowerObservationApproval(
            () => cheapGuard || current, () => !cheapGuard || current),
            fake.ReadUsb, new CompleteMetadata());
        Assert.False(result.Batch.Complete); Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
        if (capabilityCommand)
        {
            var capability = Assert.IsType<CpuPmCapabilityObservation>(result.Batch.RangeObservation!.Capability);
            Assert.True(capability.ReturnedWordTransportSucceeded); Assert.False(capability.PostReadAdmissionPassed);
        }
        else
        {
            Assert.True(result.Batch.FusedCap!.A0B!.TransportSucceeded);
            Assert.False(result.Batch.FusedCap.A0B.PostReadAdmissionPassed); Assert.Null(result.Batch.FusedCap.Candidate);
        }
    }

    [Fact]
    public void ScopedTransportGuardReducesDiscoveryWithoutChangingMailboxOrCopyTraffic()
    {
        (Fake Platform, int Guards) Run(bool optimized)
        {
            var fake = new Fake { Installed = true };
            var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
            using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
            int guards = 0;
            Func<bool>? transport = optimized ? () => { guards++; return true; } : null;
            var result = session.ObservePowerInstalled(held!.CreateGetterPowerObservationApproval(() => true, transport),
                fake.ReadUsb, new CompleteMetadata());
            Assert.True(result.Batch.Complete); Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
            Assert.Single(result.Batch.CopySamples());
            return (fake, guards);
        }
        var legacy = Run(false); var scoped = Run(true);
        Assert.Equal(legacy.Platform.Commands, scoped.Platform.Commands);
        Assert.Equal(legacy.Platform.Codes, scoped.Platform.Codes);
        Assert.True(scoped.Platform.FullBackendReads * 2 < legacy.Platform.FullBackendReads);
        Assert.True(scoped.Guards >= 2 * scoped.Platform.Codes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyTransportOrFullCommandEndRejectionCannotAdmitPmSnapshot(bool rejectTransport)
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        bool current = true;
        var approval = held!.CreateGetterPowerObservationApproval(() => rejectTransport || current,
            () => !rejectTransport || current);
        fake.AfterCopy = _ => current = false;
        var result = session.ObservePowerInstalled(approval, fake.ReadUsb, new CompleteMetadata());
        Assert.False(result.Batch.Complete); Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
        Assert.Equal(1, fake.Copies); Assert.Equal(0x81112F08u, fake.Codes.Last());
        current = true;
        Assert.False(approval.IsCurrent()); Assert.False(approval.TransportCurrent());
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public void InstalledMetadataBoundaryUsesOneFreshScopedBackendSnapshot(bool scoped, int readsPerBoundary)
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        int boundaries = 0;
        var metadata = new CompleteMetadata
        {
            Checkpoint = checkpoint =>
            {
                int before = fake.FullBackendReads;
                checkpoint();
                Assert.Equal(readsPerBoundary, fake.FullBackendReads - before);
                boundaries++;
            }
        };
        var approval = scoped ? held!.CreateGetterPowerObservationApproval(() => true)
            : held!.CreatePowerObservationApproval(() => true);
        var result = session.ObservePowerInstalled(approval, fake.ReadUsb, metadata);
        Assert.True(result.Batch.Complete); Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
        Assert.Equal(2, boundaries); Assert.Single(result.Batch.CopySamples());
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("exception")]
    [InlineData("dispose")]
    [InlineData("cancellation")]
    public void ScopedBackendSnapshotCannotAuthorizeTheNextBoundaryAfterDrift(string drift)
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        using var cancellation = new CancellationTokenSource();
        int boundaries = 0;
        var metadata = new CompleteMetadata
        {
            Checkpoint = checkpoint =>
            {
                checkpoint(); boundaries++;
                switch (drift)
                {
                    case "registration": fake.Failure = "start"; break;
                    case "exception": fake.ThrowAt = "backend"; break;
                    case "dispose": held!.Dispose(); break;
                    case "cancellation": cancellation.Cancel(); break;
                }
            }
        };
        var result = session.ObservePowerInstalled(held!.CreateGetterPowerObservationApproval(() => true),
            fake.ReadUsb, metadata, cancellation.Token);
        Assert.False(result.Batch.Complete); Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
        Assert.Equal(1, boundaries);
        if (drift != "cancellation")
        {
            fake.Failure = null; fake.ThrowAt = null;
            Assert.False(held.GetterContinuityCurrent); Assert.False(held.IsCurrent);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledBackendContinuityRejectionRetainsItsOwnCause(bool beforeEngine)
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        int checkpoints = 0;
        var approval = held!.CreateGetterPowerObservationApproval(() =>
        {
            if (beforeEngine && ++checkpoints == 2) { fake.Failure = "service"; }
            return true;
        });
        fake.AfterCopy = value => value.Failure = "service";
        var result = session.ObservePowerInstalled(approval, fake.ReadUsb, new CompleteMetadata());
        Assert.False(result.Batch.Complete); Assert.True(result.CleanupComplete);
        Assert.Equal(CpuPmObservationFailure.AdmissionDenied, result.Batch.Diagnostic!.Failure);
        Assert.Equal(CpuPmAdmissionFailure.InstalledBackendContinuity, result.Batch.Diagnostic.AdmissionFailure);
        if (beforeEngine) { Assert.Empty(fake.Codes); Assert.Equal(0, fake.Copies); }
        else { Assert.Equal(0x81112F08u, fake.Codes.Last()); Assert.Equal(1, fake.Copies); }
    }

    [Fact]
    public void NativeRefreshFailureSurvivesAdapterCleanupFailureWithoutClaimingCleanup()
    {
        var fake = new Fake { RefreshFails = true, ThrowAt = "close" };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        var approval = CpuPmAttendedApproval.Acknowledge(true, true, fake.Lifetime, CpuPmObservationProfile.RangeSingleSample);
        var result = session.ObservePower(approval, () => fake.FreshLifetime, new CompleteMetadata());
        Assert.False(result.Batch.Complete); Assert.False(result.CleanupComplete); Assert.True(result.WorkerTerminal);
        Assert.Equal(CpuPmObservationStage.Refresh, result.Batch.Diagnostic!.Stage);
        Assert.Equal(CpuQueryFailureCode.NativeIoctlFailed, result.Batch.Diagnostic.NativeFailure);
        Assert.Equal(5, result.Batch.Diagnostic.NativeErrorCode); Assert.Equal(0x81112FF8u, result.Batch.Diagnostic.Operation);
        Assert.Equal(0, fake.Copies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopedInstalledGetterBoundsDiscoveryAndKeepsMutableRevocation(bool revoke)
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        Assert.NotNull(held);
        for (int run = 0; run < 2; run++)
        {
            int hosts = fake.HostReads, controllers = fake.ControllerReads, checkpoints = 0;
            bool current = true;
            var approval = held.CreateGetterPowerObservationApproval(() => { checkpoints++; return current; });
            fake.AfterCopy = _ => { if (revoke) { current = false; } };
            var result = session.ObservePowerInstalled(approval, fake.ReadUsb, new CompleteMetadata());
            Assert.Equal(!revoke, result.Batch.Complete);
            Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
            Assert.InRange(fake.HostReads - hosts, 1, 6);
            Assert.InRange(fake.ControllerReads - controllers, 1, 6);
            Assert.True(checkpoints > 77);
            if (revoke)
            {
                Assert.Equal(CpuPmObservationFailure.AdmissionDenied, result.Batch.Diagnostic!.Failure);
                Assert.Equal(CpuPmAdmissionFailure.InstalledRequestAdmission, result.Batch.Diagnostic.AdmissionFailure);
                current = true; Assert.False(approval.IsCurrent());
                Assert.True(held.IsCurrent);
                break;
            }
        }
        Assert.All(fake.Commands, command => Assert.Contains(command, new uint[] { 2, 0x82, 6, 0x65 }));
        Assert.DoesNotContain("continuity-arm", fake.Events);
    }

    private static CpuPmAttendedApproval Approval(Fake fake) => CpuPmAttendedApproval.Acknowledge(true, true, fake.Lifetime);
    private static CpuPmObservationBatch Run(Fake fake, CpuPmAttendedApproval? approval = null) =>
        WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(approval ?? Approval(fake), () => fake.FreshLifetime, fake, fake);

    [Fact]
    public void InstalledObservationDuplicatesRetainedObjectWithoutOpeningOrArmingAndCannotReplay()
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        Assert.NotNull(held); fake.Events.Clear();
        var approval = held.CreatePowerObservationApproval(() => true);
        var result = session.ObservePowerInstalled(approval, fake.ReadUsb, new CompleteMetadata());
        Assert.True(result.Batch.Complete); Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
        Assert.Equal(0x0B5D1600u, result.Batch.FirmwareVersion);
        Assert.Equal(new uint[] { 2, 0x82, 6, 0x65, 6 }, fake.Commands);
        Assert.Single(result.Batch.CopySamples()); Assert.True(held.IsCurrent);
        Assert.Equal(1, fake.Events.Count(value => value == "duplicate"));
        Assert.DoesNotContain("open", fake.Events); Assert.DoesNotContain("pin", fake.Events);
        Assert.DoesNotContain("unpin", fake.Events); Assert.DoesNotContain("continuity-arm", fake.Events);
        Assert.DoesNotContain("continuity-dispose", fake.Events);
        Assert.Equal(0, fake.ServiceReads); // Use the existing retained-backend inspector.
        int calls = fake.Codes.Count;
        Assert.False(session.ObservePowerInstalled(approval, fake.ReadUsb, new CompleteMetadata()).Batch.Complete);
        Assert.Equal(calls, fake.Codes.Count);
        Assert.True(session.ObservePowerInstalled(held.CreatePowerObservationApproval(() => true), fake.ReadUsb, new CompleteMetadata()).Batch.Complete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledRequestRevocationIsStickyButDoesNotRevokeIndependentLease(bool throws)
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        bool current = true;
        var approval = held!.CreatePowerObservationApproval(() => current ? true : throws ? throw new IOException() : false);
        fake.AfterCopy = _ => current = false;
        var result = session.ObservePowerInstalled(approval, fake.ReadUsb, new CompleteMetadata());
        Assert.False(result.Batch.Complete); Assert.True(result.CleanupComplete);
        current = true; Assert.False(approval.IsCurrent()); Assert.True(held.IsCurrent);
        fake.AfterCopy = null;
        Assert.True(session.ObservePowerInstalled(held.CreatePowerObservationApproval(() => true), fake.ReadUsb, new CompleteMetadata()).Batch.Complete);
    }

    [Fact]
    public void InstalledObservationRejectsForeignLeaseAndExpiredRequestBeforeTraffic()
    {
        var fake = new Fake { Installed = true }; var foreign = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        var other = WindowsRz090528CpuNativeSession.ForPlatform(foreign);
        Assert.False(other.ObservePowerInstalled(held!.CreatePowerObservationApproval(() => true), foreign.ReadUsb, new CompleteMetadata()).Batch.Complete);
        Assert.Empty(foreign.Events);
        fake.Events.Clear();
        Assert.False(session.ObservePowerInstalled(held.CreatePowerObservationApproval(() => false), fake.ReadUsb, new CompleteMetadata()).Batch.Complete);
        Assert.Empty(fake.Codes); Assert.DoesNotContain("duplicate", fake.Events);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("start")]
    [InlineData("controller")]
    [InlineData("hash")]
    public void InstalledBackendDriftRevokesHeldLeaseAcrossRequests(string drift)
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        fake.AfterCopy = value => value.Failure = drift;
        Assert.False(session.ObservePowerInstalled(held!.CreatePowerObservationApproval(() => true), fake.ReadUsb, new CompleteMetadata()).Batch.Complete);
        fake.Failure = null; fake.AfterCopy = null;
        Assert.False(held.IsCurrent);
        int calls = fake.Codes.Count;
        Assert.False(session.ObservePowerInstalled(held.CreatePowerObservationApproval(() => true), fake.ReadUsb, new CompleteMetadata()).Batch.Complete);
        Assert.Equal(calls, fake.Codes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledCleanupAmbiguityRetainsAnchorAndSharedLatch(bool metadataFailure)
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        fake.Events.Clear(); if (!metadataFailure) { fake.ThrowAt = "close"; }
        var result = session.ObservePowerInstalled(held!.CreatePowerObservationApproval(() => true), fake.ReadUsb,
            new CompleteMetadata { CleanupFails = metadataFailure });
        Assert.False(result.Batch.Complete); Assert.False(result.CleanupComplete); Assert.True(result.WorkerTerminal);
        held.Dispose(); Assert.True(fake.Pinned); Assert.DoesNotContain("unpin", fake.Events);
        Assert.Equal(CpuLiveQueryStatus.Busy, session.Query(CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), fake.ReadUsb).Status);
    }

    [Fact]
    public void InstalledWrongFirmwareStopsBeforeCopyAndMetadata()
    {
        var fake = new Fake { Installed = true, Firmware = 0x005D000B };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        var result = session.ObservePowerInstalled(held!.CreatePowerObservationApproval(() => true), fake.ReadUsb, new CompleteMetadata());
        Assert.False(result.Batch.Complete); Assert.Equal(new uint[] { 2 }, fake.Commands); Assert.Equal(0, fake.Copies);
        Assert.Null(result.Batch.FirmwareVersion);
    }

    [Fact]
    public void InstalledLeaseDisposalDuringWorkerKeepsPinsUntilSameWorkerReturns()
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        fake.Events.Clear();
        fake.AfterCopy = _ =>
        {
            held!.Dispose(); Assert.True(fake.Pinned); Assert.DoesNotContain("close", fake.Events);
            Assert.Equal(CpuLiveQueryStatus.Busy, session.Query(CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), fake.ReadUsb).Status);
        };
        var result = session.ObservePowerInstalled(held!.CreatePowerObservationApproval(() => true), fake.ReadUsb, new CompleteMetadata());
        Assert.False(result.Batch.Complete); Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
        Assert.False(fake.Pinned); Assert.Equal(2, fake.Events.Count(value => value == "close"));
        Assert.Equal(1, fake.Events.Count(value => value == "unpin"));
    }

    [Fact]
    public void InstalledUsbIsRecheckedAtEveryNativeBoundary()
    {
        var fake = new Fake { Installed = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        using var held = session.TryRetainInstalledBackend(fake.ReadUsb);
        // Flip after the first firmware query submission, before its reply and
        // well before the later snapshot boundary or kernel-copy operation.
        DeviceIdentity? ReadUsb() => fake.Commands.Count == 0 ? fake.ReadUsb() : null;
        var result = session.ObservePowerInstalled(held!.CreatePowerObservationApproval(() => true), ReadUsb, new CompleteMetadata());
        Assert.False(result.Batch.Complete); Assert.Equal(new uint[] { 2 }, fake.Commands);
        Assert.Equal(0, fake.Copies); Assert.Null(result.Batch.FirmwareVersion);
        Assert.True(result.CleanupComplete); Assert.True(held.IsCurrent);
    }

    private sealed class CompleteMetadata : IRz090528CpuLivePowerRangeMetadataReader
    {
        internal bool CleanupFails;
        internal Action<Action>? Checkpoint;
        private CpuLivePowerRangeMetadataCall<T> Call<T>(T value, Action checkpoint) where T : class
        {
            if (Checkpoint is { } check) { check(checkpoint); }
            else { checkpoint(); }
            return new(value, CpuLivePowerRangeMetadataStatus.Complete,
                CpuLivePowerRangeMetadataStages.InvocationEntered | CpuLivePowerRangeMetadataStages.InvocationReturned
                    | CpuLivePowerRangeMetadataStages.OutputEnumerated | CpuLivePowerRangeMetadataStages.PropertiesRead
                    | (CleanupFails ? 0 : CpuLivePowerRangeMetadataStages.Disposed), DateTimeOffset.UtcNow, 1);
        }
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion> QueryVersion(Action checkpoint) => Call(new CpuLivePowerRangeVersion(7, false), checkpoint);
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues> GetDValues(Action checkpoint) => throw new InvalidOperationException();
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax> GetMinMaxSteppingValues(Action checkpoint) =>
            Call(new CpuLivePowerRangeMinMax(0x50001, 0, 0, 125000, 1), checkpoint);
    }

    [Fact]
    public void BothDistinctApprovalsAndFreshLifetimeAreRequiredBeforePlatformAccess()
    {
        var fake = new Fake();
        Assert.False(WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(null, () => fake.Lifetime, fake, fake).Complete);
        Assert.Empty(fake.Events);
        Assert.Throws<ArgumentException>(() => CpuPmAttendedApproval.Acknowledge(false, true, fake.Lifetime));
        Assert.Throws<ArgumentException>(() => CpuPmAttendedApproval.Acknowledge(true, false, fake.Lifetime));
        Assert.Throws<ArgumentException>(() => CpuPmAttendedApproval.Acknowledge(true, true, fake.Lifetime with { ConfirmedFreshRunOwned = false }));
        fake.FreshLifetime = fake.Lifetime with { DriverLifetimeId = Guid.NewGuid() };
        Assert.False(Run(fake).Complete); Assert.Empty(fake.Events);
        Assert.False(WindowsRz090528CpuPmObservationAdapter.ObserveProduction(true).Complete);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, WindowsRz090528CpuQueryAdapter.QueryProduction(true).Status);
    }

    [Fact]
    public void SingleUseApprovalCannotBeReplayedEvenAfterSuccessfulCleanup()
    {
        var fake = new Fake(); var approval = Approval(fake);
        Assert.True(Run(fake, approval).Complete); int events = fake.Events.Count;
        Assert.False(Run(fake, approval).Complete); Assert.Equal(events, fake.Events.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RangeFirmwareEvidenceSurvivesIncompleteMetadataAndCleanup(bool cleanupFails)
    {
        var fake = new Fake { CleanupFailure = cleanupFails };
        var approval = CpuPmAttendedApproval.Acknowledge(true, true, fake.Lifetime, CpuPmObservationProfile.RangeSingleSample);
        var result = WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(
            approval, () => fake.FreshLifetime, fake, fake, metadataReader: new RejectedMetadata());
        Assert.False(result.Complete);
        Assert.Equal(new uint[] { 2, 0x82, 6, 0x65, 6 }, fake.Commands);
        Assert.Equal(0x0B5D1600u, result.FirmwareVersion);
        Assert.Single(result.CopySamples());
        Assert.Equal(cleanupFails ? CpuPmObservationStage.Cleanup : CpuPmObservationStage.MetadataVersion, result.Diagnostic!.Stage);
    }

    private sealed class RejectedMetadata : IRz090528CpuLivePowerRangeMetadataReader
    {
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion> QueryVersion(Action checkpoint)
        {
            checkpoint();
            return new(null, CpuLivePowerRangeMetadataStatus.InvalidData, CpuLivePowerRangeMetadataStages.Disposed, default, 0);
        }
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues> GetDValues(Action checkpoint) => throw new InvalidOperationException();
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax> GetMinMaxSteppingValues(Action checkpoint) => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedContinuityKeepsOwnerRegistrationAndSameCleanupLatch(bool cleanupFails)
    {
        var fake = new Fake();
        var borrowed = fake.ArmContinuity(fake.Lifetime);
        fake.Events.Clear();
        if (cleanupFails) { fake.ThrowAt = "close"; }
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        var approval = CpuPmAttendedApproval.Acknowledge(true, true, fake.Lifetime, CpuPmObservationProfile.RangeSingleSample);
        var result = session.ObservePower(approval, () => fake.FreshLifetime, new RejectedMetadata(), borrowed);
        Assert.False(result.Batch.Complete); Assert.True(result.WorkerTerminal);
        Assert.Equal(!cleanupFails, result.CleanupComplete);
        Assert.Equal(0x0B5D1600u, result.Batch.FirmwareVersion);
        Assert.DoesNotContain("continuity-arm", fake.Events); Assert.DoesNotContain("continuity-dispose", fake.Events);
        Assert.Equal(CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime, borrowed.ReadFresh(fake.Lifetime).Continuity);
        if (cleanupFails)
        {
            Assert.Equal(CpuLiveQueryStatus.Busy, session.Query(CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), fake.ReadUsb).Status);
        }
        borrowed.Dispose();
        Assert.Equal(1, fake.Events.Count(value => value == "continuity-dispose"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedScopeRequiresFreshParentAndContinuityBeforeAnyTraffic(bool parentRevoked)
    {
        var fake = new Fake(); var borrowed = fake.ArmContinuity(fake.Lifetime);
        fake.Events.Clear(); fake.ContinuityFailure = !parentRevoked;
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        var approval = CpuPmAttendedApproval.Acknowledge(true, true, fake.Lifetime, CpuPmObservationProfile.RangeSingleSample);
        var result = session.ObservePower(approval, () => parentRevoked ? null : fake.FreshLifetime, new RejectedMetadata(), borrowed);
        Assert.False(result.Batch.Complete); Assert.Empty(fake.Commands); Assert.Equal(0, fake.Copies);
        Assert.DoesNotContain("continuity-arm", fake.Events); Assert.DoesNotContain("continuity-dispose", fake.Events);
    }

    [Theory]
    [InlineData("2.02")]
    [InlineData("2.03")]
    [InlineData("Unknown")]
    public void ThreeSamplesReuseActualThreadAffineLeaseAndTwoIndependentPmWireGuards(string bios)
    {
        var fake = new Fake { Bios = bios }; var result = Run(fake);
        Assert.True(result.Complete); Assert.Null(result.Diagnostic); Assert.Equal(3, result.CopySamples().Length);
        Assert.Equal(new uint[] { 6, 0x65, 6, 6, 0x65, 6, 6, 0x65, 6 }, fake.Commands);
        Assert.Equal(3, fake.Copies); Assert.Equal(2, fake.IdleDelays);
        Assert.Equal(9, fake.FullBackendReads); // preliminary, pinned pre/post-open, six sample checkpoints
        Assert.True(fake.ServiceReads > fake.FullBackendReads);
        Assert.Equal("continuity-arm", fake.Events[0]);
        Assert.True(fake.Events.IndexOf("pin") < fake.Events.IndexOf("open"));
        Assert.Equal(new[] { "close", "unpin", "release", "mutex-dispose", "continuity-dispose" }, fake.Events.TakeLast(5));
        Assert.All(fake.Threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
        Assert.False(fake.Pinned); Assert.False(fake.Locked);
    }

    [Theory]
    [InlineData("manufacturer")]
    [InlineData("sku")]
    [InlineData("package")]
    [InlineData("leaf")]
    [InlineData("stepping")]
    [InlineData("usb")]
    [InlineData("hash")]
    [InlineData("signature")]
    [InlineData("controller")]
    [InlineData("start")]
    [InlineData("service")]
    public void ExactPreflightFailureNeverCreatesMutexOrOpensDevice(string failure)
    {
        var fake = new Fake { Failure = failure };
        Assert.False(Run(fake).Complete);
        Assert.DoesNotContain("mutex-create", fake.Events); Assert.DoesNotContain("open", fake.Events);
        Assert.Empty(fake.Codes); Assert.Contains("continuity-dispose", fake.Events);
    }

    [Fact]
    public void UnknownContinuityCannotReachNativeMutex()
    {
        var fake = new Fake { ContinuityFailure = true };
        Assert.False(Run(fake).Complete); Assert.DoesNotContain("mutex-create", fake.Events);
    }

    [Fact]
    public void DriftAcrossDeviceOpenClosesWithoutAnIoctl()
    {
        var fake = new Fake { AfterOpen = f => f.Failure = "manufacturer" };
        Assert.False(Run(fake).Complete); Assert.Empty(fake.Codes);
        Assert.Equal(new[] { "close", "unpin", "release", "mutex-dispose", "continuity-dispose" }, fake.Events.TakeLast(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyOrAbandonedMutexNeverOpensDeviceAndOnlyAcquiredOwnershipReleases(bool abandoned)
    {
        var fake = new Fake { Wait = abandoned ? CpuQueryMutexWait.Abandoned : CpuQueryMutexWait.Timeout };
        Assert.False(Run(fake).Complete); Assert.DoesNotContain("pin", fake.Events); Assert.DoesNotContain("open", fake.Events);
        Assert.Equal(abandoned ? 1 : 0, fake.Events.Count(e => e == "release"));
        Assert.Contains("mutex-dispose", fake.Events);
    }

    [Theory]
    [InlineData("continuity")]
    [InlineData("lifetime")]
    [InlineData("service")]
    [InlineData("controller")]
    public void CheapDynamicDriftAfterCopyPreventsAnyFollowingIoctl(string failure)
    {
        var fake = new Fake
        {
            AfterCopy = f =>
        {
            if (failure == "continuity") { f.ContinuityFailure = true; }
            else if (failure == "lifetime") { f.FreshLifetime = f.Lifetime with { DriverLifetimeId = Guid.NewGuid() }; }
            else { f.Failure = failure; }
        }
        };
        var result = Run(fake); Assert.False(result.Complete); Assert.Empty(result.CopySamples());
        Assert.Equal(new uint[] { 6, 0x65 }, fake.Commands); Assert.Equal(1, fake.Copies);
        Assert.Equal(0x81112F08u, fake.Codes.Last());
        if (failure is "continuity" or "lifetime")
        {
            Assert.Equal(CpuPmObservationFailure.AdmissionDenied, result.Diagnostic!.Failure);
            Assert.Equal(CpuPmAdmissionFailure.OwnedLifetimeContinuity, result.Diagnostic.AdmissionFailure);
        }
    }

    [Fact]
    public void MetadataIsRecheckedAtSampleBoundaryAndNeverSubstitutedForFreshServiceState()
    {
        var fake = new Fake { AfterCopy = f => f.Failure = "hash" };
        var result = Run(fake); Assert.False(result.Complete);
        Assert.Single(result.CopySamples()); Assert.Equal(1, fake.Copies);
        Assert.Equal(new uint[] { 6, 0x65, 6 }, fake.Commands);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("copy")]
    [InlineData("close")]
    [InlineData("unpin")]
    public void NativeAndCleanupExceptionsStillReleaseAllRemainingLeaseResources(string failure)
    {
        var fake = new Fake { ThrowAt = failure };
        var result = Run(fake); Assert.False(result.Complete);
        Assert.Contains("unpin", fake.Events); Assert.Contains("release", fake.Events);
        Assert.Contains("mutex-dispose", fake.Events); Assert.Contains("continuity-dispose", fake.Events);
    }

    [Fact]
    public void ContinuityCleanupFailureRetainsCompletedSamplesButCannotClaimComplete()
    {
        var fake = new Fake { CleanupFailure = true }; var result = Run(fake);
        Assert.False(result.Complete); Assert.Equal(3, result.CopySamples().Length);
        Assert.Equal(CpuPmObservationFailure.CleanupFailed, result.Diagnostic!.Failure);
        Assert.Equal(95u, result.FusedCap!.Candidate!.Watts);
        Assert.Equal(5u, result.FusedCap.A0F!.RawValue);
        Assert.Equal(0xF0000000u, result.FusedCap.A0B!.RawValue);
    }

    [Fact]
    public void CancellationBeforeArmHasNoPlatformEffects()
    {
        var fake = new Fake(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(
            Approval(fake), () => fake.Lifetime, fake, fake, canceled.Token)); Assert.Empty(fake.Events);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 0u, 10)]
    [InlineData(5u, 0xFFFFFFFFu, 12)]
    [InlineData(0u, 0u, 12)]
    public void InnerNativeGuardRejectionStillPreservesExactCapReplyWithoutRetry(uint first, uint second, int frames)
    {
        var fake = new Fake { CapFirst = first, CapSecond = second };
        var result = Run(fake);
        Assert.False(result.Complete); Assert.Equal(CpuPmObservationStage.FusedCap, result.Diagnostic!.Stage);
        Assert.Equal(frames, fake.Codes.Count); Assert.Empty(fake.Commands); Assert.Empty(result.CopySamples());
        Assert.Equal(first, result.FusedCap!.A0F!.RawValue);
        var rejected = frames == 10 ? result.FusedCap.A0F : result.FusedCap.A0B!;
        Assert.True(rejected.TransportSucceeded); Assert.False(rejected.PostReadAdmissionPassed);
        Assert.Null(result.FusedCap.Candidate);
        if (frames == 12) { Assert.Equal(second, rejected.RawValue); Assert.True(result.FusedCap.A0F.PostReadAdmissionPassed); }
        Assert.Contains("release", fake.Events); Assert.Contains("unpin", fake.Events);
    }

    [Fact]
    public async Task BlockedContinuityArmRetainsActualWorkerAndExcludesEveryMailboxPath()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var fake = new Fake { DuringArm = _ => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); } };
        Task<CpuPmObservationBatch> worker = Task.Run(() => Run(fake));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAsync<TimeoutException>(async () => await worker.WaitAsync(TimeSpan.FromMilliseconds(1)));
            Assert.False(worker.IsCompleted);
            var queryApproval = CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true);
            var pmApproval = Approval(fake);
            var request = CurveRequest();
            var proof = new Proof(request);
            var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
            Assert.Equal(CpuLiveQueryStatus.Busy, session.Query(queryApproval, fake.ReadUsb).Status);
            Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, session.ExecuteCurve(request, proof, fake.ReadUsb).Disposition);
            Assert.Equal(CpuPmObservationFailure.Busy, Run(fake, pmApproval).Diagnostic!.Failure);
            Assert.Equal(new[] { "continuity-arm" }, fake.Events);
            Assert.False(queryApproval.Consume());
            Assert.False(pmApproval.Consume());
            Assert.True(proof.Consume(request)); // Busy never consumes mutation authority.
            Assert.False(proof.Consume(request));
        }
        finally { finish.Set(); }
        Assert.True((await worker).Complete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveQueryAndCurveExcludePmBeforeNotificationRegistration(bool curve)
    {
        var fake = new Fake();
        fake.AfterOpen = f =>
        {
            var result = Run(f);
            Assert.False(result.Complete);
            Assert.Equal(CpuPmObservationFailure.Busy, result.Diagnostic!.Failure);
            Assert.DoesNotContain("continuity-arm", f.Events);
        };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        if (curve)
        {
            var request = CurveRequest();
            Assert.True(session.ExecuteCurve(request, new Proof(request), fake.ReadUsb).CleanupComplete);
        }
        else
        {
            Assert.Equal(CpuLiveQueryStatus.Observed,
                session.Query(CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), fake.ReadUsb).Status);
        }
        Assert.Single(fake.Events, value => value == "open");
        Assert.Contains("release", fake.Events);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("unpin")]
    [InlineData("unpin-cancel")]
    [InlineData("acquire-cleanup-cancel")]
    [InlineData("continuity-dispose")]
    [InlineData("continuity-dispose-cancel")]
    [InlineData("continuity-read-cancel")]
    [InlineData("continuity-unconfirmed")]
    [InlineData("arm")]
    [InlineData("arm-cancel")]
    public void PmUncertainCleanupParksAllPathsEvenWhenCancellationMasksTheFailure(string fault)
    {
        var fake = new Fake { ThrowAt = fault };
        if (fault == "acquire-cleanup-cancel") { fake.AfterOpen = f => f.Failure = "manufacturer"; }
        var result = Run(fake);
        Assert.False(result.Complete);
        Assert.Equal(CpuPmObservationStage.Cleanup, result.Diagnostic!.Stage);
        Assert.Equal(CpuPmObservationFailure.CleanupFailed, result.Diagnostic.Failure);
        AssertAllPathsParked(fake);
    }

    [Theory]
    [InlineData("arm-unknown")]
    [InlineData("host-unknown")]
    [InlineData("continuity-dispose-unknown")]
    public void UnboundedExceptionsAlsoRetainTheSharedGate(string fault)
    {
        var fake = new Fake { ThrowAt = fault };
        Assert.Throws<NotSupportedException>(() => Run(fake));
        AssertAllPathsParked(fake);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueryOrCurveCleanupFailurePreventsPmArming(bool curve)
    {
        var fake = new Fake { ThrowAt = "unpin-cancel" };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        if (curve)
        {
            fake.Failure = "sku"; // Cleanup fails before Acquire can return a lease.
            var request = CurveRequest();
            Assert.False(session.ExecuteCurve(request, new Proof(request), fake.ReadUsb).CleanupComplete);
        }
        else
        {
            Assert.Throws<OperationCanceledException>(() =>
                session.Query(CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), fake.ReadUsb));
        }
        Assert.DoesNotContain("continuity-arm", fake.Events);
        AssertAllPathsParked(fake);
    }

    [Fact]
    public void ConfirmedCleanupReleasesPmGateAfterOrdinaryDenialAndAfterSuccess()
    {
        var fake = new Fake { Failure = "sku" };
        Assert.False(Run(fake).Complete);
        fake.Failure = null;
        Assert.True(Run(fake).Complete);
        Assert.True(Run(fake).Complete);
        Assert.Equal(3, fake.Events.Count(value => value == "continuity-arm"));
    }

    [Fact]
    public void GateRemainsHeldWhileNotificationsAreBeingDisposedAfterNativeLeaseRelease()
    {
        var fake = new Fake();
        fake.DuringContinuityDispose = f =>
        {
            Assert.False(f.Pinned); Assert.False(f.Locked);
            AssertAllPathsParked(f);
        };
        Assert.True(Run(fake).Complete);
        Assert.Single(fake.Events, value => value == "continuity-arm");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RefreshReplyRejectionRetainsFirstWireReasonEvenWhenCleanupFails(bool corruptEcho, bool cleanupFails)
    {
        var fake = new Fake { RefreshResponse = 2, CorruptRefreshEcho = corruptEcho, CleanupFailure = cleanupFails };
        var result = Run(fake);
        Assert.False(result.Complete);
        Assert.Equal(CpuPmObservationStage.Refresh, result.Diagnostic!.Stage);
        Assert.Equal(CpuQueryFailureCode.WireReplyRejected, result.Diagnostic.NativeFailure);
        Assert.Equal(0x81112F24u, result.Diagnostic.Operation);
        Assert.Equal(corruptEcho ? CpuPmWireRejection.PciReadEchoMismatch : CpuPmWireRejection.MailboxResponseNotSuccessful,
            result.Diagnostic.WireRejection);
        Assert.Equal(corruptEcho ? (uint?)null : 2u, result.Diagnostic.MailboxStatus);
        Assert.Equal(new uint[] { 6, 0x65 }, fake.Commands);
        Assert.Equal(0, fake.Copies); Assert.Empty(result.CopySamples());
        Assert.Equal(0x81112F24u, fake.Codes[^1]);
        Assert.Equal(95u, result.FusedCap!.Candidate!.Watts);
        Assert.True(result.FusedCap.A0F!.PostReadAdmissionPassed); Assert.True(result.FusedCap.A0B!.PostReadAdmissionPassed);
        if (cleanupFails) { AssertAllPathsParked(fake); }
    }

    private static void AssertAllPathsParked(Fake fake)
    {
        fake.ThrowAt = null; fake.Failure = null; fake.AfterOpen = null;
        int events = fake.Events.Count, calls = fake.Codes.Count;
        var session = WindowsRz090528CpuNativeSession.ForPlatform(fake);
        Assert.Equal(CpuLiveQueryStatus.Busy,
            session.Query(CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), fake.ReadUsb).Status);
        var request = CurveRequest();
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted,
            session.ExecuteCurve(request, new Proof(request), fake.ReadUsb).Disposition);
        Assert.Equal(CpuPmObservationFailure.Busy, Run(fake).Diagnostic!.Failure);
        Assert.Equal(events, fake.Events.Count);
        Assert.Equal(calls, fake.Codes.Count);
    }

    private static CpuLiveCurveNativeRequest CurveRequest()
    {
        var snapshot = CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50,
            Enumerable.Range(0, 16).Where(CpuLiveCurveSnapshot.IsEnabled).Select(id => new CpuLiveCoreOffset(id, -1)).ToArray()))!;
        Assert.True(CpuLiveCurveCommand.TryCreate(0, 0, 0xFF55, out var command));
        return new(new(), command, CpuLiveCurveCommandPurpose.Forward, snapshot);
    }

    private sealed class Proof(CpuLiveCurveNativeRequest request) : CpuLiveCurveWriteProof(request)
    {
        internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest) => true;
        internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest) => new(0x5000A, 0, 50, 1, true);
    }

    private sealed class Fake : ICpuPmWindowsPlatform, ICpuPmObservationClock
    {
        internal CpuPmRunOwnedDriverLifetime Lifetime = new(Guid.NewGuid(), Guid.NewGuid(), true);
        internal CpuPmRunOwnedDriverLifetime? FreshLifetime;
        internal string? Failure, ThrowAt;
        internal string Bios = "2.02";
        internal bool Pinned, Locked, ContinuityFailure, CleanupFailure, Installed;
        internal uint Firmware = 0x0B5D1600;
        internal CpuQueryMutexWait Wait = CpuQueryMutexWait.Acquired;
        internal int FullBackendReads, ServiceReads, Copies, IdleDelays, HostReads, ControllerReads;
        internal uint CapFirst = 5, CapSecond = 0xF0000000;
        internal Action<Fake>? AfterOpen, AfterCopy, DuringArm, DuringContinuityDispose;
        internal Action<uint, uint>? AfterRegisterRead;
        internal bool RefreshFails, CorruptRefreshEcho;
        internal uint RefreshResponse = 1;
        internal readonly List<string> Events = [];
        internal readonly List<uint> Codes = [], Commands = [];
        internal readonly List<int> Threads = [];
        private TimeSpan now;
        private uint selected, command;
        internal Fake() => FreshLifetime = Lifetime;
        private void Event(string value) { Events.Add(value); Threads.Add(Environment.CurrentManagedThreadId); }
        public HostIdentityReadResult ReadHost()
        {
            HostReads++;
            return ThrowAt == "host-unknown" ? throw new NotSupportedException()
                : new(new(Failure == "manufacturer" ? "other" : "Razer", "Blade 16 - RZ09-0528", Failure == "sku" ? "unknown" : "RZ09-05289EN4", Bios), null);
        }
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x1A, 0x24, Failure == "stepping" ? 1 : 0);
        public CpuPmProcessorPackage ReadPackage() => new(Failure == "leaf" ? 0x80000000 : 0x80000001, Failure == "package" ? 0 : 1);
        public DeviceIdentity ReadUsb() => new("RZ09-0528", 0x1532, Failure == "usb" ? (ushort)0x02E0 : (ushort)0x02C6, "2.02");
        public bool ControllersAbsent() { ControllerReads++; return Failure != "controller"; }
        public InstalledCpuBackendSnapshot ReadBackend()
        {
            FullBackendReads++;
            if (ThrowAt == "backend") { throw new IOException(); }
            if (FullBackendReads > 1) { Assert.True(Pinned); if (!Installed) { Assert.True(Locked); } }
            var files = Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair => new CpuBackendFileMetadata(pair.Key,
                Failure == "hash" ? "bad" : pair.Value, Failure != "signature", Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray();
            var service = Service(); return new(files, service.Exists, service.ImagePath, service.Type, service.Start, service.Running);
        }
        private CpuPmServiceSnapshot Service() => new(true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, Failure == "start" ? 2 : 3, Failure != "service");
        public CpuPmServiceSnapshot ReadService() { ServiceReads++; return Service(); }
        public IDisposable PinBackendFiles()
        {
            Event("pin"); Assert.True(Locked); Pinned = true;
            return new Release(() =>
            {
                Event("unpin"); Pinned = false;
                if (ThrowAt == "unpin") { throw new IOException(); }
                if (ThrowAt is "unpin-cancel" or "acquire-cleanup-cancel") { throw new OperationCanceledException(); }
            });
        }
        public ICpuQueryMutex CreateSharedMutex() { Event("mutex-create"); return new Mutex(this); }
        public ICpuQueryDevice OpenExistingDevice()
        {
            Event("open"); Assert.True(Pinned); Assert.True(Locked);
            if (ThrowAt == "open") { throw new IOException(); }
            AfterOpen?.Invoke(this); return new Device(this);
        }
        public ICpuPmContinuityScope ArmContinuity(CpuPmRunOwnedDriverLifetime lifetime)
        {
            Event("continuity-arm"); Assert.Equal(Lifetime, lifetime); FullBackendReads = 0;
            DuringArm?.Invoke(this);
            if (ThrowAt == "arm") { throw new IOException(); }
            if (ThrowAt == "arm-cancel") { throw new OperationCanceledException(); }
            if (ThrowAt == "arm-unknown") { throw new NotSupportedException(); }
            return new Continuity(this);
        }
        private sealed class Continuity(Fake fake) : ICpuPmContinuityScope
        {
            private readonly Guid session = Guid.NewGuid(); private bool disposed;
            public CpuPmContinuityEvidence ReadFresh(CpuPmRunOwnedDriverLifetime? lifetime) =>
                disposed && fake.ThrowAt == "continuity-read-cancel" ? throw new OperationCanceledException() : new(
                disposed || fake.ContinuityFailure ? CpuPmSessionContinuity.Interrupted : CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime,
                session, fake.Lifetime.DriverLifetimeId, disposed ? fake.CleanupFailure ? CpuPmContinuityFailure.UnregisterFailed
                    : fake.ThrowAt == "continuity-unconfirmed" ? CpuPmContinuityFailure.PowerChanged : CpuPmContinuityFailure.Disposed
                    : fake.ContinuityFailure ? CpuPmContinuityFailure.PowerChanged : CpuPmContinuityFailure.None);
            public void Dispose()
            {
                fake.Event("continuity-dispose"); fake.DuringContinuityDispose?.Invoke(fake); disposed = true;
                if (fake.ThrowAt == "continuity-dispose") { throw new IOException(); }
                if (fake.ThrowAt == "continuity-dispose-cancel") { throw new OperationCanceledException(); }
                if (fake.ThrowAt == "continuity-dispose-unknown") { throw new NotSupportedException(); }
            }
        }
        private sealed class Mutex(Fake fake) : ICpuQueryMutex
        {
            public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken token) { fake.Event("wait"); token.ThrowIfCancellationRequested(); fake.Locked = fake.Wait != CpuQueryMutexWait.Timeout; return fake.Wait; }
            public void Release() { fake.Event("release"); Assert.True(fake.Locked); fake.Locked = false; }
            public void Dispose() => fake.Event("mutex-dispose");
        }
        private sealed class Device(Fake fake) : ICpuQueryRetainableDevice
        {
            public ICpuQueryDevice Duplicate() { fake.Event("duplicate"); Assert.True(fake.Pinned); Assert.True(fake.Locked); return new Device(fake); }
            public CpuQueryIoctlReply Invoke(uint code, byte[] input, int length) => fake.Invoke(code, input, length);
            public void Dispose() { fake.Event("close"); if (fake.ThrowAt == "close") { throw new IOException(); } }
        }
        private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }
        private CpuQueryIoctlReply Invoke(uint code, byte[] input, int length)
        {
            Assert.True(Pinned); Assert.True(Locked); Threads.Add(Environment.CurrentManagedThreadId); Codes.Add(code);
            byte[] reply = new byte[length];
            if (code == 0x81112F18)
            {
                Put(reply, 24, 3);
                if (length > 28)
                {
                    uint[][] records = [[0, 0, 0, CpuQueryPciTopology.RootId], [0, 0x18, 0, CpuQueryPciTopology.NodeId], [0, 0x18, 1, CpuQueryPciTopology.OrientationId]];
                    for (int i = 0; i < 3; i++) { for (int j = 0; j < 4; j++) { Put(reply, 28 + i * 52 + j * 4, records[i][j]); } }
                }
            }
            else if (code == 0x81112FF8)
            {
                if (Word(input, 0) == 0) { selected = Word(input, 4); }
                else if (selected == 0x03B10A20)
                {
                    command = Word(input, 4); Commands.Add(command);
                    if (command == 0x65 && RefreshFails) { return new(false, [], CpuQueryFailureCode.NativeIoctlFailed, 5); }
                }
            }
            else if (code == 0x81112F24)
            {
                input.CopyTo(reply, 0); uint d = Word(input, 4), f = Word(input, 8), offset = Word(input, 12);
                uint value = offset == 0 ? d == 0 ? CpuQueryPciTopology.RootId : f == 0 ? CpuQueryPciTopology.NodeId : CpuQueryPciTopology.OrientationId
                    : offset == 0x140 ? 0xFF55u : selected == 0x03820AB0 ? ~0xFF55u << 5
                    : selected == 0x03820A0F ? CapFirst : selected == 0x03820A0B ? CapSecond
                    : selected is 0x03820A13 or 0x03820A90 or 0x03B10A80 ? 1u
                    : selected == 0x03B10A88 ? command == 2 ? Firmware : command == 6 ? 0x005D000Bu : 0x12345678u : throw new IOException("Unexpected selection.");
                if (selected == 0x03B10A80 && command == 0x65)
                {
                    value = RefreshResponse;
                    if (CorruptRefreshEcho) { reply[0] ^= 1; }
                }
                Put(reply, 20, value);
                if (offset == 0xC8) { AfterRegisterRead?.Invoke(selected, command); }
            }
            else if (code == 0x81112F08)
            {
                Copies++; selected = 0; if (ThrowAt == "copy") { throw new IOException(); }
                Assert.Equal(0xD54, length); Assert.Equal(0xD50u, Word(input, 0)); Put(reply, 0, 0xD50);
                for (int i = 0; i < 6; i++) { Put(reply, 4 + 4 * i, 0x3F800000); }
                AfterCopy?.Invoke(this);
            }
            else { throw new IOException("Unapproved request."); }
            return new(true, reply);
        }
        public TimeSpan MonotonicNow => now;
        public long MonotonicTimestamp => now.Ticks;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + now;
        public void DelayOneSecond(CancellationToken token) { token.ThrowIfCancellationRequested(); Assert.True(Pinned); Assert.True(Locked); IdleDelays++; now += TimeSpan.FromSeconds(1); }
        private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
}
