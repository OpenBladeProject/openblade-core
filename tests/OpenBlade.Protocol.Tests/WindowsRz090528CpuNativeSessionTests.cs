using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsRz090528CpuNativeSessionTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "2.02");

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
        internal int Opens, Pins, BackendReads, Releases;
        internal bool Pinned, Held, FailCleanup;
        private uint selected, argument, result;
        private bool submitted;

        public HostIdentityReadResult ReadHost() => new(new("Razer", "Blade 16 - RZ09-0528", "RZ09-05289EN4", "2.02"), null);
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x1A, 0x24, 0);
        public bool ControllersAbsent() => true;
        public IDisposable PinBackendFiles()
        {
            Assert.True(Held); Assert.False(Pinned); Pinned = true; Pins++;
            return new Cleanup(() => { Pinned = false; if (FailCleanup) { throw new IOException(); } });
        }
        public InstalledCpuBackendSnapshot ReadBackend()
        {
            Assert.True(Pinned); Assert.True(Held); BackendReads++;
            return new(Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair =>
                new CpuBackendFileMetadata(pair.Key, pair.Value, true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray(),
                true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, true);
        }
        public ICpuQueryDevice OpenExistingDevice() { Assert.True(Pinned); Assert.True(Held); Opens++; return this; }
        public ICpuQueryMutex CreateSharedMutex() => this;
        public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken cancellationToken)
        { Assert.False(Held); Held = true; return CpuQueryMutexWait.Acquired; }
        public void Release() { Assert.True(Held); Held = false; Releases++; }
        public void Dispose() { }

        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength)
        {
            Assert.True(Pinned); Assert.True(Held);
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
    }

    private sealed class Cleanup(Action cleanup) : IDisposable { public void Dispose() => cleanup(); }
}
