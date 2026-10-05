using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsRz090528CpuNativeSessionTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "2.02");

    [Theory]
    [InlineData("FullAdmission")]
    [InlineData("MutexWait")]
    [InlineData("Continuity")]
    [InlineData("NativeInvocation")]
    public async Task ProgressCanFreezeWhileInstalledBoundaryIsPending(string bucketName)
    {
        var bucket = Enum.Parse<CpuQueryBucket>(bucketName);
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        using var held = session.TryRetainInstalledBackend(() => Exact);
        Assert.NotNull(held);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        void Pending() { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
        if (bucket == CpuQueryBucket.FullAdmission) { platform.BeforeHost = Pending; }
        if (bucket == CpuQueryBucket.MutexWait) { platform.BeforeWait = Pending; }
        if (bucket == CpuQueryBucket.NativeInvocation) { platform.BeforeIoctl = Pending; }
        int checks = 0;
        var approval = held.CreateQueryApproval(() =>
        {
            if (++checks == 4 && bucket == CpuQueryBucket.Continuity) { Pending(); }
            return true;
        });
        var progress = new CpuQueryProgress();
        var worker = Task.Run(() => session.QueryInstalled(approval, () => Exact, progress: progress));
        string frozen;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            // Must complete while the native/guard/mutex fake remains blocked.
            frozen = await Task.Run(() => progress.Snapshot(freeze: true)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("bucket " + bucket, frozen);
            Assert.Contains("completed IOCTLs 0", frozen);
            Assert.False(worker.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(CpuLiveQueryStatus.Observed, (await worker.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(frozen, progress.Snapshot());
        Assert.Equal(164, platform.Invocations.Count);
        Assert.Equal(new uint[] { 6 }.Concat(Enumerable.Repeat(0x5Eu, 12)), platform.Commands);
    }

    [Fact]
    public void ProgressDoesNotChangeExactInstalledTrafficAndCountsReturnedCalls()
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        using var held = session.TryRetainInstalledBackend(() => Exact);
        Assert.NotNull(held);
        Assert.Equal(CpuLiveQueryStatus.Observed, session.QueryInstalled(held.CreateQueryApproval(() => true), () => Exact).Status);
        var expected = platform.Invocations.ToArray(); platform.Invocations.Clear();
        var progress = new CpuQueryProgress();
        Assert.Equal(CpuLiveQueryStatus.Observed, session.QueryInstalled(held.CreateQueryApproval(() => true), () => Exact, progress: progress).Status);
        Assert.Equal(expected, platform.Invocations);
        Assert.Contains("completed IOCTLs 164", progress.Snapshot());
        Assert.Contains("stage Cleanup", progress.Snapshot());
        Assert.Contains("cumulative ms Other=", progress.Snapshot());
    }

    [Fact]
    public void InstalledFactoryRetainsIdentityAndGrantsNoAuthority()
    {
        var session = WindowsRz090528CpuQueryAdapter.GetInstalledNativeSession();
        Assert.Same(session, WindowsRz090528CpuQueryAdapter.GetInstalledNativeSession());
        // Neither denied call may read identity, acquire resources or invoke native code.
        Assert.Equal(CpuLiveQueryStatus.Unavailable,
            session.Query(null, () => throw new InvalidOperationException()).Status);
        var result = session.ExecuteCurve(Request(), null, () => throw new InvalidOperationException());
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition);
        Assert.Equal(CpuLiveCurveNativeFailure.AdmissionDenied, result.Failure);
        Assert.True(result.CleanupComplete);
        Assert.Equal(CpuLiveQueryStatus.Unavailable,
            WindowsRz090528CpuQueryAdapter.QueryForAttendedValidation(null, () => throw new InvalidOperationException()).Status);
        Assert.False(WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(null,
            () => throw new InvalidOperationException()).Complete);
        Assert.Same(session, WindowsRz090528CpuQueryAdapter.GetInstalledNativeSession());
    }

    [Fact]
    public void InjectedFactoryRetainsExactlyOneFacadePerPlatform()
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        Assert.Same(session, WindowsRz090528CpuNativeSession.ForPlatform(platform));
        Assert.NotSame(session, WindowsRz090528CpuNativeSession.ForPlatform(new Platform()));
        Assert.Equal(0, platform.Opens);
        Assert.Equal(0, platform.Pins);
    }

    [Fact]
    public void FacadeKeepsQueryApprovalAndExactCurveProofSeparateAndRevalidatesPinnedBackend()
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        var approval = Approval();
        Assert.Equal(CpuLiveQueryStatus.Observed, session.Query(approval, () => Exact).Status);
        Assert.Equal(new uint[] { 6 }.Concat(Enumerable.Repeat(0x5Eu, 12)), platform.Commands);
        var request = Request();
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted,
            session.ExecuteCurve(request, null, () => Exact).Disposition);
        Assert.Equal(1, platform.Opens);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, session.Query(approval, () => Exact).Status);

        platform.Commands.Clear();
        var proof = new Proof(request, platform);
        var result = session.ExecuteCurve(request, proof, () => Exact);
        Assert.True(result.Verified);
        Assert.True(result.CleanupComplete);
        Assert.Equal(new uint[] { 6 }.Concat(Enumerable.Repeat(0x5Eu, 12)).Concat([0x82u, 0x53u, 6u])
            .Concat(Enumerable.Repeat(0x5Eu, 12)), platform.Commands);
        Assert.Equal(2, platform.Opens);
        Assert.Equal(2, platform.Pins);
        Assert.Equal(5, platform.BackendReads); // Two per lease, plus final setter admission.
        Assert.Equal(2, platform.Releases);
        Assert.False(platform.Pinned);
        Assert.False(platform.Held);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted,
            session.ExecuteCurve(request, proof, () => Exact).Disposition);
        Assert.Equal(2, platform.Opens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeitherRewrappingNorChangingOperationEscapesUncertainCleanup(bool curveFirst)
    {
        var platform = new Platform { FailCleanup = true };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        var request = Request();
        if (curveFirst)
        {
            var result = session.ExecuteCurve(request, new Proof(request, platform), () => Exact);
            Assert.False(result.CleanupComplete);
            Assert.Equal(CpuLiveCurveNativeFailure.Cleanup, result.Failure);
        }
        else
        {
            var result = session.Query(Approval(), () => Exact);
            Assert.Equal(CpuQueryStage.Cleanup, result.Diagnostic!.Stage);
        }
        platform.FailCleanup = false;
        var next = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        Assert.Same(session, next);
        int calls = platform.Commands.Count;
        Assert.Equal(CpuLiveQueryStatus.Busy, next.Query(Approval(), () => Exact).Status);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted,
            next.ExecuteCurve(request, new Proof(request, platform), () => Exact).Disposition);
        Assert.Equal(CpuLiveQueryStatus.Busy, WindowsRz090528CpuQueryAdapter.QueryForAttendedValidation(
            Approval(), () => Exact, platform).Status);
        Assert.Equal(1, platform.Opens);
        Assert.Equal(1, platform.Pins);
        Assert.Equal(calls, platform.Commands.Count);
    }

    [Fact]
    public void InstalledLeaseKeepsOneFileObjectAcrossQueriesAndCurveOperations()
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        bool initialCallCurrent = true;
        using var lease = session.TryRetainInstalledBackend(() => initialCallCurrent ? Exact : null)!;
        Assert.NotNull(lease);
        initialCallCurrent = false;
        Assert.True(lease.IsCurrent); // Acquisition's caller is not the retained lifetime.
        Assert.True(platform.Pinned);
        Assert.False(platform.Held);
        Assert.Empty(platform.Commands);
        Assert.Null(session.TryRetainInstalledBackend(() => Exact));

        var approval = lease.CreateQueryApproval(() => true);
        Assert.Equal(CpuLiveQueryStatus.Observed, session.QueryInstalled(approval, () => Exact).Status);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, session.QueryInstalled(approval, () => Exact).Status);
        Assert.Equal(CpuLiveQueryStatus.Observed,
            session.QueryInstalled(lease.CreateQueryApproval(() => true), () => Exact).Status);
        var request = Request();
        Assert.True(session.ExecuteCurve(request, new Proof(request, platform), () => Exact).Verified);
        Assert.Equal(1, platform.Opens);
        Assert.Equal(1, platform.Pins);
        Assert.Equal(3, platform.Duplicates);
        Assert.Equal(3, platform.DuplicateCloses);
        Assert.Equal(0, platform.AnchorCloses);
        lease.Dispose();
        Assert.Equal(1, platform.AnchorCloses);
        Assert.False(platform.Pinned);
        Assert.False(lease.IsCurrent);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, session.Query(Approval(), () => Exact).Status);
        Assert.Equal(1, platform.Opens); // No pathname fallback after revocation.
    }

    [Fact]
    public void InstalledApprovalRejectsWrongSessionAndExpiredCallerWithoutRevokingAnchor()
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        using var lease = session.TryRetainInstalledBackend(() => Exact)!;
        var other = new Platform();
        Assert.Equal(CpuLiveQueryStatus.Unavailable, WindowsRz090528CpuNativeSession.ForPlatform(other)
            .QueryInstalled(lease.CreateQueryApproval(() => true), () => Exact).Status);
        Assert.Equal(0, other.Opens);
        Assert.Equal(CpuLiveQueryStatus.Unavailable,
            session.QueryInstalled(lease.CreateQueryApproval(() => false), () => Exact).Status);
        Assert.True(lease.IsCurrent);
        Assert.Equal(0, platform.Duplicates);
        Assert.Equal(CpuLiveQueryStatus.Observed,
            session.QueryInstalled(lease.CreateQueryApproval(() => true), () => Exact).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevocationBetweenNativeCallsReportsAdmissionInsteadOfIoError(bool revokeAnchor)
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        using var lease = session.TryRetainInstalledBackend(() => Exact)!;
        bool current = true;
        platform.BeforeIoctl = () => { if (revokeAnchor) { lease.Dispose(); } else { current = false; } };
        var result = session.QueryInstalled(lease.CreateQueryApproval(() => current), () => Exact);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, result.Status);
        Assert.Equal(CpuQueryFailureCode.AdmissionDenied, result.Diagnostic!.Failure);
        Assert.Null(result.Diagnostic.NativeErrorCode);
        Assert.Single(platform.Invocations);
        Assert.Equal(1, platform.DuplicateCloses);
        Assert.False(platform.Held);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledServiceDriftRevokesPermanentlyBeforeAnyTraffic(bool configurationOnly)
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        using var lease = session.TryRetainInstalledBackend(() => Exact)!;
        platform.Drift = !configurationOnly;
        if (configurationOnly) { platform.Start = 2; }
        Assert.Equal(CpuLiveQueryStatus.Unavailable,
            session.QueryInstalled(lease.CreateQueryApproval(() => true), () => Exact).Status);
        Assert.False(lease.IsCurrent);
        Assert.Empty(platform.Commands);
        Assert.Equal(0, platform.Duplicates);
        Assert.Equal(1, platform.AnchorCloses);
        platform.Drift = false;
        Assert.Null(session.TryRetainInstalledBackend(() => Exact));
        Assert.Equal(CpuLiveQueryStatus.Unavailable, session.Query(Approval(), () => Exact).Status);
        Assert.Equal(1, platform.Opens);
    }

    [Fact]
    public async Task RevocationWhileIoctlIsBlockedRetainsActualDeviceAndPinsUntilWorkerCleanup()
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        using var lease = session.TryRetainInstalledBackend(() => Exact)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        platform.BeforeIoctl = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var work = Task.Run(() => session.QueryInstalled(lease.CreateQueryApproval(() => !cancellation.IsCancellationRequested),
            () => Exact, cancellation.Token));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            lease.Dispose();
            Assert.False(work.IsCompleted);
            Assert.True(platform.Pinned);
            Assert.Equal(0, platform.AnchorCloses);
            Assert.Equal(0, platform.DuplicateCloses);
            Assert.Equal(CpuLiveQueryStatus.Busy, session.Query(Approval(), () => Exact).Status);
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await work);
        Assert.Equal(1, platform.DuplicateCloses);
        Assert.Equal(1, platform.AnchorCloses);
        Assert.False(platform.Pinned);
    }

    [Fact]
    public void FailedDuplicateCleanupParksAnchorAndSharedSession()
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        using var lease = session.TryRetainInstalledBackend(() => Exact)!;
        platform.FailDuplicateClose = true;
        var result = session.QueryInstalled(lease.CreateQueryApproval(() => true), () => Exact);
        Assert.Equal(CpuQueryStage.Cleanup, result.Diagnostic!.Stage);
        Assert.False(lease.IsCurrent);
        lease.Dispose();
        Assert.True(platform.Pinned);
        Assert.Equal(0, platform.AnchorCloses);
        Assert.Equal(CpuLiveQueryStatus.Busy, session.Query(Approval(), () => Exact).Status);
        Assert.Null(session.TryRetainInstalledBackend(() => Exact));
    }

    [Fact]
    public void DriftAcrossDuplicationRevokesBeforeAnyIoctlAndClosesOnlyAfterCleanup()
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        using var lease = session.TryRetainInstalledBackend(() => Exact)!;
        platform.OnDuplicate = () => platform.Start = 2;
        var result = session.QueryInstalled(lease.CreateQueryApproval(() => true), () => Exact);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, result.Status);
        Assert.Equal(CpuQueryStage.InstalledBackend, result.Diagnostic!.Stage);
        Assert.Empty(platform.Commands);
        Assert.Equal(1, platform.DuplicateCloses);
        Assert.Equal(1, platform.AnchorCloses);
        Assert.False(platform.Pinned);
        Assert.False(lease.IsCurrent);
    }

    [Fact]
    public void AcquisitionCancellationDoesNotBecomePermanentRequestAuthority()
    {
        var platform = new Platform();
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        using var acquisition = new CancellationTokenSource();
        using var lease = session.TryRetainInstalledBackend(() => Exact, acquisition.Token)!;
        acquisition.Cancel();
        Assert.True(lease.IsCurrent);
        using var request = new CancellationTokenSource();
        request.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => session.QueryInstalled(
            lease.CreateQueryApproval(() => true), () => Exact, request.Token));
        Assert.Equal(0, platform.Duplicates);
        Assert.Equal(CpuLiveQueryStatus.Observed,
            session.QueryInstalled(lease.CreateQueryApproval(() => true), () => Exact).Status);
    }

    private static CpuQueryAttendedApproval Approval() => CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true);
    private static CpuLiveCurveNativeRequest Request()
    {
        var snapshot = CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50,
            Enumerable.Range(0, 16).Where(CpuLiveCurveSnapshot.IsEnabled).Select(id => new CpuLiveCoreOffset(id, -1)).ToArray()))!;
        Assert.True(CpuLiveCurveCommand.TryCreate(0, 0, 0xFF55, out var command));
        return new(new(), command, CpuLiveCurveCommandPurpose.Forward, snapshot);
    }

    private sealed class Proof(CpuLiveCurveNativeRequest request, Platform platform) : CpuLiveCurveWriteProof(request)
    {
        internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest) => true;
        internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest)
        {
            Assert.True(platform.Pinned); Assert.True(platform.Held);
            return new(0x5000A, 0, 50, 1, true);
        }
    }

    private sealed class Platform : ICpuQueryWindowsPlatform, ICpuQueryDevice, ICpuQueryMutex
    {
        internal readonly List<uint> Commands = [];
        internal readonly List<string> Invocations = [];
        internal int Opens, Pins, BackendReads, Releases, Duplicates, DuplicateCloses, AnchorCloses;
        internal int Start = 3;
        internal bool Pinned, Held, FailCleanup, Drift, FailDuplicateClose;
        internal Action? BeforeIoctl, BeforeHost, BeforeWait;
        internal Action? OnDuplicate;
        private uint selected, argument, result;
        private bool submitted;

        public HostIdentityReadResult ReadHost()
        {
            Interlocked.Exchange(ref BeforeHost, null)?.Invoke();
            return new(new("Razer", "Blade 16 - RZ09-0528", "RZ09-05289EN4", "2.02"), null);
        }
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x1A, 0x24, 0);
        public bool ControllersAbsent() => true;
        public IDisposable PinBackendFiles()
        {
            Assert.True(Held); Assert.False(Pinned); Pinned = true; Pins++;
            return new Cleanup(() => { Pinned = false; if (FailCleanup) { throw new IOException(); } });
        }
        public InstalledCpuBackendSnapshot ReadBackend()
        {
            Assert.True(Pinned); BackendReads++;
            return new(Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair =>
                new CpuBackendFileMetadata(pair.Key, pair.Value, true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray(),
                true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, Start, !Drift);
        }
        public ICpuQueryDevice OpenExistingDevice() { Assert.True(Pinned); Assert.True(Held); Opens++; return new DeviceHandle(this, false); }
        public ICpuQueryMutex CreateSharedMutex() => this;
        public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken cancellationToken)
        { Interlocked.Exchange(ref BeforeWait, null)?.Invoke(); Assert.False(Held); Held = true; return CpuQueryMutexWait.Acquired; }
        public void Release() { Assert.True(Held); Held = false; Releases++; }
        public void Dispose() { }

        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength)
        {
            Assert.True(Pinned); Assert.True(Held);
            Interlocked.Exchange(ref BeforeIoctl, null)?.Invoke();
            Invocations.Add($"{code:X8}:{Convert.ToHexString(input)}:{outputLength}");
            var output = new byte[outputLength];
            if (code == 0x81112F18)
            {
                var records = CpuQueryPciTestRecords.Create(null);
                Put(output, 24, Word(records, 24));
                if (outputLength > 28) { records.CopyTo(output, 0); }
            }
            else if (code == 0x81112F24)
            {
                input.CopyTo(output, 0);
                uint offset = Word(input, 12);
                uint value = offset switch
                {
                    0 => Word(input, 4) == 0 ? 0x15071022u : Word(input, 8) == 0 ? 0x16F81022u : 0x16F91022u,
                    0x140 => 0xFF55,
                    _ => selected switch { 0x03820AB0 => (~0xFF55u & 0xFFFF) << 5, 0x03B10A80 => 1, 0x03B10A88 => result, _ => throw new InvalidOperationException() },
                };
                Put(output, 20, value);
            }
            else if (code == 0x81112FF8)
            {
                uint value = Word(input, 4);
                if (Word(input, 0) == 0) { selected = value; }
                else if (selected == 0x03B10A88) { argument = value; }
                else if (selected == 0x03B10A20)
                {
                    Commands.Add(value);
                    if (value == 0x53) { submitted = true; }
                    result = value switch { 6 => 0x005D000B, 0x82 => 4, 0x5E => submitted && argument == 0 ? 0u : 0xFFFFu, 0x53 => 0, _ => throw new InvalidOperationException() };
                }
            }
            else { throw new InvalidOperationException(); }
            return new(true, output);
        }
        private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

        private sealed class DeviceHandle(Platform platform, bool duplicate) : ICpuQueryRetainableDevice
        {
            public ICpuQueryDevice Duplicate()
            { platform.Duplicates++; platform.OnDuplicate?.Invoke(); return new DeviceHandle(platform, true); }
            public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength) => platform.Invoke(code, input, outputLength);
            public void Dispose()
            {
                if (duplicate)
                {
                    platform.DuplicateCloses++;
                    if (platform.FailDuplicateClose) { throw new IOException(); }
                }
                else { platform.AnchorCloses++; }
            }
        }
    }

    private sealed class Cleanup(Action cleanup) : IDisposable { public void Dispose() => cleanup(); }
}
