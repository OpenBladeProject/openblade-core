using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuLivePowerNativeTransportTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "2.02");

    [Fact]
    public void CombinedCurveExpiryAfterFullSetterProofCannotReachNativeSetter()
    {
        var platform = new Platform { Capability = 6 }; var request = CurveRequest(platform);
        bool current = true;
        using var proof = new CurveProof(request) { Scoped = true, Prepared = true, Reading = true };
        proof.FullAllowed = () => { if (proof.Ends == 1) { current = false; } return true; };
        proof.SubmissionAllowed = () => current;
        var result = WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(request, proof, () => Exact);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition);
        Assert.Equal(0, platform.Setters); Assert.True(proof.FullChecks > 0);
        Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedReadPrefixCannotSkipIndependentSetterBarrier(bool rejectSetter)
    {
        var ordinary = new Platform { Capability = 6 };
        var ordinaryRequest = CurveRequest(ordinary);
        Assert.True(WindowsRz090528CpuNativeSession.ForPlatform(ordinary)
            .ExecuteCurve(ordinaryRequest, new CurveProof(ordinaryRequest), () => Exact).Verified);
        var platform = new Platform { Capability = 6 }; var request = CurveRequest(platform);
        using var proof = new CurveProof(request) { Scoped = true, Prepared = true, Reading = true };
        proof.FullAllowed = () => !rejectSetter;
        platform.AtSetter = () => { Assert.False(proof.Reading); Assert.Equal(1, proof.Ends); Assert.True(proof.FullChecks > 0); };
        var result = WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(request, proof, () => Exact);
        Assert.Equal(!rejectSetter, result.Verified); Assert.False(proof.Reading);
        Assert.Equal(rejectSetter ? 0 : 1, platform.Setters);
        if (rejectSetter) { Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition); }
        else
        {
            Assert.Equal(ordinary.Calls, platform.Calls);
            Assert.True(CpuLiveCurveCommand.TryCreate(0, -1, 0xFF55, out var command));
            var restore = new CpuLiveCurveNativeRequest(new(), command, CpuLiveCurveCommandPurpose.Restore, request.After, new(platform.Pair, null));
            using var reverse = new CurveProof(restore) { Scoped = true, Prepared = true, Reading = true };
            platform.AtSetter = () => { Assert.False(reverse.Reading); Assert.True(reverse.FullChecks > 0); };
            Assert.True(WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(restore, reverse, () => Exact).Verified);
            Assert.Equal(2, platform.Setters);
        }
    }

    [Fact]
    public void CombinedReadContinuityKeepsExactTrafficAndIndependentRestoreBarrier()
    {
        var ordinary = new Platform { Capability = 6 };
        var originalRequest = CurveRequest(ordinary);
        Assert.True(WindowsRz090528CpuNativeSession.ForPlatform(ordinary)
            .ExecuteCurve(originalRequest, new CurveProof(originalRequest), () => Exact).Verified);
        var platform = new Platform { Capability = 6 };
        var request = CurveRequest(platform);
        var proof = new CurveProof(request) { Scoped = true };
        platform.AtSetter = () => { Assert.False(proof.Reading); Assert.Equal(1, proof.Ends); };
        Assert.True(WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(request, proof, () => Exact).Verified);
        Assert.Equal(ordinary.Calls, platform.Calls);
        Assert.Equal(2, proof.Begins); Assert.Equal(2, proof.Ends); Assert.Equal(2, proof.Completions);
        Assert.True(proof.ReadChecks > 100); Assert.True(proof.FullChecks > 0);
        Assert.True(CpuLiveCurveCommand.TryCreate(0, -1, 0xFF55, out var command));
        var restore = new CpuLiveCurveNativeRequest(new(), command, CpuLiveCurveCommandPurpose.Restore,
            request.After, new(platform.Pair, null));
        var restoreProof = new CurveProof(restore) { Scoped = true };
        platform.AtSetter = () => { Assert.False(restoreProof.Reading); Assert.Equal(1, restoreProof.Ends); };
        Assert.True(WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(restore, restoreProof, () => Exact).Verified);
        Assert.Equal(2, restoreProof.Begins); Assert.Equal(2, restoreProof.Completions);
        Assert.True(restoreProof.FullChecks > 0); Assert.Equal(2, platform.Setters);
    }

    [Theory]
    [InlineData("read-drift")]
    [InlineData("cancel")]
    [InlineData("setter-barrier")]
    [InlineData("after-setter")]
    [InlineData("final-read")]
    public void CombinedReadScopeCannotBypassDriftCancellationOrSetterAuthority(string fault)
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { Capability = 6 };
        var request = CurveRequest(platform);
        var proof = new CurveProof(request) { Scoped = true };
        if (fault == "read-drift") { platform.AtFirstCopy = () => proof.ReadAllowed = false; }
        if (fault == "cancel") { platform.AtFirstCopy = cancellation.Cancel; }
        if (fault == "setter-barrier") { proof.FullAllowed = () => proof.Ends == 0; }
        if (fault == "after-setter") { platform.AtSetter = () => proof.ReadAllowed = false; }
        if (fault == "final-read") { proof.CompletionAllowed = () => proof.Completions < 2; }
        var result = WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(request, proof, () => Exact, cancellation.Token);
        Assert.False(result.Verified); Assert.True(result.CleanupComplete); Assert.False(proof.Reading);
        Assert.Equal(fault is "after-setter" or "final-read" ? 1 : 0, platform.Setters);
        if (platform.Setters == 0) { Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition); }
        else { Assert.NotEqual(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinedCurveReadsPowerAfterCurveMetadataWithinOneLeaseAndNeverReenters(bool restoring)
    {
        var platform = new Platform { Capability = 6 };
        var request = CurveRequest(platform, restoring ? CpuLiveCurveCommandPurpose.Restore : CpuLiveCurveCommandPurpose.Forward);
        platform.AtFirstCopy = () => AssertCurveBusy(platform);
        var proof = new CurveProof(request)
        {
            ReadRange = () => { Assert.True(platform.Held); Assert.Equal(0, platform.Copies); return true; },
        };
        var result = WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(request, proof, () => Exact);
        Assert.True(result.Verified); Assert.Equal(CpuLiveCurveNativeDisposition.Acknowledged, result.Disposition);
        Assert.Equal(1, platform.Opens); Assert.Equal(1, platform.Releases);
        Assert.Equal(2, platform.Copies); Assert.Equal(1, platform.Setters);
        Assert.Equal(24, platform.Commands.Count(command => command == 0x5E));
        Assert.DoesNotContain(0x32u, platform.Commands); Assert.DoesNotContain(0x33u, platform.Commands);
        Assert.Equal(request.PowerPeer!.Expected, platform.Pair);
    }

    [Theory]
    [InlineData("drift")]
    [InlineData("copy-length")]
    [InlineData("peer-authority")]
    [InlineData("capability-revoked")]
    public void CombinedCurveStopsOnPeerLossAfterOwnRangeBeforeNativeSetter(string fault)
    {
        var platform = new Platform { Capability = 6, Fault = fault };
        var request = CurveRequest(platform);
        var proof = new CurveProof(request)
        {
            PeerAllowed = fault != "peer-authority",
            ReadRange = () => { if (fault == "drift") { platform.Pair = Pair(84000, 80000); } return true; },
        };
        var result = WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(request, proof, () => Exact);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition);
        Assert.False(result.Verified); Assert.True(result.CleanupComplete); Assert.Equal(0, platform.Setters);
        if (fault == "capability-revoked")
        {
            Assert.Equal(1, platform.Copies);
            Assert.Equal(CpuLiveCurveNativeFailure.AdmissionDenied, result.Failure);
        }
    }

    [Theory]
    [InlineData(false, "fast")]
    [InlineData(true, "fast")]
    [InlineData(false, "slow")]
    [InlineData(true, "slow")]
    [InlineData(false, "stapm")]
    [InlineData(true, "stapm")]
    [InlineData(false, "copy-length")]
    [InlineData(true, "copy-length")]
    public void CombinedCurvePowerReadbackLossRetainsSubmissionWithoutCompensation(bool restoring, string fault)
    {
        var platform = new Platform { Capability = 6 };
        var request = CurveRequest(platform, restoring ? CpuLiveCurveCommandPurpose.Restore : CpuLiveCurveCommandPurpose.Forward);
        platform.AtSetter = () =>
        {
            if (fault == "fast") { platform.Pair = Pair(84000, 80000); }
            if (fault == "slow") { platform.Pair = Pair(85000, 79000); }
            if (fault == "stapm") { platform.Pair = platform.Pair with { StapmRawBits = 0 }; }
            if (fault == "copy-length") { platform.Fault = fault; }
        };
        var result = WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(request, new CurveProof(request), () => Exact);
        Assert.Equal(CpuLiveCurveNativeDisposition.Acknowledged, result.Disposition);
        Assert.False(result.Verified); Assert.True(result.CleanupComplete);
        Assert.Equal(1, platform.Setters); Assert.Equal(2, platform.Copies);
        Assert.Equal(1, platform.Opens); Assert.Equal(1, platform.Releases);
        Assert.DoesNotContain(0x32u, platform.Commands); Assert.DoesNotContain(0x33u, platform.Commands);
        if (fault != "copy-length") { Assert.Equal(CpuLiveCurveNativeFailure.ReadbackDrift, result.Failure); }
    }

    [Fact]
    public void CombinedCurveSetterFailureRemainsPossibleSubmissionWithoutCompensation()
    {
        var platform = new Platform { Capability = 6, Fault = "setter-throw" };
        var request = CurveRequest(platform);
        var result = WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(request, new CurveProof(request), () => Exact);
        Assert.Equal(CpuLiveCurveNativeDisposition.PossibleSubmission, result.Disposition);
        Assert.False(result.Verified); Assert.Equal(1, platform.Setters);
    }

    private static CpuLiveCurveNativeRequest CurveRequest(Platform platform, CpuLiveCurveCommandPurpose purpose = CpuLiveCurveCommandPurpose.Forward)
    {
        Assert.True(CpuLiveCurveCommand.TryCreate(0, -2, 0xFF55, out var command));
        return new(new(), command, purpose, CurveSnapshot(), new(platform.Pair, null));
    }
    private static CpuLiveCurveSnapshot CurveSnapshot() => CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50,
        Enumerable.Range(0, 16).Where(CpuLiveCurveSnapshot.IsEnabled).Select(id => new CpuLiveCoreOffset(id, -1)).ToArray()))!;

    private static CpuLivePowerPair Pair(uint fast, uint slow)
    {
        Assert.True(CpuLivePowerValue.TryFromMilliwatts(fast, out var first));
        Assert.True(CpuLivePowerValue.TryFromMilliwatts(slow, out var second));
        return new(first, second, BitConverter.SingleToUInt32Bits(45));
    }
    private static void AssertCurveBusy(Platform platform)
    {
        var snapshot = CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50,
            Enumerable.Range(0, 16).Where(CpuLiveCurveSnapshot.IsEnabled).Select(id => new CpuLiveCoreOffset(id, -1)).ToArray()))!;
        Assert.True(CpuLiveCurveCommand.TryCreate(0, 0, 0xFF55, out var command));
        var request = new CpuLiveCurveNativeRequest(new(), command, CpuLiveCurveCommandPurpose.Forward, snapshot);
        var proof = new CurveProof(request);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted,
            WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecuteCurve(request, proof, () => Exact).Disposition);
        Assert.True(proof.Consume(request)); // Refused before consuming this distinct authority.
    }
    private sealed class CurveProof(CpuLiveCurveNativeRequest request) : CpuLiveCurveWriteProof(request)
    {
        internal Func<bool>? SubmissionAllowed;
        internal override bool IsSubmissionCurrent(CpuLiveCurveNativeRequest exactRequest) => SubmissionAllowed?.Invoke() ?? true;
        internal bool PeerAllowed = true;
        internal bool Scoped, Reading, ReadAllowed = true;
        internal bool Prepared;
        private bool adopted;
        internal int Begins, Ends, Completions, ReadChecks, FullChecks;
        internal Func<bool>? FullAllowed, CompletionAllowed;
        internal Func<bool>? ReadRange;
        private bool rangeRead;
        internal override bool IsPreparationCurrent(CpuLiveCurveNativeRequest exactRequest) => Prepared
            ? !adopted && Reading && ReadAllowed : IsCurrent(exactRequest);
        internal override IDisposable? BeginReadCore(CpuLiveCurveNativeRequest exactRequest)
        {
            if (!Scoped) { return null; }
            if (Prepared && !adopted) { Assert.True(Reading); adopted = true; }
            else { Assert.False(Reading); }
            Begins++; Reading = true;
            return new ReadScope(() => { Reading = false; Ends++; });
        }
        internal override bool IsReadCurrent(CpuLiveCurveNativeRequest exactRequest)
        { Assert.True(Reading); ReadChecks++; return ReadAllowed; }
        internal override bool CompleteReadCore(CpuLiveCurveNativeRequest exactRequest)
        { Assert.True(Reading); Completions++; return ReadAllowed && (CompletionAllowed?.Invoke() ?? true); }
        internal override bool AdmitPowerPeer(CpuLiveCurveNativeRequest exactRequest, uint capability, CpuLivePowerFusedCap cap) => PeerAllowed;
        internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest)
        { Assert.False(Reading); FullChecks++; return FullAllowed?.Invoke() ?? true; }
        internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest)
        {
            if (!rangeRead) { rangeRead = true; if (ReadRange?.Invoke() == false) { return null; } }
            return new(0x5000A, 0, 50, 1, true);
        }
        private sealed class ReadScope(Action end) : IDisposable { public void Dispose() => end(); }
    }
    private sealed class Platform : ICpuPmWindowsPlatform, ICpuQueryDevice, ICpuQueryMutex, ICpuPmObservationClock
    {
        internal CpuLiveCoreOffset[] Curve = CurveSnapshot().CoreOffsets.ToArray();
        internal CpuLivePowerPair Pair = Rz090528CpuLivePowerNativeTransportTests.Pair(85000, 80000);
        internal readonly CpuPmRunOwnedDriverLifetime Lifetime = new(Guid.NewGuid(), Guid.NewGuid(), true);
        internal readonly List<uint> Commands = [];
        internal readonly List<string> Calls = [];
        internal readonly List<string> Cleanup = [];
        internal uint Capability = 2, FirmwareVersion = 0x0B5D1600;
        internal int Copies, Setters, Opens, Releases, BackendReads;
        internal bool Held, Pinned, SetterPrepared;
        internal string? Fault;
        internal Action? AtSetter, AtFirstCopy;
        private uint selected, argument, command;
        private bool deviceOpen;
        public HostIdentityReadResult ReadHost() => new(new("Razer", Fault == "acquire-cleanup-cancel" && Pinned ? "other" : "Blade 16 - RZ09-0528", "RZ09-05289EN4", "2.02"), null);
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x1A, 0x24, 0);
        public CpuPmProcessorPackage ReadPackage() => new(0x80000001, 1);
        public DeviceIdentity ReadUsb() => Exact;
        public bool ControllersAbsent() => true;
        public CpuPmServiceSnapshot ReadService() => new(true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, true);
        public ICpuPmContinuityScope ArmContinuity(CpuPmRunOwnedDriverLifetime lifetime) => throw new InvalidOperationException("A busy PM call must not arm.");
        public InstalledCpuBackendSnapshot ReadBackend()
        {
            Assert.True(Pinned); Assert.True(Held); BackendReads++;
            return new(Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair => new CpuBackendFileMetadata(pair.Key, pair.Value,
                true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray(), true,
                @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, true);
        }
        public IDisposable PinBackendFiles()
        {
            Assert.True(Held); Pinned = true;
            return new PinRelease(() =>
            {
                Cleanup.Add("unpin"); Pinned = false;
                if (Fault is "unpin-cancel" or "acquire-cleanup-cancel") { throw new OperationCanceledException(); }
            });
        }
        public ICpuQueryMutex CreateSharedMutex() => this;
        public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken token) { Assert.False(Held); Held = true; return CpuQueryMutexWait.Acquired; }
        public void Release() { Assert.True(Held); Held = false; Releases++; Cleanup.Add("release"); }
        public ICpuQueryDevice OpenExistingDevice() { Assert.True(Pinned); Opens++; deviceOpen = true; return this; }
        public void Dispose()
        {
            if (deviceOpen) { deviceOpen = false; Cleanup.Add("close"); if (Fault == "close") { throw new IOException(); } }
            else { Cleanup.Add("mutex-dispose"); }
        }
        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int length)
        {
            Calls.Add($"{code:X8}/{length}/{Convert.ToHexString(input)}");
            Assert.True(Pinned); Assert.True(Held);
            var output = new byte[length];
            if (code == 0x81112F18)
            {
                var records = CpuQueryPciTestRecords.Create(null); Put(output, 24, Word(records, 24));
                if (length > 28) { records.CopyTo(output, 0); }
            }
            else if (code == 0x81112F24)
            {
                input.CopyTo(output, 0); uint offset = Word(input, 12);
                if (offset == 0xC8 && selected == 0x03B10A80 && command == 0x02 && Fault == "firmware-ack-failed-reply")
                { return new(false, []); }
                uint value = offset == 0 ? Word(input, 4) == 0 ? Fault == "root" ? 0 : CpuQueryPciTopology.RootId
                    : Word(input, 8) == 0 ? CpuQueryPciTopology.NodeId : CpuQueryPciTopology.OrientationId
                    : offset == 0x140 ? Fault == "mask" ? 0 : 0xFF55u
                    : selected switch
                    {
                        0x03820AB0 => (~0xFF55u & 0xFFFF) << 5,
                        0x03820A0F => Fault == "cap" ? uint.MaxValue : 4u,
                        0x03820A0B => 0x70000000u,
                        0x03B10A80 => command == 0x02 && Fault == "firmware-ack"
                            || Setters > 0 && Fault == "setter-ack" ? 0xFEu : 1u,
                        0x03B10A88 => command == 0x02 ? FirmwareVersion
                            : command == 6 ? Fault == "version" || Fault == "post-before-version" && Copies == 1
                            || Fault == "post-version" && Copies == 2 ? 0 : 0x005D000Bu
                            : command == 0x5E ? unchecked((ushort)Curve.Single(core => core.PhysicalCoreId == (int)(((argument >> 28) * 8) + ((argument >> 20) & 7))).Offset)
                            : command == 0x82 ? Fault == "capability-revoked" && Copies == 1 ? 0 : Capability : 1u,
                        _ => throw new InvalidOperationException(),
                    };
                Put(output, 20, value);
            }
            else if (code == 0x81112FF8)
            {
                uint value = Word(input, 4);
                if (Word(input, 0) == 0)
                {
                    selected = value;
                    if (selected == 0x03B10A20 && Commands.Count == 6) { SetterPrepared = true; }
                }
                else if (selected == 0x03B10A88) { argument = value; }
                else if (selected == 0x03B10A20)
                {
                    command = value; Commands.Add(value);
                    if (value == 0x53)
                    {
                        Setters++; AtSetter?.Invoke();
                        if (Fault == "setter-throw") { throw new IOException(); }
                        Assert.True(CpuLiveCurveCommand.TryDecode(value, argument, 0xFF55, out var curve));
                        int index = Array.FindIndex(Curve, core => core.PhysicalCoreId == curve.PhysicalCoreId);
                        Curve[index] = new(curve.PhysicalCoreId, curve.Offset);
                    }
                    Assert.False(value is 0x32 or 0x33);
                }
            }
            else if (code == 0x81112F08)
            {
                Assert.Equal(0xD50u, Word(input, 0)); Assert.Equal(0xD54, length); Copies++;
                if (Copies == 1) { AtFirstCopy?.Invoke(); }
                Put(output, 0, Fault == "copy-length" ? 0u : 0xD50u);
                Put(output, 4, Pair.StapmRawBits);
                uint fast = Pair.Fast.RawBits;
                if (Copies == 1 && Fault == "before-drift") { fast = BitConverter.SingleToUInt32Bits(84); }
                if (Copies == 1 && Fault == "before-noninvertible") { fast++; }
                if (Copies == 1 && Fault == "before-canonical-float") { fast = BitConverter.SingleToUInt32Bits(85); }
                Put(output, 12, fast); Put(output, 20, Pair.Slow.RawBits);
                selected = command = 0; // Model the driver's hidden mailbox/index work.
            }
            else { throw new InvalidOperationException(); }
            return new(true, output);
        }
        public TimeSpan MonotonicNow => TimeSpan.Zero;
        public long MonotonicTimestamp => 0;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public void DelayOneSecond(CancellationToken token) => throw new InvalidOperationException();
        private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        private sealed class PinRelease(Action action) : IDisposable { public void Dispose() => action(); }
    }
}
