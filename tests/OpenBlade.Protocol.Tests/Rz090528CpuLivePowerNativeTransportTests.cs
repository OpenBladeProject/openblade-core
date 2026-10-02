using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuLivePowerNativeTransportTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "2.02");

    [Theory]
    [InlineData(0, 0x32u)]
    [InlineData(1, 0x33u)]
    public void OneIndependentSetterHasFreshUnequalFullPairReadbackAndTwoBracketedCopies(int limitValue, uint message)
    {
        var limit = (CpuLivePowerLimit)limitValue;
        var platform = new Platform(); var request = Request(platform.Pair, limit: limit);
        var proof = new Proof(request, platform);
        var result = Run(platform, request, proof);
        Assert.True(result.Verified); Assert.True(result.CleanupComplete); Assert.True(result.WorkerTerminal);
        Assert.Equal(CpuLivePowerNativeDisposition.Acknowledged, result.Disposition);
        Assert.Equal(request.After, result.After!.Pair);
        Assert.Equal(new uint[] { 0x02, 0x82, 6, 0x65, 6, 0x82, message, 6, 0x65, 6 }, platform.Commands);
        Assert.Equal(2, platform.Copies); Assert.Equal(1, platform.Setters); Assert.Equal(3, proof.RangeReads);
        Assert.Equal(1, platform.Opens); Assert.Equal(1, platform.Releases);
        Assert.Equal(3, platform.BackendReads); Assert.False(platform.Pinned); Assert.False(platform.Held);
        Assert.Equal(new[] { "close", "unpin", "release", "mutex-dispose" }, platform.Cleanup);
        Assert.Equal(limit == CpuLivePowerLimit.Fast ? 80000u : 85000u,
            result.After.Pair.Get(limit == CpuLivePowerLimit.Fast ? CpuLivePowerLimit.Slow : CpuLivePowerLimit.Fast).Milliwatts);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x005D000Bu)]
    [InlineData(0x0B5D1700u)]
    [InlineData(uint.MaxValue)]
    public void DifferentFirmwareVersionStopsBeforeBaselineOrAnySetter(uint firmwareVersion)
    {
        var platform = new Platform { FirmwareVersion = firmwareVersion }; var request = Request(platform.Pair);
        var proof = new Proof(request, platform);
        var result = Run(platform, request, proof);
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, result.Disposition);
        Assert.False(result.Verified); Assert.Null(result.After); Assert.True(result.CleanupComplete);
        Assert.Equal(new uint[] { 0x02 }, platform.Commands);
        Assert.Equal(0, platform.Copies); Assert.Equal(0, platform.Setters); Assert.Equal(0, proof.RangeReads);
        Assert.Equal(request.Before, platform.Pair);
        Assert.False(platform.Pinned); Assert.False(platform.Held);
    }

    [Theory]
    [InlineData("firmware-ack")]
    [InlineData("firmware-ack-failed-reply")]
    public void FirmwareAcknowledgementFailureStopsBeforeBaselineOrAnySetter(string fault)
    {
        var platform = new Platform { Fault = fault }; var request = Request(platform.Pair);
        var result = Run(platform, request, new Proof(request, platform));
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, result.Disposition);
        Assert.False(result.Verified); Assert.Null(result.After); Assert.True(result.CleanupComplete);
        Assert.Equal(new uint[] { 0x02 }, platform.Commands);
        Assert.Equal(0, platform.Copies); Assert.Equal(0, platform.Setters);
        Assert.Equal(request.Before, platform.Pair);
    }

    [Fact]
    public void EachLeaseReadsFirmwareAgainRatherThanReusingEarlierMatch()
    {
        var platform = new Platform(); var first = Request(platform.Pair);
        Assert.True(Run(platform, first, new Proof(first, platform)).Verified);
        int copies = platform.Copies, commands = platform.Commands.Count;
        platform.FirmwareVersion = 0x0B5D1700;
        var second = Request(platform.Pair, target: 59000);
        var result = Run(platform, second, new Proof(second, platform));
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, result.Disposition);
        Assert.False(result.Verified); Assert.True(result.CleanupComplete);
        Assert.Equal(new uint[] { 0x02 }, platform.Commands.Skip(commands));
        Assert.Equal(copies, platform.Copies); Assert.Equal(1, platform.Setters);
        Assert.Equal(first.After, platform.Pair);
    }

    [Fact]
    public void RestorePreservesExactFractionalMilliwattBaselineOutsideForwardEditorRange()
    {
        var original = Pair(102345, 80765);
        var platform = new Platform { Pair = Pair(60000, 80765) };
        var request = Request(platform.Pair, original, CpuLivePowerLimit.Fast, 102345, CpuLivePowerCommandPurpose.Restore);
        Assert.True(Run(platform, request, new Proof(request, platform)).Verified);
        Assert.Equal(original, platform.Pair);
        Assert.Equal(102345u, platform.SetterArgument);
    }

    [Fact]
    public void FusedCeilingIsConditionalButNeverOverridesIndependentFirmwareOrReverseAdmission()
    {
        var platform = new Platform { Capability = 0 };
        var request = Request(platform.Pair, target: 60000);
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, Run(platform, request, new Proof(request, platform)).Disposition);
        Assert.Equal(0, platform.Setters); // Immediate reverse85W exceeds the actual71W cap.
        platform = new Platform(); request = Request(platform.Pair);
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted,
            Run(platform, request, new Proof(request, platform) { Range = new(0x50001, 0, 84000, 1) }).Disposition);
        Assert.Equal(0, platform.Setters); // Bit1 bypasses only the fused check, never firmware range.
    }

    [Fact]
    public void CompleteOriginalRestorationForBothLimitsIsCheckedAfterRepeatedEdits()
    {
        var platform = new Platform { Pair = Pair(90000, 82000) };
        var request = Request(platform.Pair, Pair(85000, 80000), target: 89000);
        Assert.Equal(2, request.OriginalRestores.Count);
        var result = Run(platform, request, new Proof(request, platform) { Range = new(0x50001, 81000, 125000, 1) });
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, result.Disposition);
        Assert.Equal(0, platform.Setters); // Current pair and immediate reverse fit; original slow80W does not.
    }

    [Theory]
    [InlineData(0x5000Au, 1u)]
    [InlineData(0x50001u, 0u)]
    [InlineData(0x50001u, 2u)]
    public void UnreviewedFirmwareSelectorOrGranularityIsRejected(uint selector, uint step)
    {
        var platform = new Platform(); var request = Request(platform.Pair);
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted,
            Run(platform, request, new Proof(request, platform) { Range = new(selector, 0, 125000, step) }).Disposition);
        Assert.Equal(0, platform.Setters);
    }

    [Fact]
    public void CapAndCapabilityAreRecheckedAfterBeforeSnapshotAndRangeImmediatelyBeforeSubmission()
    {
        foreach (string fault in new[] { "capability-revoked", "range-revoked" })
        {
            var platform = new Platform { Fault = fault }; var request = Request(platform.Pair);
            var proof = new Proof(request, platform);
            var result = Run(platform, request, proof);
            Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, result.Disposition);
            Assert.Equal(0, platform.Setters);
            Assert.Equal(1, platform.Copies);
            Assert.Equal(2, platform.Commands.Count(command => command == 0x82));
        }
    }

    [Theory]
    [InlineData("before-drift")]
    [InlineData("before-noninvertible")]
    [InlineData("before-canonical-float")]
    [InlineData("version")]
    [InlineData("post-before-version")]
    [InlineData("copy-length")]
    [InlineData("root")]
    [InlineData("mask")]
    [InlineData("cap")]
    public void InvalidBeforeEvidenceNeverReachesSetter(string fault)
    {
        var platform = new Platform { Fault = fault }; var request = Request(platform.Pair);
        var result = Run(platform, request, new Proof(request, platform));
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, result.Disposition);
        Assert.True(result.CleanupComplete); Assert.False(result.Verified); Assert.Equal(0, platform.Setters);
    }

    [Theory]
    [InlineData("setter-throw")]
    [InlineData("setter-failed-reply")]
    [InlineData("setter-ack")]
    [InlineData("setter-cancel")]
    public void ActualSetterInvocationIsPossibleSubmissionEvenWithoutAcknowledgement(string fault)
    {
        using var cancelled = new CancellationTokenSource();
        var platform = new Platform { Fault = fault, Cancel = cancelled.Cancel }; var request = Request(platform.Pair);
        var result = Run(platform, request, new Proof(request, platform), cancelled.Token);
        Assert.Equal(CpuLivePowerNativeDisposition.PossibleSubmission, result.Disposition);
        Assert.False(result.Verified); Assert.True(result.CleanupComplete);
        Assert.Equal(1, platform.Setters); Assert.Equal(1, platform.Copies);
    }

    [Fact]
    public void ProofRevokedAfterSelectorPreparationStopsAtPreNativeSetterCheckpoint()
    {
        var platform = new Platform(); var request = Request(platform.Pair);
        int preparedChecks = 0;
        var proof = new Proof(request, platform)
        {
            ReadCurrent = () => !platform.SetterPrepared || ++preparedChecks == 1,
        };
        // The first check accepts the completed selector write; the next is
        // BeforeNativeInvoke for the actual setter message, before its marker.
        var result = Run(platform, request, proof);
        Assert.True(platform.SetterPrepared); Assert.Equal(2, preparedChecks);
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, result.Disposition);
        Assert.Equal(CpuLivePowerNativeFailure.AdmissionDenied, result.Failure);
        Assert.False(result.Verified); Assert.True(result.CleanupComplete);
        Assert.Equal(0, platform.Setters); Assert.Equal(request.Before, platform.Pair);
        Assert.Equal(new uint[] { 0x02, 0x82, 6, 0x65, 6, 0x82 }, platform.Commands);
        Assert.Equal(1, proof.RangeReads);
        Assert.False(platform.Pinned); Assert.False(platform.Held);
    }

    [Fact]
    public void ProofRevokedInsideActualSetterInvokePreservesPossibleSubmission()
    {
        var platform = new Platform(); var request = Request(platform.Pair);
        bool current = true;
        var proof = new Proof(request, platform) { ReadCurrent = () => current };
        platform.AtSetter = () => current = false;
        var result = Run(platform, request, proof);
        Assert.Equal(1, platform.Setters);
        Assert.Equal(request.After, platform.Pair); // The call took effect before revocation was observed.
        Assert.Equal(CpuLivePowerNativeDisposition.PossibleSubmission, result.Disposition);
        Assert.Equal(CpuLivePowerNativeFailure.AdmissionDenied, result.Failure);
        Assert.False(result.Verified); Assert.Null(result.After); Assert.True(result.CleanupComplete);
        Assert.Equal(new uint[] { 0x02, 0x82, 6, 0x65, 6, 0x82, 0x32 }, platform.Commands);
        Assert.False(platform.Pinned); Assert.False(platform.Held);
    }

    [Theory]
    [InlineData("firmware")]
    [InlineData("layout")]
    [InlineData("copy")]
    public async Task GetterOrCopyCancellationRetainsActualWorkerUntilCleanClosure(string blockedOperation)
    {
        using var cancelled = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        void Block() { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); }
        var platform = new Platform(); var request = Request(platform.Pair);
        if (blockedOperation == "copy") { platform.AtFirstCopy = Block; }
        else if (blockedOperation == "firmware") { platform.AtFirmwareVersionGetter = Block; }
        else { platform.AtVersionGetter = Block; }
        Task<CpuLivePowerNativeResult> worker = Task.Run(() => Run(platform, request, new Proof(request, platform), cancelled.Token));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await worker.WaitAsync(cancelled.Token));
            Assert.False(worker.IsCompleted); Assert.True(platform.Pinned); Assert.True(platform.Held);
            Assert.Empty(platform.Cleanup); Assert.Equal(0, platform.Setters);
            Assert.Equal(blockedOperation == "copy" ? 1 : 0, platform.Copies);
            Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, Run(platform, request, new Proof(request, platform)).Disposition);
            Assert.Equal(CpuLiveQueryStatus.Busy, WindowsRz090528CpuNativeSession.ForPlatform(platform).Query(
                CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), () => Exact).Status);
            AssertCurveBusy(platform);
            Assert.Equal(CpuPmObservationFailure.Busy, WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(
                CpuPmAttendedApproval.Acknowledge(true, true, platform.Lifetime), () => platform.Lifetime, platform, platform).Diagnostic!.Failure);
            Assert.Equal(1, platform.Opens);
        }
        finally { finish.Set(); }
        var result = await worker;
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, result.Disposition);
        Assert.Equal(CpuLivePowerNativeFailure.Cancelled, result.Failure);
        Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete); Assert.False(result.Verified);
        Assert.False(platform.Held); Assert.False(platform.Pinned); Assert.Equal(1, platform.Releases);
        Assert.Equal(new[] { "close", "unpin", "release", "mutex-dispose" }, platform.Cleanup);
        platform.AtFirstCopy = platform.AtVersionGetter = platform.AtFirmwareVersionGetter = null;
        Assert.True(Run(platform, request, new Proof(request, platform)).Verified); // Only terminal clean closure releases the gate.
    }

    [Theory]
    [InlineData("clamp")]
    [InlineData("other-limit")]
    [InlineData("stapm")]
    [InlineData("post-version")]
    public void AckDoesNotVerifyClampingDriftOrUnbracketedReadback(string fault)
    {
        var platform = new Platform { Fault = fault }; var request = Request(platform.Pair);
        var result = Run(platform, request, new Proof(request, platform));
        Assert.Equal(CpuLivePowerNativeDisposition.Acknowledged, result.Disposition);
        Assert.False(result.Verified); Assert.True(result.CleanupComplete);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("unpin-cancel")]
    [InlineData("acquire-cleanup-cancel")]
    public void UncertainCleanupParksAllNativePathsOnRetainedPlatform(string fault)
    {
        var platform = new Platform { Fault = fault }; var request = Request(platform.Pair);
        var result = Run(platform, request, new Proof(request, platform));
        Assert.False(result.CleanupComplete); Assert.False(result.Verified);
        Assert.Equal(CpuLivePowerNativeFailure.Cleanup, result.Failure);
        int opens = platform.Opens; platform.Fault = null;
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, Run(platform, request, new Proof(request, platform)).Disposition);
        Assert.Equal(CpuLiveQueryStatus.Busy, WindowsRz090528CpuNativeSession.ForPlatform(platform).Query(
            CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), () => Exact).Status);
        AssertCurveBusy(platform);
        Assert.Equal(CpuPmObservationFailure.Busy, WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(
            CpuPmAttendedApproval.Acknowledge(true, true, platform.Lifetime), () => platform.Lifetime, platform, platform).Diagnostic!.Failure);
        Assert.Equal(opens, platform.Opens);
    }

    [Fact]
    public void ProofIsIndependentSingleUseBoundToExactRequestAndFactoryGrantsNoAuthority()
    {
        var platform = new Platform(); var request = Request(platform.Pair); var proof = new Proof(request, platform);
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, Run(platform, request, null).Disposition);
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, Run(platform, Request(platform.Pair), proof).Disposition);
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, Run(platform, request, proof).Disposition);
        Assert.Equal(0, platform.Opens);
        Assert.False(typeof(CpuLivePowerWriteProof).IsAssignableFrom(typeof(CpuLiveCurveWriteProof)));
        Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, WindowsRz090528CpuQueryAdapter.GetInstalledNativeSession()
            .ExecutePower(request, null, () => throw new InvalidOperationException()).Disposition);
    }

    [Fact]
    public async Task CallerTimeoutCannotReleaseOutstandingPowerWorkerOrAllowQueryOrPm()
    {
        using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        var platform = new Platform { AtSetter = () => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); } };
        var request = Request(platform.Pair);
        Task<CpuLivePowerNativeResult> worker = Task.Run(() => Run(platform, request, new Proof(request, platform)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAsync<TimeoutException>(async () => await worker.WaitAsync(TimeSpan.FromMilliseconds(1)));
            Assert.True(platform.Held); Assert.True(platform.Pinned);
            Assert.Equal(CpuLivePowerNativeDisposition.NotSubmitted, Run(platform, request, new Proof(request, platform)).Disposition);
            Assert.Equal(CpuLiveQueryStatus.Busy, WindowsRz090528CpuNativeSession.ForPlatform(platform).Query(
                CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), () => Exact).Status);
            AssertCurveBusy(platform);
            Assert.Equal(CpuPmObservationFailure.Busy, WindowsRz090528CpuPmObservationAdapter.ObserveForAttendedValidation(
                CpuPmAttendedApproval.Acknowledge(true, true, platform.Lifetime), () => platform.Lifetime, platform, platform).Diagnostic!.Failure);
        }
        finally { finish.Set(); }
        Assert.True((await worker).Verified);
    }

    private static CpuLivePowerNativeResult Run(Platform platform, CpuLivePowerNativeRequest request, Proof? proof, CancellationToken token = default) =>
        WindowsRz090528CpuNativeSession.ForPlatform(platform).ExecutePower(request, proof, () => Exact, token);
    private static CpuLivePowerNativeRequest Request(CpuLivePowerPair before, CpuLivePowerPair? original = null,
        CpuLivePowerLimit limit = CpuLivePowerLimit.Fast, uint target = 60000, CpuLivePowerCommandPurpose purpose = CpuLivePowerCommandPurpose.Forward)
    {
        Assert.True(CpuLivePowerCommand.TryCreate(limit, target, out var command));
        return new(new(), command, purpose, before, original ?? before);
    }
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
        internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest) => true;
        internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest) => new(0x5000A, 0, 50, 1, true);
    }
    private sealed class Proof(CpuLivePowerNativeRequest request, Platform platform) : CpuLivePowerWriteProof(request)
    {
        internal CpuLivePowerTrustedRange Range = new(0x50001, 0, 125000, 1);
        internal int RangeReads;
        internal Func<bool>? ReadCurrent;
        internal override bool IsCurrent(CpuLivePowerNativeRequest exactRequest) => ReadCurrent?.Invoke() ?? true;
        internal override CpuLivePowerTrustedRange? ReadFreshRange(CpuLivePowerNativeRequest exactRequest)
        {
            Assert.True(platform.Pinned); Assert.True(platform.Held); RangeReads++;
            return platform.Fault == "range-revoked" && RangeReads == 2 ? null : Range;
        }
    }
    private sealed class Platform : ICpuPmWindowsPlatform, ICpuQueryDevice, ICpuQueryMutex, ICpuPmObservationClock
    {
        internal CpuLivePowerPair Pair = Rz090528CpuLivePowerNativeTransportTests.Pair(85000, 80000);
        internal readonly CpuPmRunOwnedDriverLifetime Lifetime = new(Guid.NewGuid(), Guid.NewGuid(), true);
        internal readonly List<uint> Commands = [];
        internal readonly List<string> Cleanup = [];
        internal uint Capability = 2, SetterArgument, FirmwareVersion = 0x0B5D1600;
        internal int Copies, Setters, Opens, Releases, BackendReads;
        internal bool Held, Pinned, SetterPrepared;
        internal string? Fault;
        internal Action? AtSetter, AtVersionGetter, AtFirmwareVersionGetter, AtFirstCopy, Cancel;
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
                if (offset == 0xC8 && selected == 0x03B10A88 && command == 6 && Copies == 0) { AtVersionGetter?.Invoke(); }
                if (offset == 0xC8 && selected == 0x03B10A88 && command == 0x02) { AtFirmwareVersionGetter?.Invoke(); }
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
                    if (value is 0x32 or 0x33)
                    {
                        Setters++; SetterArgument = argument; AtSetter?.Invoke();
                        if (Fault == "setter-throw") { throw new IOException(); }
                        if (Fault == "setter-failed-reply") { return new(false, []); }
                        if (Fault == "setter-cancel") { Cancel!(); }
                        Assert.True(CpuLivePowerCommand.TryDecode(value, argument, out var typed)); Pair = Pair.With(typed);
                        if (Fault == "clamp") { Assert.True(CpuLivePowerCommand.TryCreate(typed.Limit, argument - 1000, out typed)); Pair = Pair.With(typed); }
                        if (Fault == "other-limit") { Pair = Rz090528CpuLivePowerNativeTransportTests.Pair(Pair.Fast.Milliwatts, 70000); }
                        if (Fault == "stapm") { Pair = Pair with { StapmRawBits = BitConverter.SingleToUInt32Bits(46) }; }
                    }
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
