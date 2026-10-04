using System.Buffers.Binary;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLiveCurveNativeBackendTests
{
    [Fact]
    public async Task ExistingQueryDiagnosticPreservesStageAndNativeCodeWithoutExceptionText()
    {
        var platform = new Platform { OnWait = () => throw new System.ComponentModel.Win32Exception(5, "private path") };
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        var result = await new CpuLiveCurveNativeBackend(platform, new Provider()).ObserveAsync(default, diagnostic);
        Assert.Null(result.Observation);
        Assert.Equal("native query MutexWait/Win32Error; native error 5; operation ", diagnostic.FirstFailure);
        Assert.Equal(1, platform.MutexCreates); Assert.Equal(0, platform.Opens); Assert.Equal(0, platform.Setters);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledMailboxDistinguishesPriorErrorFromCurrentRejection(bool afterSubmission)
    {
        var platform = new Platform { RetainDevice = true, MailboxError = 0xFE, ErrorAfterSubmission = afterSubmission };
        var session = WindowsRz090528CpuNativeSession.ForPlatform(platform);
        var provider = new Provider();
        using var held = session.TryRetainInstalledBackend(() => provider.Context!.Identity);
        Assert.NotNull(held);
        provider.InstalledApproval = () => held.CreateQueryApproval(() => true);
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        var result = await new CpuLiveCurveNativeBackend(session, provider).ObserveAsync(default, diagnostic);
        if (!afterSubmission)
        {
            Assert.NotNull(result.Observation); Assert.Null(diagnostic.FirstFailure);
            Assert.Equal(0, platform.Setters); return;
        }
        Assert.Null(result.Observation);
        Assert.StartsWith("native query PmVersion/InvalidData;", diagnostic.FirstFailure);
        Assert.Contains("current mailbox " + (afterSubmission ? "ResponseWait" : "ReadinessWait"), diagnostic.FirstFailure);
        Assert.Contains("rejected mailbox response 0x000000FE", diagnostic.FirstFailure);
        Assert.Contains("submission attempted/completed " + (afterSubmission ? "True/True" : "False/False"), diagnostic.FirstFailure);
        Assert.Equal(0, platform.Setters); Assert.Null(result.Context);
    }

    [Theory]
    [InlineData("2.03")]
    [InlineData("Unknown")]
    public async Task BiosProvenanceChangeDoesNotInvalidateTheOtherwiseExactFreshContext(string bios)
    {
        var platform = new Platform(); var provider = new Provider();
        var expected = provider.Context!;
        provider.Context = expected with { Identity = expected.Identity with { BiosVersion = bios } };
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        Assert.True(await backend.CheckWriteAdmissionAsync(expected, Command(0), CpuLiveCurveCommandPurpose.Forward, default));
        Assert.Equal(CpuLiveCurveSubmission.Acknowledged,
            await backend.SendAsync(expected, Command(0), CpuLiveCurveCommandPurpose.Forward, Snapshot(), default));
        Assert.Equal(1, platform.Setters);
        Assert.True(backend.Status.LastNativeResult!.Verified);
    }

    [Fact]
    public async Task ActualProtocolCompositionObservesAdmitsWritesAndRestoresFullMixedVector()
    {
        var platform = new Platform();
        var provider = new Provider();
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        var before = await backend.ObserveAsync(default);
        Assert.Equal(provider.Context, before.Context);
        Assert.NotNull(before.Observation);
        Assert.Equal(1, provider.QueryApprovals);
        Assert.True(await backend.CheckWriteAdmissionAsync(provider.Context!, Command(0), CpuLiveCurveCommandPurpose.Forward, default));
        Assert.Equal(CpuLiveCurveSubmission.Acknowledged, await Send(backend, provider));
        Assert.Equal(1, platform.Setters);
        Assert.Equal(0, platform.Values[0]);
        Assert.True(backend.Status.LastNativeResult!.Verified);
        Assert.False(backend.Status.ScopeInvalid);
        Assert.Null(backend.OutstandingWorker);
        Assert.NotNull(provider.LastRequest);
        Assert.Equal((short)-1, provider.LastRequest!.ImmediateReverse.Offset);
        var after = CpuLiveCurveSnapshot.TryCreate(await Observation(backend))!;
        Assert.Equal(CpuLiveCurveSubmission.Acknowledged, await backend.SendAsync(provider.Context!, Command(-1),
            CpuLiveCurveCommandPurpose.Restore, after, default));
        Assert.True(Snapshot().CoreOffsets.SequenceEqual(platform.Values.Select(pair => new CpuLiveCoreOffset(pair.Key, pair.Value))));
        Assert.Equal(2, platform.Setters);
        Assert.All(platform.LeaseThreads, lease => Assert.Single(lease.Distinct()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown-source")]
    [InlineData("undefined-source")]
    [InlineData("missing-counter")]
    [InlineData("backend")]
    public async Task UntrustedOrMalformedContextCannotObtainGetterApprovalOrTouchPlatform(string fault)
    {
        var platform = new Platform();
        var provider = new Provider();
        provider.Context = fault switch
        {
            "missing" => null,
            "unknown-source" => provider.Context! with { BootSequence = new(CpuLiveCurveBootSequenceSource.Unknown, 42) },
            "undefined-source" => provider.Context! with { BootSequence = new((CpuLiveCurveBootSequenceSource)99, 42) },
            "missing-counter" => provider.Context! with { BootSequence = new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, null) },
            _ => provider.Context! with { Backend = provider.Context!.Backend with { DriverSha256 = "other" } },
        };
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        Assert.Null((await backend.ObserveAsync(default)).Observation);
        Assert.Equal(0, provider.QueryApprovals);
        Assert.Equal(0, platform.Opens);
        Assert.Equal(0, platform.MutexCreates);
    }

    [Fact]
    public async Task KnownSourceCounterZeroIsNotConfusedWithUnknownSource()
    {
        var provider = new Provider();
        provider.Context = provider.Context! with { BootSequence = new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, 0) };
        Assert.NotNull((await new CpuLiveCurveNativeBackend(new Platform(), provider).ObserveAsync(default)).Observation);
    }

    [Fact]
    public async Task GetterApprovalMustComeFromProviderAndIsNeverMintedByBackend()
    {
        var platform = new Platform();
        var backend = new CpuLiveCurveNativeBackend(platform, new Provider { QueryAllowed = false });
        Assert.Null((await backend.ObserveAsync(default)).Observation);
        Assert.Equal(0, platform.Opens);
        Assert.False(backend.Status.ScopeInvalid);
    }

    [Fact]
    public async Task AlreadyConsumedInnerProofCannotAcquireNewAuthorityThroughANewContextWrapper()
    {
        var platform = new Platform();
        var provider = new Provider { ReuseProof = true };
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        Assert.Equal(CpuLiveCurveSubmission.Acknowledged, await Send(backend, provider));
        int opens = platform.Opens;
        var after = Snapshot().WithOffset(Command(0));
        Assert.Equal(CpuLiveCurveSubmission.NotSubmitted, await backend.SendAsync(provider.Context!, Command(-1),
            CpuLiveCurveCommandPurpose.Restore, after, default));
        Assert.Equal(2, provider.Proofs);
        Assert.Equal(opens, platform.Opens);
        Assert.Equal(1, platform.Setters);
        Assert.Equal((short)0, platform.Values[0]);
    }

    [Theory]
    [InlineData("counter", "acquire")]
    [InlineData("backend", "acquire")]
    [InlineData("counter", "boundary")]
    [InlineData("backend", "boundary")]
    [InlineData("counter", "range")]
    public async Task BoundProofRechecksContextUnderLeaseEvenWhenInnerProofAlwaysClaimsCurrent(string change, string when)
    {
        var platform = new Platform();
        var provider = new Provider();
        var original = provider.Context!;
        Action drift = () => provider.Context = Change(original, change);
        if (when == "acquire") { platform.OnWait = drift; }
        if (when == "boundary") { platform.BeforeSetterSelection = drift; }
        if (when == "range") { provider.OnRange = drift; }
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        var result = await Send(backend, provider, original);
        Assert.NotEqual(CpuLiveCurveSubmission.Acknowledged, result);
        Assert.Equal(0, platform.Setters);
        Assert.True(provider.Proofs > 0);
    }

    [Fact]
    public async Task ContextDriftAfterCompleteGetterRejectsObservationAndParksScope()
    {
        var platform = new Platform();
        var provider = new Provider();
        platform.OnCoreRead = count => { if (count == 12) { provider.Context = Change(provider.Context!, "counter"); } };
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        Assert.Null((await backend.ObserveAsync(default)).Observation);
        Assert.True(backend.Status.ScopeInvalid);
        Assert.Equal(12, platform.CoreReads);
        Assert.False(platform.Pinned);
    }

    [Fact]
    public async Task AdmissionDelegatesExactCommandPurposeAndRechecksContextAfterProvider()
    {
        var provider = new Provider();
        var original = provider.Context!;
        provider.OnAdmission = () => provider.Context = Change(original, "counter");
        var platform = new Platform();
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        Assert.False(await backend.CheckWriteAdmissionAsync(original, Command(-1), CpuLiveCurveCommandPurpose.Restore, default));
        Assert.Equal((Command(-1), CpuLiveCurveCommandPurpose.Restore), Assert.Single(provider.Admitted));
        Assert.Equal(0, platform.Opens);
    }

    [Theory]
    [InlineData("before", "NotSubmitted", true)]
    [InlineData("invoke", "Unknown", false)]
    [InlineData("malformed-ack", "Unknown", false)]
    [InlineData("drift", "Unknown", false)]
    [InlineData("cleanup", "Unknown", true)]
    [InlineData("acquire-cleanup-cancel", "Unknown", true)]
    public async Task ActualProtocolResultsMapConservatively(string fault, string expected, bool invalid)
    {
        var platform = new Platform { Fault = fault };
        var provider = new Provider();
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        Assert.Equal(Enum.Parse<CpuLiveCurveSubmission>(expected), await Send(backend, provider));
        Assert.Equal(invalid, backend.Status.ScopeInvalid);
        Assert.True(backend.Status.LastNativeResult!.WorkerTerminal);
        Assert.Null(backend.OutstandingWorker);
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("acquire-cleanup-cancel")]
    public async Task GetterCleanupFailureCannotBeReusedThroughSameOrNewBackend(string fault)
    {
        var platform = new Platform { Fault = fault };
        var provider = new Provider();
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        Assert.Null((await backend.ObserveAsync(default)).Observation);
        Assert.True(backend.Status.ScopeInvalid);
        int opens = platform.Opens;
        platform.Fault = null;
        var replacement = new CpuLiveCurveNativeBackend(platform, new Provider());
        Assert.Null((await replacement.ObserveAsync(default)).Observation);
        Assert.False(await replacement.CheckWriteAdmissionAsync(provider.Context!, Command(0), CpuLiveCurveCommandPurpose.Forward, default));
        Assert.Equal(CpuLiveCurveSubmission.Unknown, await Send(replacement, provider));
        Assert.Equal(opens, platform.Opens);
    }

    [Fact]
    public async Task PostSendContextDriftCannotPromoteAcknowledgementToOwnership()
    {
        var provider = new Provider();
        var platform = new Platform();
        platform.OnCoreRead = count => { if (count == 24) { provider.Context = Change(provider.Context!, "counter"); } };
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        Assert.Equal(CpuLiveCurveSubmission.Unknown, await Send(backend, provider));
        Assert.Equal(CpuLiveCurveNativeDisposition.Acknowledged, backend.Status.LastNativeResult!.Disposition);
        Assert.False(backend.Status.LastNativeResult.Verified);
        Assert.True(backend.Status.ScopeInvalid);
    }

    [Theory]
    [InlineData("caller-cancel")]
    [InlineData("awaiter-timeout")]
    public async Task BlockedActualNativeWorkerRetainsResourcesAndExcludesAllOtherOperations(string interruption)
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { OnSetter = () => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); } };
        var provider = new Provider();
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        Task<CpuLiveCurveSubmission> caller = Send(backend, provider, token: cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Task worker = Assert.IsAssignableFrom<Task>(backend.OutstandingWorker);
            if (interruption == "caller-cancel")
            { cancellation.Cancel(); Assert.False(caller.IsCompleted); }
            else { await Assert.ThrowsAsync<TimeoutException>(async () => await caller.WaitAsync(TimeSpan.FromMilliseconds(1))); }
            Assert.False(worker.IsCompleted);
            Assert.Same(worker, backend.OutstandingWorker);
            Assert.True(platform.Held); Assert.True(platform.Pinned);
            await AssertAllRefused(backend, provider);
            await AssertAllRefused(new CpuLiveCurveNativeBackend(platform, new Provider()), provider);
            await AssertAllRefused(new CpuLiveCurveNativeBackend(
                WindowsRz090528CpuNativeSession.ForPlatform(platform), new Provider()), provider);
            Assert.Equal(1, platform.Setters);
        }
        finally { finish.Set(); }
        await AwaitTerminal(backend);
        if (interruption == "awaiter-timeout") { Assert.Equal(CpuLiveCurveSubmission.Acknowledged, await caller); }
        else { Assert.Equal(CpuLiveCurveSubmission.Unknown, await caller); }
        Assert.False(platform.Held); Assert.False(platform.Pinned);
    }

    [Fact]
    public async Task CancelledPreSetterWorkerReturnsTerminalNonSubmissionWithoutFreshContext()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { OnWait = () => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); } };
        var provider = new Provider();
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        Task<CpuLiveCurveSubmission> caller = Send(backend, provider, token: cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel(); provider.Context = null;
            Assert.False(caller.IsCompleted);
            Assert.NotNull(backend.OutstandingWorker);
        }
        finally { finish.Set(); }
        Assert.Equal(CpuLiveCurveSubmission.NotSubmitted, await caller.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(backend.OutstandingWorker); Assert.True(backend.Status.ScopeInvalid);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, backend.Status.LastNativeResult!.Disposition);
        Assert.True(backend.Status.LastNativeResult.WorkerTerminal); Assert.True(backend.Status.LastNativeResult.CleanupComplete);
        Assert.Equal(0, platform.Setters); Assert.False(platform.Held); Assert.False(platform.Pinned);
    }

    [Fact]
    public async Task LateProviderRefusalNeedsNoNativeResultToProveTerminalNonSubmission()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform();
        var provider = new Provider
        {
            AdmissionAllowed = false,
            OnAdmission = () => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); },
        };
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        var caller = Send(backend, provider, token: cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel(); Assert.False(caller.IsCompleted);
        }
        finally { finish.Set(); }
        Assert.Equal(CpuLiveCurveSubmission.NotSubmitted, await caller.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(backend.OutstandingWorker); Assert.Null(backend.Status.LastNativeResult);
        Assert.Equal(0, platform.Opens); Assert.Equal(0, platform.Setters);
    }

    [Fact]
    public async Task BlockingProviderIsAlsoAnOwnedWorkerAndLateExceptionIsObservedAndParksScope()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var provider = new Provider { OnAdmission = () => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); throw new IOException(); } };
        var platform = new Platform();
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        var caller = backend.CheckWriteAdmissionAsync(provider.Context!, Command(0), CpuLiveCurveCommandPurpose.Forward, cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await caller);
            Assert.Equal(CpuLiveCurveNativeWorkerKind.Admission, backend.Status.WorkerKind);
            await AssertAllRefused(backend, provider);
            Assert.Equal(0, platform.Opens);
        }
        finally { finish.Set(); }
        await AwaitTerminal(backend);
        Assert.True(backend.Status.ScopeInvalid);
    }

    [Fact]
    public async Task RealTransactionTimeoutRetainsPossibleSubmissionAfterTheOriginalWorkerTerminates()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var platform = new Platform { OnSetter = () => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); } };
        var provider = new Provider();
        var backend = new CpuLiveCurveNativeBackend(platform, provider);
        var store = new CpuLiveCurveTuningTransactionTests.MemoryStore();
        var transaction = new CpuLiveCurveTuningTransaction(backend, store, TimeSpan.FromMilliseconds(250));
        var caller = transaction.ApplySelectiveAsync([new(0, 0)], default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await caller).Outcome);
            Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, store.Journal!.State);
            Assert.NotNull(backend.OutstandingWorker);
            Assert.True(platform.Pinned);
            Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.RestoreAsync(default)).Outcome);
        }
        finally { finish.Set(); }
        await AwaitTerminal(backend);
        Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, store.Journal!.State);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.ObserveOwnershipAsync(default)).Outcome);
        Assert.Equal(1, platform.Setters);
        Assert.Equal(0, store.Journal.CompletedSteps);
    }

    [Theory]
    [InlineData("NotSubmitted", false, true, false, "Unknown")]
    [InlineData("NotSubmitted", true, false, false, "Unknown")]
    [InlineData("NotSubmitted", true, true, false, "NotSubmitted")]
    [InlineData("PossibleSubmission", true, true, false, "Unknown")]
    [InlineData("Acknowledged", false, true, true, "Unknown")]
    [InlineData("Acknowledged", true, false, true, "Unknown")]
    [InlineData("Acknowledged", true, true, false, "Unknown")]
    [InlineData("Acknowledged", true, true, true, "Acknowledged")]
    public void ResultMappingRequiresIndependentTerminalCleanupAndVerificationFacts(string disposition, bool terminal,
        bool cleanup, bool verified, string expected)
    {
        var request = new CpuLiveCurveNativeRequest(new(), Command(0), CpuLiveCurveCommandPurpose.Forward, Snapshot());
        var result = new CpuLiveCurveNativeResult(Enum.Parse<CpuLiveCurveNativeDisposition>(disposition), terminal, cleanup,
            verified, request.After, CpuLiveCurveNativeFailure.None);
        Assert.Equal(Enum.Parse<CpuLiveCurveSubmission>(expected), CpuLiveCurveNativeBackend.MapSubmission(request, result));
        if (disposition == "Acknowledged")
        {
            Assert.Equal(CpuLiveCurveSubmission.Unknown, CpuLiveCurveNativeBackend.MapSubmission(request, result with { After = request.Before }));
            Assert.Equal(CpuLiveCurveSubmission.Unknown, CpuLiveCurveNativeBackend.MapSubmission(request, result with { Failure = CpuLiveCurveNativeFailure.Transport }));
        }
    }

    private static async Task AssertAllRefused(CpuLiveCurveNativeBackend backend, Provider provider)
    {
        Assert.Null((await backend.ObserveAsync(default)).Observation);
        Assert.False(await backend.CheckWriteAdmissionAsync(provider.Context!, Command(0), CpuLiveCurveCommandPurpose.Forward, default));
        Assert.Equal(CpuLiveCurveSubmission.Unknown, await Send(backend, provider));
    }
    private static async Task AwaitTerminal(CpuLiveCurveNativeBackend backend)
    {
        var worker = backend.OutstandingWorker;
        if (worker is not null) { await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
        // Actual worker completion precedes the lifecycle continuation; wait only
        // for that bounded bookkeeping, never by starting another operation.
        Assert.True(SpinWait.SpinUntil(() => !backend.Status.WorkerOutstanding, TimeSpan.FromSeconds(10)));
    }
    private static Task<CpuLiveCurveSubmission> Send(CpuLiveCurveNativeBackend backend, Provider provider,
        CpuLiveCurveContext? context = null, CancellationToken token = default) =>
        backend.SendAsync(context ?? provider.Context!, Command(0), CpuLiveCurveCommandPurpose.Forward, Snapshot(), token);
    private static async Task<Rz090528CpuLiveObservation?> Observation(CpuLiveCurveNativeBackend backend) => (await backend.ObserveAsync(default)).Observation;
    private static CpuLiveCurveSnapshot Snapshot() => CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50,
        Enumerable.Range(0, 16).Where(CpuLiveCurveSnapshot.IsEnabled).Select(id => new CpuLiveCoreOffset(id, (short)(-id - 1))).ToArray()))!;
    private static CpuLiveCurveCommand Command(int offset) { Assert.True(CpuLiveCurveCommand.TryCreate(0, offset, 0xFF55, out var result)); return result; }
    private static CpuLiveCurveContext Change(CpuLiveCurveContext context, string change) => change == "counter"
        ? context with { BootSequence = context.BootSequence with { Sequence = context.BootSequence.Sequence + 1 } }
        : context with { Backend = context.Backend with { DriverSha256 = "other" } };

    private sealed class Provider : ICpuLiveCurveTrustedNativeProvider
    {
        internal volatile CpuLiveCurveContext? Context = CpuLiveCurveTuningTransactionTests.ContextFixture();
        internal bool QueryAllowed = true;
        internal bool AdmissionAllowed = true;
        internal Func<CpuQueryInstalledApproval?>? InstalledApproval;
        internal bool ReuseProof;
        internal int QueryApprovals, Proofs;
        internal CpuLiveCurveNativeRequest? LastRequest;
        internal Action? OnAdmission, OnRange;
        internal readonly List<(CpuLiveCurveCommand, CpuLiveCurveCommandPurpose)> Admitted = [];
        private CpuLiveCurveWriteProof? cachedProof;
        public CpuLiveCurveContext? ReadFreshContext(CancellationToken token) => Context;
        public CpuQueryInstalledApproval? CreateInstalledQueryApproval(CpuLiveCurveContext context, CancellationToken token) => InstalledApproval?.Invoke();
        public CpuQueryAttendedApproval? CreateQueryApproval(CpuLiveCurveContext exactContext, CancellationToken token)
        { QueryApprovals++; return QueryAllowed ? CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true) : null; }
        public bool CheckWriteAdmission(CpuLiveCurveContext exactContext, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CancellationToken token)
        { Admitted.Add((command, purpose)); OnAdmission?.Invoke(); return AdmissionAllowed; }
        public CpuLiveCurveWriteProof? CreateWriteProof(CpuLiveCurveContext exactContext, CpuLiveCurveNativeRequest exactRequest, CancellationToken token)
        {
            Proofs++; LastRequest = exactRequest;
            if (ReuseProof && cachedProof is not null) { return cachedProof; }
            return cachedProof = new Proof(exactRequest, this);
        }
        private sealed class Proof(CpuLiveCurveNativeRequest request, Provider provider) : CpuLiveCurveWriteProof(request)
        {
            // Deliberately permissive offline fake: backend context checks must
            // independently catch changes even if this proof remains current.
            internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest) => true;
            internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest)
            { provider.OnRange?.Invoke(); return new(0x5000A, 0, 50, 1, true); }
        }
    }

    private sealed class Platform : ICpuQueryWindowsPlatform, ICpuQueryDevice, ICpuQueryMutex
    {
        internal readonly SortedDictionary<int, short> Values = new(Snapshot().CoreOffsets.ToDictionary(core => core.PhysicalCoreId, core => core.Offset));
        internal readonly List<List<int>> LeaseThreads = [];
        internal int Opens, MutexCreates, Setters, CoreReads;
        internal bool Held, Pinned;
        internal uint MailboxError;
        internal bool ErrorAfterSubmission, RetainDevice;
        internal string? Fault;
        internal Action? OnWait, BeforeSetterSelection, OnSetter;
        internal Action<int>? OnCoreRead;
        private uint selected, argument, replyValue, lastCommand;
        private bool capabilityRead;
        public HostIdentityReadResult ReadHost() => new(new("Razer", Fault == "acquire-cleanup-cancel" ? "other" : "Blade 16 - RZ09-0528", "RZ09-05289EN4", "2.02"), null);
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x1A, 0x24, 0);
        public bool ControllersAbsent() => true;
        public IDisposable PinBackendFiles()
        {
            Pinned = true;
            return new Cleanup(() => { Pinned = false; if (Fault == "acquire-cleanup-cancel") { throw new OperationCanceledException(); } });
        }
        public InstalledCpuBackendSnapshot ReadBackend() => new(Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair =>
            new CpuBackendFileMetadata(pair.Key, pair.Value, true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray(),
            true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, true);
        public ICpuQueryDevice OpenExistingDevice() { Assert.True(Held); Assert.True(Pinned); Opens++; capabilityRead = false; lastCommand = 0; return RetainDevice ? new RetainedDevice(this) : this; }
        private sealed class RetainedDevice(Platform owner) : ICpuQueryRetainableDevice
        {
            public ICpuQueryDevice Duplicate() => new RetainedDevice(owner);
            public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength) => owner.Invoke(code, input, outputLength);
            public void Dispose() { }
        }
        public ICpuQueryMutex CreateSharedMutex() { MutexCreates++; return this; }
        public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken cancellationToken)
        { Assert.False(Held); Held = true; LeaseThreads.Add([Environment.CurrentManagedThreadId]); OnWait?.Invoke(); return CpuQueryMutexWait.Acquired; }
        public void Release() { Assert.True(Held); LeaseThreads[^1].Add(Environment.CurrentManagedThreadId); Held = false; }
        void IDisposable.Dispose() { if (Held && Fault == "cleanup") { throw new IOException(); } }
        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength)
        {
            Assert.True(Held); Assert.True(Pinned); LeaseThreads[^1].Add(Environment.CurrentManagedThreadId);
            var output = new byte[outputLength];
            if (code == 0x81112F18)
            {
                Put(output, 24, 3);
                if (outputLength > 28)
                {
                    Put(output, 40, 0x15071022);
                    Put(output, 84, 0x18); Put(output, 92, 0x16F81022);
                    Put(output, 136, 0x18); Put(output, 140, 1); Put(output, 144, 0x16F91022);
                }
            }
            else if (code == 0x81112F24)
            {
                input.CopyTo(output, 0);
                uint offset = Word(input, 12);
                uint value = offset == 0 ? Word(input, 4) == 0 ? 0x15071022u : Word(input, 8) == 0 ? 0x16F81022u : 0x16F91022u
                    : offset == 0x140 ? 0xFF55u : selected switch
                    {
                        0x03820AB0 => (~0xFF55u & 0xFFFF) << 5,
                        0x03B10A80 => MailboxError != 0 && (ErrorAfterSubmission ? lastCommand != 0 : lastCommand == 0) ? MailboxError : 1,
                        0x03B10A88 => replyValue,
                        _ => throw new InvalidOperationException(),
                    };
                if (offset == 0xC8 && selected == 0x03B10A80 && lastCommand == 0x53 && Fault == "malformed-ack") { output[0] ^= 1; }
                if (offset == 0xC8 && selected == 0x03B10A88)
                {
                    if (lastCommand == 0x82) { capabilityRead = true; }
                    if (lastCommand == 0x5E) { OnCoreRead?.Invoke(++CoreReads); }
                }
                Put(output, 20, value);
            }
            else if (code == 0x81112FF8)
            {
                Assert.Equal(12, input.Length); Assert.Equal(0, outputLength); Assert.Equal(0u, Word(input, 8));
                uint value = Word(input, 4);
                if (Word(input, 0) == 0)
                {
                    if (value == 0x03B10A20 && capabilityRead && lastCommand == 0x82) { BeforeSetterSelection?.Invoke(); }
                    selected = value;
                }
                else if (selected == 0x03B10A88) { argument = value; }
                else if (selected == 0x03B10A20)
                {
                    lastCommand = value;
                    int id = (int)(((argument >> 28) & 1) * 8 + ((argument >> 20) & 7));
                    if (value == 0x53)
                    {
                        Setters++; OnSetter?.Invoke();
                        if (Fault == "invoke") { throw new IOException(); }
                        Values[id] = unchecked((short)argument);
                        if (Fault == "drift") { Values[2] = -9; }
                    }
                    replyValue = value switch
                    {
                        6 => 0x005D000B,
                        0x82 => 0xE6,
                        0x5E => unchecked((ushort)(Fault == "before" && Setters == 0 ? -50 : Values[id])),
                        0x53 => 0,
                        _ => throw new InvalidOperationException(),
                    };
                }
            }
            else { throw new InvalidOperationException(); }
            return new(true, output);
        }
    }
    private sealed class Cleanup(Action release) : IDisposable { public void Dispose() => release(); }
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
