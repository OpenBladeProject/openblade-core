using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsRz090528CpuPmObservationAdapterTests
{
    private static CpuPmAttendedApproval Approval(Fake fake) => CpuPmAttendedApproval.Acknowledge(true, true, fake.Lifetime);
    private static CpuPmObservationBatch Run(Fake fake, CpuPmAttendedApproval? approval = null) =>
        WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(approval ?? Approval(fake), () => fake.FreshLifetime, fake, fake);

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

    [Fact]
    public void ThreeSamplesReuseActualThreadAffineLeaseAndTwoIndependentPmWireGuards()
    {
        var fake = new Fake(); var result = Run(fake);
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
    [InlineData("bios")]
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
        var fake = new Fake { AfterOpen = f => f.Failure = "bios" };
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
    }

    [Fact]
    public void CancellationBeforeArmHasNoPlatformEffects()
    {
        var fake = new Fake(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(
            Approval(fake), () => fake.Lifetime, fake, fake, canceled.Token)); Assert.Empty(fake.Events);
    }

    private sealed class Fake : ICpuPmWindowsPlatform, ICpuPmObservationClock
    {
        internal CpuPmRunOwnedDriverLifetime Lifetime = new(Guid.NewGuid(), Guid.NewGuid(), true);
        internal CpuPmRunOwnedDriverLifetime? FreshLifetime;
        internal string? Failure, ThrowAt;
        internal bool Pinned, Locked, ContinuityFailure, CleanupFailure;
        internal CpuQueryMutexWait Wait = CpuQueryMutexWait.Acquired;
        internal int FullBackendReads, ServiceReads, Copies, IdleDelays;
        internal Action<Fake>? AfterOpen, AfterCopy;
        internal readonly List<string> Events = [];
        internal readonly List<uint> Codes = [], Commands = [];
        internal readonly List<int> Threads = [];
        private TimeSpan now;
        private uint selected, command;
        internal Fake() => FreshLifetime = Lifetime;
        private void Event(string value) { Events.Add(value); Threads.Add(Environment.CurrentManagedThreadId); }
        public HostIdentityReadResult ReadHost() => new(new("Razer", "Blade 16 - RZ09-0528", Failure == "sku" ? "unknown" : "RZ09-05289EN4", Failure == "bios" ? "2.03" : "2.02"), null);
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x1A, 0x24, Failure == "stepping" ? 1 : 0);
        public CpuPmProcessorPackage ReadPackage() => new(Failure == "leaf" ? 0x80000000 : 0x80000001, Failure == "package" ? 0 : 1);
        public DeviceIdentity ReadUsb() => new("RZ09-0528", 0x1532, Failure == "usb" ? (ushort)0x02E0 : (ushort)0x02C6, "2.02");
        public bool ControllersAbsent() => Failure != "controller";
        public InstalledCpuBackendSnapshot ReadBackend()
        {
            FullBackendReads++;
            if (FullBackendReads > 1) { Assert.True(Pinned); Assert.True(Locked); }
            var files = Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair => new CpuBackendFileMetadata(pair.Key,
                Failure == "hash" ? "bad" : pair.Value, Failure != "signature", Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray();
            var service = Service(); return new(files, service.Exists, service.ImagePath, service.Type, service.Start, service.Running);
        }
        private CpuPmServiceSnapshot Service() => new(true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, Failure == "start" ? 2 : 3, Failure != "service");
        public CpuPmServiceSnapshot ReadService() { ServiceReads++; return Service(); }
        public IDisposable PinBackendFiles() { Event("pin"); Assert.True(Locked); Pinned = true; return new Release(() => { Event("unpin"); Pinned = false; if (ThrowAt == "unpin") { throw new IOException(); } }); }
        public ICpuQueryMutex CreateSharedMutex() { Event("mutex-create"); return new Mutex(this); }
        public ICpuQueryDevice OpenExistingDevice()
        {
            Event("open"); Assert.True(Pinned); Assert.True(Locked);
            if (ThrowAt == "open") { throw new IOException(); }
            AfterOpen?.Invoke(this); return new Device(this);
        }
        public ICpuPmContinuityScope ArmContinuity(CpuPmRunOwnedDriverLifetime lifetime)
        { Event("continuity-arm"); Assert.Equal(Lifetime, lifetime); return new Continuity(this); }
        private sealed class Continuity(Fake fake) : ICpuPmContinuityScope
        {
            private readonly Guid session = Guid.NewGuid(); private bool disposed;
            public CpuPmContinuityEvidence ReadFresh(CpuPmRunOwnedDriverLifetime? lifetime) => new(
                disposed || fake.ContinuityFailure ? CpuPmSessionContinuity.Interrupted : CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime,
                session, fake.Lifetime.DriverLifetimeId, disposed ? fake.CleanupFailure ? CpuPmContinuityFailure.UnregisterFailed : CpuPmContinuityFailure.Disposed
                    : fake.ContinuityFailure ? CpuPmContinuityFailure.PowerChanged : CpuPmContinuityFailure.None);
            public void Dispose() { fake.Event("continuity-dispose"); disposed = true; }
        }
        private sealed class Mutex(Fake fake) : ICpuQueryMutex
        {
            public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken token) { fake.Event("wait"); token.ThrowIfCancellationRequested(); fake.Locked = fake.Wait != CpuQueryMutexWait.Timeout; return fake.Wait; }
            public void Release() { fake.Event("release"); Assert.True(fake.Locked); fake.Locked = false; }
            public void Dispose() => fake.Event("mutex-dispose");
        }
        private sealed class Device(Fake fake) : ICpuQueryDevice
        {
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
                else if (selected == 0x03B10A20) { command = Word(input, 4); Commands.Add(command); }
            }
            else if (code == 0x81112F24)
            {
                input.CopyTo(reply, 0); uint d = Word(input, 4), f = Word(input, 8), offset = Word(input, 12);
                uint value = offset == 0 ? d == 0 ? CpuQueryPciTopology.RootId : f == 0 ? CpuQueryPciTopology.NodeId : CpuQueryPciTopology.OrientationId
                    : offset == 0x140 ? 0xFF55u : selected == 0x03820AB0 ? ~0xFF55u << 5 : selected == 0x03B10A80 ? 1u
                    : selected == 0x03B10A88 ? command == 6 ? 0x005D000Bu : 0x12345678u : throw new IOException("Unexpected selection.");
                Put(reply, 20, value);
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
