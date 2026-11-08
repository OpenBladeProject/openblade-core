using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuLiveCurveNativeTransportTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "2.02");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevocationDuringFreshSetterProofBlocksForwardAndRestoreBeforeNativeInvocation(bool restoring)
    {
        var platform = new Platform();
        if (restoring) { platform.Offsets[0] = 0; }
        var request = Request(platform);
        if (restoring)
        {
            Assert.True(CpuLiveCurveCommand.TryCreate(0, -1, 0xFF55, out var command));
            request = new(request.Scope, command, CpuLiveCurveCommandPurpose.Restore, request.Before);
        }
        bool current = true;
        var proof = new Proof(request, platform) { SubmissionAllowed = () => current };
        proof.ReadRange = count => { if (count == 2) { current = false; } return new(0x5000A, 0, 50, 1, true); };
        var result = Run(platform, request, proof);
        Assert.Equal(2, proof.RangeReads);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition);
        Assert.Equal(0, platform.Setters);
        Assert.True(result.WorkerTerminal); Assert.True(result.CleanupComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedNonPeerReadEndsBeforeFreshSetterAndIndependentRestoration(bool rejectSetter)
    {
        var platform = new Platform();
        var request = Request(platform);
        var proof = new Proof(request, platform) { Prepared = true, Reading = true };
        platform.OnStage = stage =>
        {
            if (stage == "select-message" && rejectSetter) { proof.RejectSetter = true; }
            if (stage == "invoke")
            {
                Assert.False(proof.Reading);
                Assert.Equal(1, proof.ReadsCompleted);
                Assert.True(proof.FreshChecks > 0);
            }
        };
        var result = Run(platform, request, proof);
        Assert.True(result.CleanupComplete);
        Assert.False(proof.Reading);
        if (rejectSetter)
        {
            Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition);
            Assert.Equal(CpuLiveCurveNativeFailure.AdmissionDenied, result.Failure);
            Assert.Equal(0, platform.Setters);
            Assert.Equal(1, proof.ReadsCompleted);
            return;
        }
        Assert.True(result.Verified);
        Assert.Equal(2, proof.ReadsCompleted);
        Assert.Equal(2, proof.ReadsBegun);
        var ordinary = new Platform();
        var ordinaryRequest = Request(ordinary);
        Assert.True(Run(ordinary, ordinaryRequest, new Proof(ordinaryRequest, ordinary)).Verified);
        Assert.Equal(ordinary.Commands, platform.Commands);
        Assert.Equal(ordinary.Threads.Count, platform.Threads.Count);
        var reverse = new CpuLiveCurveNativeRequest(request.Scope, request.ImmediateReverse,
            CpuLiveCurveCommandPurpose.Restore, result.After!);
        var reverseProof = new Proof(reverse, platform) { Prepared = true, Reading = true };
        platform.OnStage = stage =>
        {
            if (stage == "invoke")
            {
                Assert.False(reverseProof.Reading);
                Assert.Equal(1, reverseProof.ReadsCompleted);
                Assert.True(reverseProof.FreshChecks > 0);
            }
        };
        var restored = Run(platform, reverse, reverseProof);
        Assert.True(restored.Verified);
        Assert.True(CpuLiveCurveNativeRequest.Equal(request.Before, restored.After));
        Assert.Equal(2, reverseProof.ReadsCompleted);
        Assert.False(reverseProof.Reading);
    }

    [Fact]
    public void ExactNativeWireReadsWholeBeforeAndAfterUnderOneLeaseAndCanRestore()
    {
        var platform = new Platform();
        var request = Request(platform);
        var proof = new Proof(request, platform);
        var result = Run(platform, request, proof);
        Assert.True(result.Verified);
        Assert.True(result.WorkerTerminal);
        Assert.True(result.CleanupComplete);
        Assert.Equal(CpuLiveCurveNativeDisposition.Acknowledged, result.Disposition);
        Assert.Equal(new uint[] { 6 }.Concat(Enumerable.Repeat(0x5Eu, 12)).Concat([0x82u, 0x53u, 6u])
            .Concat(Enumerable.Repeat(0x5Eu, 12)), platform.Commands);
        Assert.Equal(2, platform.Enumerations);
        Assert.Equal(24, platform.CoreReads);
        Assert.Equal(1, platform.Setters);
        Assert.Equal(2, proof.RangeReads);
        Assert.Equal(1, platform.Releases);
        Assert.Equal([1u, request.Command.EncodeArgument(), 0u], platform.SetterArgument!);
        Assert.Equal([1u, 0x53u, 0u], platform.SetterMessage!);
        Assert.All(platform.Threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
        Assert.True(CpuLiveCurveCommand.TryCreate(0, -1, 0xFF55, out var restore));
        var reverse = new CpuLiveCurveNativeRequest(request.Scope, restore, CpuLiveCurveCommandPurpose.Restore, result.After!);
        var restored = Run(platform, reverse, new Proof(reverse, platform));
        Assert.True(restored.Verified);
        Assert.True(CpuLiveCurveNativeRequest.Equal(request.Before, restored.After));
    }

    [Theory]
    [InlineData("clear", "NotSubmitted")]
    [InlineData("argument", "NotSubmitted")]
    [InlineData("select-message", "NotSubmitted")]
    [InlineData("invoke", "PossibleSubmission")]
    [InlineData("malformed-invoke", "PossibleSubmission")]
    [InlineData("native-false", "PossibleSubmission")]
    [InlineData("poll", "PossibleSubmission")]
    [InlineData("malformed-ack", "PossibleSubmission")]
    [InlineData("firmware-error", "PossibleSubmission")]
    [InlineData("timeout", "PossibleSubmission")]
    [InlineData("after", "Acknowledged")]
    public void BoundaryDispositionSurvivesEveryTransportFailure(string fault, string expectedDisposition)
    {
        var disposition = Enum.Parse<CpuLiveCurveNativeDisposition>(expectedDisposition);
        var platform = new Platform { Fault = fault };
        var request = Request(platform);
        var result = Run(platform, request, new Proof(request, platform));
        Assert.Equal(disposition, result.Disposition);
        Assert.False(result.Verified);
        Assert.True(result.WorkerTerminal);
        Assert.True(result.CleanupComplete);
        Assert.Equal(disposition == CpuLiveCurveNativeDisposition.NotSubmitted ? 0 : 1, platform.Setters);
        Assert.Equal(1, platform.Releases);
    }

    [Theory]
    [InlineData("clear", "NotSubmitted")]
    [InlineData("argument", "NotSubmitted")]
    [InlineData("select-message", "NotSubmitted")]
    [InlineData("invoke", "PossibleSubmission")]
    [InlineData("poll", "PossibleSubmission")]
    [InlineData("after", "Acknowledged")]
    public void CancellationNeverDowngradesAnAttemptOrAcknowledgement(string stage, string expectedDisposition)
    {
        var disposition = Enum.Parse<CpuLiveCurveNativeDisposition>(expectedDisposition);
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { OnStage = value => { if (value == stage) { cancellation.Cancel(); throw new OperationCanceledException(); } } };
        var request = Request(platform);
        var result = Run(platform, request, new Proof(request, platform), cancellation.Token);
        Assert.Equal(disposition, result.Disposition);
        Assert.False(result.Verified);
        Assert.True(result.WorkerTerminal);
        Assert.Equal(CpuLiveCurveNativeFailure.Cancelled, result.Failure);
    }

    [Fact]
    public void LastAdmissionAfterMessageSelectionCanRevokeBeforeTheNativeDataCall()
    {
        var platform = new Platform();
        var request = Request(platform);
        var proof = new Proof(request, platform);
        platform.OnStage = stage => { if (stage == "select-message") { proof.Revoked = true; } };
        var result = Run(platform, request, proof);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition);
        Assert.Equal(0, platform.Setters);
        Assert.True(result.WorkerTerminal);
    }

    [Fact]
    public void WholeVectorDriftPreservesAcknowledgementAndActualAfterWithoutOwnership()
    {
        var platform = new Platform { Fault = "drift" };
        var request = Request(platform);
        var result = Run(platform, request, new Proof(request, platform));
        Assert.Equal(CpuLiveCurveNativeDisposition.Acknowledged, result.Disposition);
        Assert.False(result.Verified);
        Assert.Equal(CpuLiveCurveNativeFailure.ReadbackDrift, result.Failure);
        Assert.Equal(24, platform.CoreReads);
        Assert.Equal((short)-9, result.After!.CoreOffsets.Single(core => core.PhysicalCoreId == 2).Offset);
    }

    [Fact]
    public void CleanupFailureDoesNotReturnAnEarlierSuccessfulResult()
    {
        var platform = new Platform { Fault = "cleanup" };
        var request = Request(platform);
        var result = Run(platform, request, new Proof(request, platform));
        Assert.Equal(CpuLiveCurveNativeDisposition.Acknowledged, result.Disposition);
        Assert.False(result.Verified);
        Assert.False(result.CleanupComplete);
        Assert.True(result.WorkerTerminal);
        Assert.Equal(CpuLiveCurveNativeFailure.Cleanup, result.Failure);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, Run(platform, request, new Proof(request, platform)).Disposition);
        Assert.Equal(1, platform.Setters);
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("mask")]
    [InlineData("fuse")]
    [InlineData("version")]
    [InlineData("root")]
    [InlineData("node")]
    [InlineData("orientation")]
    [InlineData("before")]
    public void WireRejectsUnadmittedTopologyCapabilityAndFullBefore(string fault)
    {
        var platform = new Platform { Fault = fault };
        var request = Request(platform);
        var result = Run(platform, request, new Proof(request, platform));
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition);
        Assert.Equal(0, platform.Setters);
    }

    [Theory]
    [InlineData(0x50009u, 0, 50, 1, true)]
    [InlineData(0x5000Au, -1, 50, 1, true)]
    [InlineData(0x5000Au, 0, 51, 1, true)]
    [InlineData(0x5000Au, 0, 50, 2, true)]
    [InlineData(0x5000Au, 0, 50, 1, false)]
    [InlineData(0x5000Au, 1, 50, 1, true)]
    public void IndependentlyTrustedRangeMustMeetTheStrictCoContract(uint selector, int min, int max, int step, bool supported)
    {
        var platform = new Platform();
        var request = Request(platform);
        var proof = new Proof(request, platform) { Range = new(selector, min, max, step, supported) };
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, Run(platform, request, proof).Disposition);
        Assert.Equal(0, platform.Setters);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-51)]
    [InlineData(short.MinValue)]
    public void ForwardRejectsAnUnrestorableImmediateOriginalWithoutClamping(int previous)
    {
        var platform = new Platform();
        platform.Offsets[0] = checked((short)previous);
        var request = Request(platform);
        var proof = new Proof(request, platform);
        var result = Run(platform, request, proof);
        Assert.Equal((short)previous, request.ImmediateReverse.Offset);
        Assert.Equal(unchecked((ushort)previous), request.ImmediateReverse.EncodeArgument());
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition);
        Assert.Equal(CpuLiveCurveNativeFailure.AdmissionDenied, result.Failure);
        Assert.Equal(1, proof.RangeReads);
        Assert.Equal(0, platform.Setters);
    }

    [Fact]
    public void FinalBoundaryRechecksReverseRangeEvenWhileForwardStillFits()
    {
        var platform = new Platform();
        var request = Request(platform); // forward0, exact immediate reverse-1
        var proof = new Proof(request, platform)
        {
            ReadRange = count => new(0x5000A, 0, count == 1 ? 50 : 0, 1, true),
        };
        var result = Run(platform, request, proof);
        Assert.Equal(2, proof.RangeReads);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition);
        Assert.Equal(0, platform.Setters);
    }

    [Fact]
    public void IndependentlyAdmittedRestoreChecksItsActualTargetWithoutRequiringReapplicationOfEditedState()
    {
        var platform = new Platform();
        platform.Offsets[0] = -51;
        var baseline = Request(platform);
        Assert.True(CpuLiveCurveCommand.TryCreate(0, -1, 0xFF55, out var command));
        var restore = new CpuLiveCurveNativeRequest(baseline.Scope, command, CpuLiveCurveCommandPurpose.Restore, baseline.Before);
        var result = Run(platform, restore, new Proof(restore, platform));
        Assert.True(result.Verified);
        Assert.Equal((short)-1, platform.Offsets[0]);
        Assert.Equal((short)-51, restore.ImmediateReverse.Offset);
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("pin-cleanup")]
    [InlineData("acquire-cleanup")]
    public void GetterCleanupUncertaintyParksBothFollowingGettersAndSetters(string fault)
    {
        var platform = new Platform { Fault = fault };
        var first = WindowsRz090528CpuQueryAdapter.QueryForAttendedValidation(
            CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), () => Exact, platform);
        Assert.Equal(CpuQueryStage.Cleanup, first.Diagnostic!.Stage);
        int opens = platform.Opens, calls = platform.Threads.Count;
        platform.Fault = null;
        var next = WindowsRz090528CpuQueryAdapter.QueryForAttendedValidation(
            CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), () => Exact, platform);
        Assert.Equal(CpuLiveQueryStatus.Busy, next.Status);
        var request = Request(platform);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, Run(platform, request, new Proof(request, platform)).Disposition);
        Assert.Equal(opens, platform.Opens);
        Assert.Equal(calls, platform.Threads.Count);
        Assert.Equal(0, platform.Setters);
    }

    [Fact]
    public void AcquireCleanupCancellationWithoutAReturnedLeasePreservesCleanupUncertaintyAndParksScope()
    {
        var platform = new Platform { Fault = "acquire-cleanup-cancel" };
        var request = Request(platform);
        var result = Run(platform, request, new Proof(request, platform));
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, result.Disposition);
        Assert.True(result.WorkerTerminal);
        Assert.False(result.CleanupComplete);
        Assert.False(result.Verified);
        Assert.Equal(CpuLiveCurveNativeFailure.Cleanup, result.Failure);
        Assert.Equal(0, platform.Opens);
        Assert.Equal(1, platform.Releases);
        platform.Fault = null;
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, Run(platform, request, new Proof(request, platform)).Disposition);
        Assert.Equal(CpuLiveQueryStatus.Busy, WindowsRz090528CpuQueryAdapter.QueryForAttendedValidation(
            CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), () => Exact, platform).Status);
        Assert.Equal(0, platform.Opens);
        Assert.Equal(1, platform.Releases);
    }

    [Fact]
    public void ProofIsDistinctSingleUseAndBoundToExactRequestReference()
    {
        var platform = new Platform();
        var request = Request(platform);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, Run(platform, request, null).Disposition);
        Assert.Equal(0, platform.Opens);
        var proof = new Proof(request, platform);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, Run(platform, Request(platform), proof).Disposition);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, Run(platform, request, proof).Disposition);
        Assert.Equal(0, platform.Opens);
        Assert.False(typeof(CpuLiveCurveWriteProof).IsAssignableFrom(typeof(CpuQueryAttendedApproval)));
    }

    [Fact]
    public async Task AwaiterTimeoutDoesNotReleaseTheOutstandingOperationOrAllowASecondGetterOrSetter()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var platform = new Platform { OnStage = stage => { if (stage == "invoke") { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); } } };
        var request = Request(platform);
        Task<CpuLiveCurveNativeResult> worker = Task.Run(() => Run(platform, request, new Proof(request, platform)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAsync<TimeoutException>(async () => await worker.WaitAsync(TimeSpan.FromMilliseconds(1)));
            Assert.False(worker.IsCompleted);
            Assert.True(platform.Pinned);
            Assert.True(platform.Held);
            Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, Run(platform, request, new Proof(request, platform)).Disposition);
            Assert.Equal(CpuLiveQueryStatus.Busy, WindowsRz090528CpuQueryAdapter.QueryForAttendedValidation(
                CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true), () => Exact, platform).Status);
            Assert.Equal(1, platform.Setters);
        }
        finally { finish.Set(); }
        Assert.True((await worker).Verified);
        Assert.False(platform.Pinned);
    }

    private static CpuLiveCurveNativeResult Run(Platform platform, CpuLiveCurveNativeRequest request,
        Proof? proof, CancellationToken token = default) =>
        WindowsRz090528CpuQueryAdapter.ExecuteCurveWithPlatform(request, proof, () => Exact, platform, token);

    private static CpuLiveCurveNativeRequest Request(Platform platform)
    {
        var before = CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50,
            platform.Offsets.Select(pair => new CpuLiveCoreOffset(pair.Key, pair.Value)).ToArray()))!;
        Assert.True(CpuLiveCurveCommand.TryCreate(0, 0, 0xFF55, out var command));
        return new(new(), command, CpuLiveCurveCommandPurpose.Forward, before);
    }

    private sealed class Proof(CpuLiveCurveNativeRequest request, Platform platform) : CpuLiveCurveWriteProof(request)
    {
        internal bool Revoked;
        internal bool Prepared, Reading, RejectSetter;
        internal int ReadsBegun, ReadsCompleted, FreshChecks;
        internal int RangeReads;
        internal CpuLiveCurveTrustedRange? Range = new(0x5000A, 0, 50, 1, true);
        internal Func<int, CpuLiveCurveTrustedRange?>? ReadRange;
        internal Func<bool>? SubmissionAllowed;
        internal override bool IsSubmissionCurrent(CpuLiveCurveNativeRequest exactRequest) => SubmissionAllowed?.Invoke() ?? true;
        internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest)
        {
            FreshChecks++;
            return !Reading && !RejectSetter && !Revoked;
        }
        internal override bool IsPreparationCurrent(CpuLiveCurveNativeRequest exactRequest) =>
            Prepared ? Reading && ReadsBegun == 0 && !Revoked : IsCurrent(exactRequest);
        internal override IDisposable? BeginReadCore(CpuLiveCurveNativeRequest exactRequest)
        {
            if (!Prepared) { return null; }
            if (ReadsBegun == 0) { Assert.True(Reading); }
            else { Assert.False(Reading); }
            ReadsBegun++;
            Reading = true;
            return new Cleanup(() => Reading = false);
        }
        internal override bool IsReadCurrent(CpuLiveCurveNativeRequest exactRequest) => Reading && !Revoked;
        internal override bool CompleteReadCore(CpuLiveCurveNativeRequest exactRequest)
        {
            ReadsCompleted++;
            return IsReadCurrent(exactRequest);
        }
        internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest)
        {
            Assert.True(platform.Held); Assert.True(platform.Pinned); Assert.True(platform.Opens > 0);
            RangeReads++; return ReadRange is null ? Range : ReadRange(RangeReads);
        }
    }

    private sealed class Platform : ICpuQueryWindowsPlatform, ICpuQueryDevice, ICpuQueryMutex
    {
        internal readonly SortedDictionary<int, short> Offsets = new(Enumerable.Range(0, 16).Where(CpuLiveCurveSnapshot.IsEnabled)
            .ToDictionary(id => id, id => (short)(-id - 1)));
        internal readonly List<uint> Commands = [];
        internal readonly List<int> Threads = [];
        internal uint[]? SetterArgument, SetterMessage;
        internal int Setters, Enumerations, CoreReads, Releases, Opens;
        internal bool Held, Pinned;
        internal string? Fault;
        internal Action<string>? OnStage;
        private uint selected, argument, replyValue, lastCommand;
        private bool capabilityRead;

        public HostIdentityReadResult ReadHost() => new(new("Razer", Fault is "acquire-cleanup" or "acquire-cleanup-cancel" ? "other" : "Blade 16 - RZ09-0528", "RZ09-05289EN4", "2.02"), null);
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x1A, 0x24, 0);
        public bool ControllersAbsent() => true;
        public IDisposable PinBackendFiles()
        {
            Pinned = true;
            return new Cleanup(() =>
            {
                Pinned = false;
                if (Fault == "acquire-cleanup-cancel") { throw new OperationCanceledException(); }
                if (Fault is "pin-cleanup" or "acquire-cleanup") { throw new IOException(); }
            });
        }
        public InstalledCpuBackendSnapshot ReadBackend() => new(Rz090528InstalledCpuBackendInspector.ExpectedHashes
            .Select(pair => new CpuBackendFileMetadata(pair.Key, pair.Value, true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray(),
            true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, true);
        public ICpuQueryDevice OpenExistingDevice() { Assert.True(Held); Assert.True(Pinned); Opens++; capabilityRead = false; lastCommand = 0; return this; }
        public ICpuQueryMutex CreateSharedMutex() => this;
        public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken cancellationToken) { Assert.False(Held); Held = true; return CpuQueryMutexWait.Acquired; }
        public void Release() { Assert.True(Held); Held = false; Releases++; }
        void IDisposable.Dispose() { if (Held && Fault == "cleanup") { throw new IOException(); } }

        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength)
        {
            Assert.True(Held); Assert.True(Pinned); Threads.Add(Environment.CurrentManagedThreadId);
            byte[] output = new byte[outputLength];
            if (code == 0x81112F18)
            {
                Enumerations++;
                byte[] records = CpuQueryPciTestRecords.Create(null);
                Put(output, 24, Word(records, 24));
                if (outputLength > 28) { records.CopyTo(output, 0); }
            }
            else if (code == 0x81112F24)
            {
                input.CopyTo(output, 0);
                uint offset = Word(input, 12), value;
                if (offset == 0)
                {
                    string part = Word(input, 4) == 0 ? "root" : Word(input, 8) == 0 ? "node" : "orientation";
                    value = part switch { "root" => 0x15071022, "node" => 0x16F81022, _ => 0x16F91022 };
                    if (Fault == part) { value = 0; }
                    if (Setters > 0 && lastCommand == 0x53) { Stage("after"); }
                }
                else if (offset == 0x140) { value = Fault == "mask" ? 0xFFFu : 0xFF55u; }
                else if (selected == 0x03820AB0) { value = (~(Fault == "fuse" ? 0xFFFu : 0xFF55u) & 0xFFFF) << 5; }
                else if (selected == 0x03B10A80)
                {
                    value = 1;
                    if (lastCommand == 0x53)
                    {
                        Stage("poll");
                        if (Fault == "timeout") { value = 0; }
                        if (Fault == "firmware-error") { value = 0xFE; }
                        if (Fault == "malformed-ack") { output[0] ^= 1; }
                    }
                }
                else if (selected == 0x03B10A88)
                {
                    value = replyValue;
                    if (lastCommand == 0x82) { capabilityRead = true; }
                    if (lastCommand == 0x5E) { CoreReads++; }
                }
                else { throw new InvalidOperationException(); }
                Put(output, 20, value);
            }
            else if (code == 0x81112FF8)
            {
                Assert.Equal(12, input.Length); Assert.Equal(0, outputLength); Assert.Equal(0u, Word(input, 8));
                uint value = Word(input, 4);
                if (Word(input, 0) == 0)
                {
                    if (value == 0x03B10A20 && capabilityRead && Setters == 0) { Stage("select-message"); }
                    selected = value;
                }
                else if (selected == 0x03B10A80 && capabilityRead && Setters == 0) { Stage("clear"); }
                else if (selected == 0x03B10A88)
                {
                    if (capabilityRead && Setters == 0) { Stage("argument"); SetterArgument = [Word(input, 0), value, Word(input, 8)]; }
                    argument = value;
                }
                else if (selected == 0x03B10A20)
                {
                    Commands.Add(value); lastCommand = value;
                    int id = (int)(((argument >> 28) & 1) * 8 + ((argument >> 20) & 7));
                    if (value == 0x53)
                    {
                        Setters++; SetterMessage = [Word(input, 0), value, Word(input, 8)]; Stage("invoke");
                        Offsets[id] = unchecked((short)argument);
                        if (Fault == "drift") { Offsets[2] = -9; }
                        if (Fault == "native-false") { return new(false, [], CpuQueryFailureCode.NativeIoctlFailed); }
                        if (Fault == "malformed-invoke") { return new(true, [0]); }
                    }
                    replyValue = value switch
                    {
                        6 => Fault == "version" ? 0x005D0008u : 0x005D000Bu,
                        0x82 => Fault == "capability" ? 0xE2u : 0xE6u,
                        0x5E => unchecked((ushort)(Fault == "before" && Setters == 0 ? -50 : Offsets[id])),
                        0x53 => 0,
                        _ => throw new InvalidOperationException(),
                    };
                }
            }
            else { throw new InvalidOperationException(); }
            return new(true, output);
        }
        private void Stage(string stage) { OnStage?.Invoke(stage); if (Fault == stage) { throw new IOException(); } }
    }
    private sealed class Cleanup(Action release) : IDisposable { public void Dispose() => release(); }
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
