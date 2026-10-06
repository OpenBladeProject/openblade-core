using System.Buffers.Binary;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLivePowerNativeBackendTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(0xFDu)]
    [InlineData(2u)]
    public async Task InstalledEmptyWindowGetsOneFreshCompleteObservation(uint secondResponse)
    {
        var platform = new Platform { RefreshResponse = 0xFD, NextRefreshResponse = secondResponse };
        var provider = new Provider(platform) { InstalledMetadata = new Metadata(), FreshMetadata = true, GetterElapsed = () => TimeSpan.Zero };
        var backend = new CpuLivePowerNativeBackend(platform, provider);
        var store = new CpuLivePowerTuningTransactionTests.MemoryStore();
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        try
        {
            var result = await Controller(backend, store).ObserveLiveAsync(_ => true, default, diagnostic: diagnostic);
            bool recovered = secondResponse == 1;
            Assert.Equal(recovered ? CpuTuningRecoveryState.None : CpuTuningRecoveryState.Blocked, result.LiveRecovery!.State);
            Assert.Equal(recovered, result.LiveState?.PowerLimits is not null);
            if (recovered) { Assert.Null(diagnostic.FirstFailure); }
            else { Assert.Contains($"mailbox status 0x{secondResponse:X8}", diagnostic.FirstFailure!); }
            Assert.Contains(EmptyWindowFailure, diagnostic.LiveObservationReason!);
            Assert.Contains(recovered ? "recovery succeeded" : "recovery rejected", diagnostic.LiveObservationReason!);
            Assert.Contains("first getter; power phases total ", diagnostic.LiveObservationReason!);
            Assert.Contains("replacement getter; power phases total ", diagnostic.LiveObservationReason!);
            Assert.Contains("CompleteGetter=", diagnostic.LiveObservationReason!);
            Assert.Null(diagnostic.PowerTimingSummary);
            Assert.Equal(2, platform.Commands.Count(command => command == 0x65));
            Assert.Equal(1, platform.Opens); Assert.Equal(2, platform.Duplicates); Assert.Equal(0, platform.Arms);
            Assert.Equal(1, platform.PriorRejectedRefreshReadiness);
            Assert.NotSame(provider.InstalledApprovals[0], provider.InstalledApprovals[1]);
            Assert.Same(provider.InstalledApprovals[0]!.Lease, provider.InstalledApprovals[1]!.Lease);
            Assert.NotSame(provider.MetadataReaders[0], provider.MetadataReaders[1]);
            Assert.NotEqual(provider.GetterTokens[0], provider.GetterTokens[1]);
            Assert.Equal(2, provider.GetterBegins); Assert.Equal(2, provider.GetterDisposals);
            Assert.Equal(recovered ? 2 : 1, provider.GetterCompletions);
            Assert.Equal(0, platform.Setters); Assert.Equal(0, store.Writes);
            Assert.False(backend.Status.ScopeInvalid); Assert.Null(backend.OutstandingWorker);
        }
        finally { provider.InstalledLease?.Dispose(); }
    }

    [Theory]
    [InlineData(false, false, 0xFDu)]
    [InlineData(true, true, 0xFDu)]
    [InlineData(true, false, 2u)]
    public async Task EmptyWindowRecoveryRequiresExactInstalledWireFailure(bool installed, bool corruptEcho, uint response)
    {
        var platform = new Platform { RefreshResponse = response, CorruptRefreshEcho = corruptEcho };
        var provider = new Provider(platform) { InstalledMetadata = installed ? new Metadata() : null };
        var backend = new CpuLivePowerNativeBackend(platform, provider, new Metadata());
        try
        {
            var observed = await backend.ObserveAsync(default);
            Assert.Null(observed.Observation); Assert.Null(observed.RecoveryTicket);
            Assert.Equal(1, platform.Commands.Count(command => command == 0x65));
            Assert.Equal(installed ? 1 : 0, platform.Duplicates); Assert.Equal(0, platform.Setters);
        }
        finally { provider.InstalledLease?.Dispose(); }
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("final-context")]
    [InlineData("dispose")]
    [InlineData("cancel")]
    [InlineData("deadline")]
    public async Task EmptyWindowRecoveryRequiresTerminalCleanupAndActiveSequence(string fault)
    {
        var platform = new Platform
        {
            RefreshResponse = 0xFD,
            NextRefreshResponse = 1,
            CleanupFault = fault == "cleanup" ? "device" : null
        };
        var provider = new Provider(platform)
        {
            InstalledMetadata = new Metadata(),
            FreshMetadata = true,
            FinalAllowed = fault != "final-context"
        };
        var backend = new CpuLivePowerNativeBackend(platform, provider);
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        using var cancellation = new CancellationTokenSource();
        provider.OnGetterDispose = () =>
        {
            if (fault == "dispose") { throw new IOException("Getter disposal failed."); }
            if (fault == "cancel") { cancellation.CancelAfter(50); }
        };
        try
        {
            var result = await Controller(backend, new(), fault == "deadline" ? TimeSpan.FromMilliseconds(150) : null)
                .ObserveLiveAsync(_ => true, cancellation.Token, diagnostic: diagnostic);
            Assert.Equal(CpuTuningRecoveryState.Blocked, result.LiveRecovery!.State);
            Assert.Equal(1, platform.Commands.Count(command => command == 0x65));
            Assert.Equal(1, provider.GetterDisposals); Assert.Equal(0, platform.Setters);
            if (fault is "cancel" or "deadline")
            { Assert.Contains(fault == "cancel" ? "caller cancellation" : "operation timeout", diagnostic.FirstFailure!); }
            else { Assert.True(backend.Status.ScopeInvalid); Assert.NotNull(diagnostic.FirstFailure); }
        }
        finally { platform.CleanupFault = null; provider.InstalledLease?.Dispose(); }
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("runtime")]
    [InlineData("metadata")]
    [InlineData("approval")]
    [InlineData("missing-metadata")]
    public async Task RecoveryTicketIsOneUseAndRejectsChangedAnchorBeforeNativeTraffic(string drift)
    {
        var platform = new Platform { RefreshResponse = 0xFD, NextRefreshResponse = 1 };
        var provider = new Provider(platform)
        {
            InstalledMetadata = new Metadata(),
            FreshMetadata = drift != "metadata",
            DenyRecoveryApproval = drift == "approval",
            DenyRecoveryMetadata = drift == "missing-metadata"
        };
        var backend = new CpuLivePowerNativeBackend(platform, provider);
        using var firstToken = new CancellationTokenSource(); using var nextToken = new CancellationTokenSource();
        try
        {
            var first = await backend.ObserveAsync(firstToken.Token);
            Assert.Null(first.Context); Assert.Null(first.Observation);
            var ticket = Assert.IsType<CpuLivePowerRecoveryTicket>(first.RecoveryTicket);
            Assert.Equal(1, provider.GetterCompletions); Assert.Equal(1, provider.GetterDisposals);
            Assert.Null(backend.OutstandingWorker);
            Assert.True(ticket.TryConsume()); Assert.False(ticket.TryConsume());
            if (drift == "boot") { provider.Context = provider.Context with { BootSequence = provider.Context.BootSequence with { Sequence = 43 } }; }
            if (drift == "runtime") { provider.Context = provider.Context with { RuntimeLifetime = new(Guid.NewGuid(), 0, 0) }; }
            var diagnostic = new CpuSavedAdmissionDiagnostic();
            Assert.Null((await backend.ObserveAsync(nextToken.Token, diagnostic, ticket)).Observation);
            Assert.NotNull(diagnostic.FirstFailure);
            Assert.Null((await backend.ObserveAsync(nextToken.Token, diagnostic, ticket)).Observation);
            Assert.Equal(2, provider.GetterBegins); Assert.Equal(1, platform.Commands.Count(command => command == 0x65));
            Assert.Equal(0, platform.Setters);
        }
        finally { provider.InstalledLease?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyFirstGetterPreservesOwnedJournalAndReplacementClassifiesDrift(bool contextDrift)
    {
        var store = new CpuLivePowerTuningTransactionTests.MemoryStore();
        var seed = new CpuLivePowerTuningTransactionTests.FakeBackend(store);
        Assert.Equal(CpuLivePowerOutcome.Verified, (await new CpuLivePowerTuningTransaction(seed, store)
            .ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default)).Outcome);
        var journal = store.Journal; int writes = store.Writes;
        var platform = new Platform { RefreshResponse = 0xFD, NextRefreshResponse = 1 };
        var provider = new Provider(platform) { InstalledMetadata = new Metadata(), FreshMetadata = true, GetterElapsed = () => TimeSpan.Zero };
        if (contextDrift) { provider.Context = provider.Context with { BootSequence = provider.Context.BootSequence with { Sequence = 43 } }; }
        provider.OnGetterDispose = () => { Assert.Same(journal, store.Journal); Assert.Equal(writes, store.Writes); };
        var backend = new CpuLivePowerNativeBackend(platform, provider); var diagnostic = new CpuSavedAdmissionDiagnostic();
        try
        {
            var result = await Controller(backend, store).ObserveLiveAsync(_ => true, default, diagnostic: diagnostic);
            Assert.Equal(CpuTuningRecoveryState.Blocked, result.LiveRecovery!.State);
            Assert.Contains(EmptyWindowFailure, diagnostic.LiveObservationReason!);
            Assert.Contains(contextDrift ? "power transaction ContextMismatch" : "power transaction StateChanged", diagnostic.LiveObservationReason!);
            Assert.Equal(CpuLivePowerOwnershipState.Blocked, store.Journal!.State);
            Assert.Equal(writes + 1, store.Writes); Assert.Equal(2, provider.GetterBegins); Assert.Equal(0, platform.Setters);
        }
        finally { provider.InstalledLease?.Dispose(); }
    }

    [Fact]
    public async Task MutationPreparationNeverConsumesEmptyWindowTicket()
    {
        var platform = new Platform { RefreshResponse = 0xFD, NextRefreshResponse = 1 };
        var provider = new Provider(platform) { InstalledMetadata = new Metadata(), FreshMetadata = true, GetterElapsed = () => TimeSpan.Zero };
        var store = new CpuLivePowerTuningTransactionTests.MemoryStore();
        try
        {
            var result = await new CpuLivePowerTuningTransaction(new CpuLivePowerNativeBackend(platform, provider), store)
                .PrepareApplyAsync([new(CpuLivePowerLimit.Fast, 90)], default);
            Assert.Null(result.Operation); Assert.Equal(1, provider.GetterBegins);
            Assert.Equal(1, platform.Commands.Count(command => command == 0x65)); Assert.Equal(0, platform.Setters); Assert.Equal(0, store.Writes);
        }
        finally { provider.InstalledLease?.Dispose(); }
    }

    [Fact]
    public async Task LateGetterDisposalCannotPublishRecoveryAfterSequenceTimeout()
    {
        var platform = new Platform { RefreshResponse = 0xFD, NextRefreshResponse = 1 };
        var provider = new Provider(platform) { InstalledMetadata = new Metadata(), FreshMetadata = true, GetterElapsed = () => TimeSpan.Zero };
        var backend = new CpuLivePowerNativeBackend(platform, provider); var diagnostic = new CpuSavedAdmissionDiagnostic();
        using var disposing = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        provider.OnGetterDispose = () => { disposing.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        try
        {
            var pending = Controller(backend, new(), TimeSpan.FromMilliseconds(300))
                .ObserveLiveAsync(_ => true, default, diagnostic: diagnostic);
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(5)));
            var result = await pending;
            Assert.Equal(CpuTuningRecoveryState.Blocked, result.LiveRecovery!.State);
            var worker = Assert.IsAssignableFrom<Task>(backend.OutstandingWorker);
            string? frozen = diagnostic.LiveObservationTimeoutContext;
            release.Set(); await worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(frozen, diagnostic.LiveObservationTimeoutContext);
            Assert.Equal(1, provider.GetterBegins); Assert.Equal(1, platform.Commands.Count(command => command == 0x65));
            Assert.Equal(0, platform.Setters);
        }
        finally { release.Set(); provider.InstalledLease?.Dispose(); }
    }

    [Fact]
    public async Task RuntimePostAdmissionDenialDropsTerminalTicket()
    {
        var platform = new Platform { RefreshResponse = 0xFD, NextRefreshResponse = 1 };
        var provider = new Provider(platform) { InstalledMetadata = new Metadata(), FreshMetadata = true, GetterElapsed = () => TimeSpan.Zero };
        bool admitted = true; provider.OnGetterDispose = () => admitted = false;
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        try
        {
            var result = await Controller(new(platform, provider), new()).ObserveLiveAsync(_ => admitted, default, diagnostic: diagnostic);
            Assert.Equal(CpuTuningRecoveryState.Blocked, result.LiveRecovery!.State);
            Assert.NotNull(diagnostic.FirstFailure); Assert.Equal(1, provider.GetterBegins);
            Assert.Contains(EmptyWindowFailure, diagnostic.LiveObservationReason!);
            Assert.Equal(0, platform.Setters);
        }
        finally { provider.InstalledLease?.Dispose(); }
    }

    private static CpuTuningController Controller(CpuLivePowerNativeBackend backend,
        CpuLivePowerTuningTransactionTests.MemoryStore store, TimeSpan? timeout = null) =>
        new(new UnusedSource(), powerBackend: backend, powerStore: store, powerObservationTimeout: timeout);
    private sealed class UnusedSource : ICpuTuningObservationSource
    {
        public DeviceIdentity? ReadIdentity() => throw new InvalidOperationException();
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity) => throw new InvalidOperationException();
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend) => throw new InvalidOperationException();
    }

    private const string EmptyWindowFailure = "power native observation Refresh/NativeFailure; native failure WireReplyRejected; operation 0x81112F24; wire rejection MailboxResponseNotSuccessful; mailbox status 0x000000FD";

    [Theory]
    [InlineData(false, "NativeFailure")]
    [InlineData(true, "AdmissionDenied")]
    public async Task RefreshFailureSurfacesRawNativeDetailsAndStopsBeforeCopy(bool admission, string expected)
    {
        var platform = new Platform { RefreshFailure = admission ? CpuQueryFailureCode.AdmissionDenied : CpuQueryFailureCode.NativeIoctlFailed };
        var provider = new Provider(platform);
        var backend = new CpuLivePowerNativeBackend(platform, provider, new Metadata());
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        var result = await backend.ObserveAsync(default, diagnostic);
        Assert.Null(result.Observation);
        Assert.Equal($"power native observation Refresh/{expected}; native failure {platform.RefreshFailure}; native error 5; operation 0x81112FF8", diagnostic.FirstFailure);
        Assert.Equal(new uint[] { 2, 0x82, 6, 0x65 }, platform.Commands);
        Assert.Equal(0, platform.Setters); Assert.Equal(0, provider.Interpretations);
        Assert.False(backend.Status.ScopeInvalid); Assert.Null(backend.OutstandingWorker);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RefreshReplyRejectionSurfacesBoundedWireReasonAndPreservesItAcrossCleanupFailure(bool corruptEcho, bool cleanupFails)
    {
        var platform = new Platform { RefreshResponse = 2, CorruptRefreshEcho = corruptEcho, CleanupFault = cleanupFails ? "continuity" : null };
        var provider = new Provider(platform);
        var backend = new CpuLivePowerNativeBackend(platform, provider, new Metadata());
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        Assert.Null((await backend.ObserveAsync(default, diagnostic)).Observation);
        string detail = corruptEcho ? "PciReadEchoMismatch" : "MailboxResponseNotSuccessful; mailbox status 0x00000002";
        Assert.Equal("power native observation Refresh/NativeFailure; native failure WireReplyRejected; operation 0x81112F24; wire rejection " + detail,
            diagnostic.FirstFailure);
        Assert.Equal(new uint[] { 2, 0x82, 6, 0x65 }, platform.Commands);
        Assert.Equal(0, platform.Copies); Assert.Equal(0, platform.Setters); Assert.Equal(0, provider.Interpretations);
        Assert.Equal(cleanupFails, backend.Status.ScopeInvalid); Assert.Null(backend.OutstandingWorker);
        int calls = platform.Calls;
        if (cleanupFails)
        {
            Assert.Null((await backend.ObserveAsync(default)).Observation);
            Assert.Equal(calls, platform.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerObservationEndsGetterAndRejectsFailedFinalAdmission(bool rejectFinal)
    {
        var platform = new Platform();
        var provider = new Provider(platform) { InstalledMetadata = new Metadata(), FinalAllowed = !rejectFinal };
        var backend = new CpuLivePowerNativeBackend(platform, provider);
        try
        {
            var result = await backend.ObserveAsync(default);
            Assert.Equal(!rejectFinal, result.Context is not null);
            Assert.Equal(1, provider.GetterBegins); Assert.Equal(1, provider.GetterCompletions);
            Assert.Equal(1, provider.GetterDisposals); Assert.False(provider.GetterActive);
            Assert.Equal(rejectFinal, backend.Status.ScopeInvalid);
            Assert.Equal(0, platform.Setters);
        }
        finally { provider.InstalledLease?.Dispose(); }
    }

    [Fact]
    public async Task InstalledObservationUsesProviderMetadataAndRetainedDeviceWithoutAttendedContinuity()
    {
        var platform = new Platform(); var metadata = new Metadata();
        var provider = new Provider(platform) { InstalledMetadata = metadata };
        var backend = new CpuLivePowerNativeBackend(platform, provider);
        try
        {
            var result = await backend.ObserveAsync(default);
            Assert.Equal(provider.Context, result.Context);
            Assert.Equal(platform.Pair, CpuLivePowerSnapshot.TryCreate(result.Observation)!.Pair);
            Assert.Equal(1, platform.Opens); Assert.Equal(1, platform.Duplicates);
            Assert.Equal(0, platform.Arms); Assert.Equal(0, platform.Setters);
            Assert.Equal(2, metadata.Calls); Assert.Equal(1, provider.Interpretations);
            Assert.False(backend.Status.ScopeInvalid);
        }
        finally { provider.InstalledLease?.Dispose(); }
    }

    [Theory]
    [InlineData("2.03")]
    [InlineData("Unknown")]
    public async Task BiosProvenanceChangeDoesNotInvalidateTheOtherwiseExactFreshContext(string bios)
    {
        var platform = new Platform(); var provider = new Provider(platform);
        var expected = provider.Context;
        provider.Context = expected with { Identity = expected.Identity with { BiosVersion = bios } };
        var backend = new CpuLivePowerNativeBackend(platform, provider, new Metadata());
        var request = Request(platform.Pair, platform.Pair, CpuLivePowerLimit.Fast, 90000);
        Assert.Equal(CpuLivePowerSubmission.Acknowledged, await backend.SendAsync(expected, request, default));
        Assert.Same(request, provider.LastRequest);
        Assert.Equal(1, platform.Setters);
        Assert.True(backend.Status.LastNativeResult!.Verified);
    }

    [Fact]
    public async Task ActualReadProfileRequiresIndependentInterpretationAndNeverUsesSetter()
    {
        var platform = new Platform(); var provider = new Provider(platform); var metadata = new Metadata();
        var backend = new CpuLivePowerNativeBackend(platform, provider, metadata);
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        var result = await backend.ObserveAsync(default, diagnostic);
        Assert.Null(diagnostic.FirstFailure);
        Assert.Equal(provider.Context, result.Context); Assert.Equal(platform.Pair, CpuLivePowerSnapshot.TryCreate(result.Observation)!.Pair);
        Assert.Equal(new uint[] { 2, 0x82, 6, 0x65, 6 }, platform.Commands); Assert.Equal(0, platform.Setters);
        Assert.Equal(1, provider.Interpretations); Assert.Equal(2, metadata.Calls);
        Assert.Equal(2u, result.Capability!.RawValue); Assert.Equal(71u, result.FusedCap!.Watts);
        Assert.False(backend.Status.ScopeInvalid); Assert.Null(backend.OutstandingWorker);
        provider.InterpretationAllowed = false;
        Assert.Null((await backend.ObserveAsync(default)).Observation); Assert.Equal(0, platform.Setters);
        Assert.Equal(2, provider.Interpretations);
    }

    [Fact]
    public async Task ExistingBatchDiagnosticSurvivesRejectedPowerObservation()
    {
        var platform = new Platform(); var provider = new Provider(platform);
        var metadata = new Metadata { Fault = "invalid-clean" };
        var backend = new CpuLivePowerNativeBackend(platform, provider, metadata);
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        Assert.Null((await backend.ObserveAsync(default, diagnostic)).Observation);
        Assert.Equal("power native observation MetadataVersion/InvalidData", diagnostic.FirstFailure);
        Assert.Equal(1, metadata.Calls); Assert.Equal(0, platform.Setters);
        Assert.False(backend.Status.ScopeInvalid); Assert.Null(backend.OutstandingWorker);
    }

    [Fact]
    public async Task WrongProfileAndReplayedObservationApprovalCannotObtainAnotherSample()
    {
        var platform = new Platform(); var provider = new Provider(platform) { WrongProfile = true };
        var backend = new CpuLivePowerNativeBackend(platform, provider, new Metadata());
        Assert.Null((await backend.ObserveAsync(default)).Observation); Assert.Equal(0, platform.Opens);
        provider.WrongProfile = false; provider.ReuseApproval = true;
        Assert.NotNull((await backend.ObserveAsync(default)).Observation); int opens = platform.Opens;
        Assert.Null((await backend.ObserveAsync(default)).Observation); Assert.Equal(opens, platform.Opens);
        Assert.Equal(1, provider.Interpretations); Assert.False(backend.Status.ScopeInvalid);
    }

    [Fact]
    public async Task SendPreservesExactIncomingRequestReferenceAndEarlierFullOriginal()
    {
        var platform = new Platform(); var provider = new Provider(platform); var backend = new CpuLivePowerNativeBackend(platform, provider, new Metadata());
        var original = platform.Pair;
        var first = Request(platform.Pair, original, CpuLivePowerLimit.Fast, 90000);
        Assert.Equal(CpuLivePowerSubmission.Acknowledged, await backend.SendAsync(provider.Context, first, default));
        Assert.Same(first, provider.LastRequest);
        var second = Request(platform.Pair, original, CpuLivePowerLimit.Slow, 79000);
        Assert.Equal(CpuLivePowerSubmission.Acknowledged, await backend.SendAsync(provider.Context, second, default));
        Assert.Same(second, provider.LastRequest); Assert.Equal(original, provider.LastRequest!.Original);
        Assert.NotEqual(second.Before, second.Original);
        Assert.Contains(provider.Admissions, a => a.Command.Limit == CpuLivePowerLimit.Fast && a.Command.Milliwatts == original.Fast.Milliwatts
            && a.Purpose == CpuLivePowerCommandPurpose.Restore && a.Before == second.Before && a.Original == original);
        Assert.True(backend.Status.LastNativeResult!.Verified); Assert.Equal(2, platform.Setters);
        var restore = Request(platform.Pair, original, CpuLivePowerLimit.Fast, original.Fast.Milliwatts, CpuLivePowerCommandPurpose.Restore);
        Assert.Equal(CpuLivePowerSubmission.Acknowledged, await backend.SendAsync(provider.Context, restore, default));
        Assert.Same(restore, provider.LastRequest); Assert.Equal(original.Fast, platform.Pair.Fast);
    }

    [Fact]
    public async Task ConsumedProofCannotGainAuthorityFromFreshWrapperOrNewRequest()
    {
        var platform = new Platform(); var provider = new Provider(platform) { ReuseProof = true };
        var backend = new CpuLivePowerNativeBackend(platform, provider, new Metadata()); var original = platform.Pair;
        var first = Request(platform.Pair, original);
        Assert.Equal(CpuLivePowerSubmission.Acknowledged, await backend.SendAsync(provider.Context, first, default));
        int opens = platform.Opens;
        Assert.Equal(CpuLivePowerSubmission.NotSubmitted, await backend.SendAsync(provider.Context, first, default));
        Assert.Equal(CpuLivePowerSubmission.NotSubmitted, await backend.SendAsync(provider.Context, Request(platform.Pair, original, CpuLivePowerLimit.Slow, 79000), default));
        Assert.Equal(opens, platform.Opens); Assert.Equal(1, platform.Setters);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("setter-boundary")]
    public async Task ContextBindingRevokesEvenPermissiveSourceProofBeforeNativeSetter(string point)
    {
        var platform = new Platform(); var provider = new Provider(platform); var request = Request(platform.Pair, platform.Pair);
        Action drift = () => provider.Context = provider.Context with { BootSequence = provider.Context.BootSequence with { Sequence = 43 } };
        if (point == "range") { provider.OnRange = drift; } else { platform.BeforeSetter = drift; }
        var backend = new CpuLivePowerNativeBackend(platform, provider, new Metadata());
        Assert.Equal(CpuLivePowerSubmission.Unknown, await backend.SendAsync(provider.Context, request, default));
        Assert.Equal(0, platform.Setters); Assert.True(backend.Status.ScopeInvalid);
    }

    [Theory]
    [InlineData("metadata-throw")]
    [InlineData("metadata-disposal")]
    [InlineData("metadata-missing-disposed")]
    [InlineData("metadata-preinvoke-no-disposal")]
    [InlineData("metadata-unknown")]
    [InlineData("continuity")]
    [InlineData("unpin")]
    public async Task ActualObservationCleanupUncertaintyParksBothFamiliesAndReplacement(string fault)
    {
        var platform = new Platform { CleanupFault = fault }; var provider = new Provider(platform);
        var metadata = new Metadata { Fault = fault };
        var backend = new CpuLivePowerNativeBackend(platform, provider, metadata);
        Assert.Null((await backend.ObserveAsync(default)).Observation); Assert.True(backend.Status.ScopeInvalid);
        int opens = platform.Opens, callbacks = provider.ContextReads;
        var replacement = new CpuLivePowerNativeBackend(WindowsRz090528CpuNativeSession.ForPlatform(platform), provider, metadata);
        Assert.Null((await replacement.ObserveAsync(default)).Observation);
        Assert.Equal(CpuLivePowerSubmission.Unknown, await replacement.SendAsync(provider.Context, Request(platform.Pair, platform.Pair), default));
        var curveProvider = new CurveProvider(); var curve = new CpuLiveCurveNativeBackend(platform, curveProvider);
        Assert.Null((await curve.ObserveAsync(default)).Observation); Assert.True(curve.Status.ScopeInvalid);
        Assert.Equal(0, curveProvider.Calls); Assert.Equal(callbacks, provider.ContextReads); Assert.Equal(opens, platform.Opens);
        await AssertCurveRefused(platform);
        // Refusal must also survive a caller bypassing Service through the
        // same opaque Protocol session; shared Service state alone is not enough.
        var native = WindowsRz090528CpuNativeSession.ForPlatform(platform).ObservePower(
            CpuPmAttendedApproval.Acknowledge(true, true, platform.Lifetime, CpuPmObservationProfile.RangeSingleSample),
            () => platform.Lifetime, new Metadata());
        Assert.Equal(CpuPmObservationFailure.Busy, native.Batch.Diagnostic!.Failure); Assert.Equal(opens, platform.Opens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvenDisposedInvalidMetadataAndGenuinelyUncalledMetadataDoNotInventCleanupFailure(bool neverCalled)
    {
        var platform = new Platform { InvalidVersion = neverCalled }; var provider = new Provider(platform);
        var metadata = new Metadata { Fault = neverCalled ? null : "invalid-clean" };
        var backend = new CpuLivePowerNativeBackend(platform, provider, metadata);
        Assert.Null((await backend.ObserveAsync(default)).Observation); Assert.False(backend.Status.ScopeInvalid);
        Assert.Equal(neverCalled ? 0 : 1, metadata.Calls);
        platform.InvalidVersion = false; metadata.Fault = null;
        Assert.NotNull((await new CpuLivePowerNativeBackend(platform, provider, metadata).ObserveAsync(default)).Observation);
        Assert.Equal(0, platform.Setters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderBlockedBeforeNativeWorkExcludesBothFamiliesAcrossCancellationAndReplacement(bool curveFirst)
    {
        var platform = new Platform(); var provider = new Provider(platform); var curveProvider = new CurveProvider();
        var power = new CpuLivePowerNativeBackend(platform, provider, new Metadata()); var curve = new CpuLiveCurveNativeBackend(platform, curveProvider);
        using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        Action block = () => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); throw new OperationCanceledException(); };
        if (curveFirst) { curveProvider.OnContext = block; } else { provider.OnContext = block; }
        Task caller = curveFirst ? curve.ObserveAsync(cancellation.Token) : power.ObserveAsync(cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); Task worker = Assert.IsAssignableFrom<Task>(power.OutstandingWorker);
            Assert.Same(worker, curve.OutstandingWorker); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await caller);
            Assert.Same(worker, power.OutstandingWorker);
            var replacement = new CpuLivePowerNativeBackend(WindowsRz090528CpuNativeSession.ForPlatform(platform), new Provider(platform), new Metadata());
            Assert.Same(worker, replacement.OutstandingWorker); Assert.Null((await replacement.ObserveAsync(default)).Observation);
            Assert.False(await replacement.CheckWriteAdmissionAsync(provider.Context, Request(platform.Pair, platform.Pair).Command,
                CpuLivePowerCommandPurpose.Forward, platform.Pair, platform.Pair, default));
            Assert.Equal(CpuLivePowerSubmission.Unknown, await replacement.SendAsync(provider.Context, Request(platform.Pair, platform.Pair), default));
            await AssertCurveRefused(platform);
            Assert.Equal(0, platform.Opens);
        }
        finally { finish.Set(); }
        var actual = power.OutstandingWorker; if (actual is not null) { await actual.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.True(SpinWait.SpinUntil(() => !power.Status.WorkerOutstanding, TimeSpan.FromSeconds(10)));
        Assert.True(power.Status.ScopeInvalid); Assert.True(curve.Status.ScopeInvalid);
        Assert.Null((await new CpuLivePowerNativeBackend(platform, new Provider(platform), new Metadata()).ObserveAsync(default)).Observation);
    }

    [Fact]
    public async Task CancelledActualSetterRetainsWorkerPinsAndBlocksCurveUntilTerminalCleanup()
    {
        var platform = new Platform(); var provider = new Provider(platform);
        var backend = new CpuLivePowerNativeBackend(platform, provider, new Metadata());
        using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        platform.AtSetter = () => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); };
        var caller = backend.SendAsync(provider.Context, Request(platform.Pair, platform.Pair), cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); Task worker = Assert.IsAssignableFrom<Task>(backend.OutstandingWorker);
            cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await caller);
            Assert.Same(worker, backend.OutstandingWorker); Assert.True(platform.Pinned); Assert.True(platform.Held);
            await AssertCurveRefused(platform);
            Assert.Equal(CpuLivePowerSubmission.Unknown, await new CpuLivePowerNativeBackend(platform, new Provider(platform), new Metadata())
                .SendAsync(provider.Context, Request(platform.Pair, platform.Pair), default));
            Assert.Equal(1, platform.Setters);
        }
        finally { finish.Set(); }
        var actual = backend.OutstandingWorker; if (actual is not null) { await actual.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.True(SpinWait.SpinUntil(() => !backend.Status.WorkerOutstanding, TimeSpan.FromSeconds(10)));
        Assert.False(platform.Held); Assert.False(platform.Pinned); Assert.True(backend.Status.ScopeInvalid);
        Assert.Equal(CpuLivePowerNativeDisposition.PossibleSubmission, backend.Status.LastNativeResult!.Disposition);
        Assert.True(backend.Status.LastNativeResult.CleanupComplete);
    }

    [Fact]
    public void ResultMappingRequiresActualTerminalCleanupAndVerifiedExactPair()
    {
        var before = CpuLivePowerTuningTransactionTests.PairFixture(); var request = Request(before, before);
        var after = CpuLivePowerSnapshot.TryCreate(CpuLivePowerTuningTransactionTests.Raw(request.After))!;
        var acknowledged = new CpuLivePowerNativeResult(CpuLivePowerNativeDisposition.Acknowledged, true, true, true, after, CpuLivePowerNativeFailure.None);
        Assert.Equal(CpuLivePowerSubmission.Acknowledged, CpuLivePowerNativeBackend.MapSubmission(request, acknowledged));
        foreach (var result in new[] { acknowledged with { WorkerTerminal = false }, acknowledged with { CleanupComplete = false },
            acknowledged with { Verified = false }, acknowledged with { Failure = CpuLivePowerNativeFailure.Transport },
            acknowledged with { After = CpuLivePowerSnapshot.TryCreate(CpuLivePowerTuningTransactionTests.Raw(before)) },
            acknowledged with { Disposition = CpuLivePowerNativeDisposition.PossibleSubmission } })
        { Assert.Equal(CpuLivePowerSubmission.Unknown, CpuLivePowerNativeBackend.MapSubmission(request, result)); }
        var clean = new CpuLivePowerNativeResult(CpuLivePowerNativeDisposition.NotSubmitted, true, true, false, null, CpuLivePowerNativeFailure.AdmissionDenied);
        Assert.Equal(CpuLivePowerSubmission.NotSubmitted, CpuLivePowerNativeBackend.MapSubmission(request, clean));
        Assert.Equal(CpuLivePowerSubmission.Unknown, CpuLivePowerNativeBackend.MapSubmission(request, clean with { CleanupComplete = false }));
    }

    private static CpuLivePowerNativeRequest Request(CpuLivePowerPair before, CpuLivePowerPair original,
        CpuLivePowerLimit limit = CpuLivePowerLimit.Fast, uint value = 90000, CpuLivePowerCommandPurpose purpose = CpuLivePowerCommandPurpose.Forward)
    { Assert.True(CpuLivePowerCommand.TryCreate(limit, value, out var command)); return new(new(), command, purpose, before, original); }

    private static async Task AssertCurveRefused(Platform platform)
    {
        var provider = new CurveProvider(); var backend = new CpuLiveCurveNativeBackend(platform, provider);
        var context = CpuLiveCurveTuningTransactionTests.ContextFixture();
        Assert.True(CpuLiveCurveCommand.TryCreate(0, 0, 0xFF55, out var command));
        var snapshot = CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50, CpuLiveCurveTuningTransactionTests.ValuesFixture()))!;
        Assert.Null((await backend.ObserveAsync(default)).Observation);
        Assert.False(await backend.CheckWriteAdmissionAsync(context, command, CpuLiveCurveCommandPurpose.Forward, default));
        Assert.Equal(CpuLiveCurveSubmission.Unknown, await backend.SendAsync(context, command, CpuLiveCurveCommandPurpose.Forward, snapshot, default));
        Assert.Equal(0, provider.Calls);
    }

    private sealed class Provider(Platform platform) : ICpuLivePowerTrustedNativeProvider
    {
        internal CpuLivePowerContext Context = CpuLivePowerTuningTransactionTests.ContextFixture();
        internal int ContextReads, Interpretations, GetterBegins, GetterCompletions, GetterDisposals;
        internal bool GetterActive, FinalAllowed = true;
        internal Action? OnGetterDispose;
        internal readonly List<CancellationToken> GetterTokens = [];
        internal Func<TimeSpan>? GetterElapsed;
        private CpuLiveGetterAdmission? getterProof;
        public IDisposable BeginGetter(CancellationToken token)
        {
            Assert.False(GetterActive); GetterActive = true; GetterBegins++; GetterTokens.Add(token);
            if (GetterElapsed is not null) { getterProof = new(token, () => true, _ => true, GetterElapsed); }
            return new GetterRelease(() => { getterProof?.Dispose(); GetterActive = false; GetterDisposals++; OnGetterDispose?.Invoke(); });
        }
        public bool CompleteGetter(CpuLivePowerContext expected, CancellationToken token)
        {
            Assert.True(GetterActive); GetterCompletions++;
            return FinalAllowed && expected.MatchesHardwareContext(ReadFreshContext(token));
        }
        private sealed class GetterRelease(Action release) : IDisposable { public void Dispose() => release(); }
        internal bool InterpretationAllowed = true, WrongProfile, ReuseApproval, ReuseProof;
        internal Action? OnContext, OnRange;
        internal Metadata? InstalledMetadata;
        internal bool FreshMetadata, DenyRecoveryApproval, DenyRecoveryMetadata;
        internal readonly List<CpuPmInstalledApproval?> InstalledApprovals = [];
        internal readonly List<Metadata> MetadataReaders = [];
        internal CpuInstalledBackendLease? InstalledLease;
        internal CpuLivePowerNativeRequest? LastRequest;
        internal readonly List<(CpuLivePowerCommand Command, CpuLivePowerCommandPurpose Purpose, CpuLivePowerPair Before, CpuLivePowerPair Original)> Admissions = [];
        private CpuPmAttendedApproval? approval; private CpuLivePowerWriteProof? proof;
        public CpuLivePowerContext? ReadFreshContext(CancellationToken token)
        { ContextReads++; OnContext?.Invoke(); return getterProof?.Allows() != false ? Context : null; }
        public CpuPmInstalledApproval? CreateInstalledObservationApproval(CpuLivePowerContext context, CancellationToken token)
        {
            if (InstalledMetadata is null) { return null; }
            InstalledLease ??= WindowsRz090528CpuNativeSession.ForPlatform(platform).TryRetainInstalledBackend(platform.ReadUsb, token);
            var created = DenyRecoveryApproval && InstalledApprovals.Count != 0 ? null
                : InstalledLease?.CreatePowerObservationApproval(() => context.MatchesHardwareContext(Context));
            InstalledApprovals.Add(created); return created;
        }
        public IRz090528CpuLivePowerRangeMetadataReader? CreateObservationMetadata(CpuLivePowerContext context, CancellationToken token)
        {
            if (InstalledMetadata is null || DenyRecoveryMetadata && MetadataReaders.Count != 0) { return null; }
            var created = FreshMetadata ? new Metadata() : InstalledMetadata;
            MetadataReaders.Add(created); return created;
        }
        public CpuPmAttendedApproval? CreateObservationApproval(CpuLivePowerContext context, CancellationToken token)
        {
            Assert.Null(InstalledMetadata);
            if (ReuseApproval && approval is not null) { return approval; }
            var created = CpuPmAttendedApproval.Acknowledge(true, true, platform.Lifetime,
                WrongProfile ? CpuPmObservationProfile.IdleThreeSamples : CpuPmObservationProfile.RangeSingleSample);
            if (!WrongProfile) { approval = created; }
            return created;
        }
        public CpuPmRunOwnedDriverLifetime? ReadFreshObservationLifetime(CpuLivePowerContext context, CancellationToken token) => platform.Lifetime;
        public CpuLivePowerTrustedRange? AdmitRequestedStateAndRange(CpuLivePowerContext context, CpuPmObservationBatch batch, CancellationToken token)
        { Interpretations++; Assert.True(batch.Complete); return InterpretationAllowed ? new(0x50001, 0, 125000, 1) : null; }
        public bool CheckWriteAdmission(CpuLivePowerContext context, CpuLivePowerCommand command, CpuLivePowerCommandPurpose purpose,
            CpuLivePowerPair before, CpuLivePowerPair original, CancellationToken token)
        { Admissions.Add((command, purpose, before, original)); return true; }
        public CpuLivePowerWriteProof? CreateWriteProof(CpuLivePowerContext context, CpuLivePowerNativeRequest request, CancellationToken token)
        { LastRequest = request; if (ReuseProof && proof is not null) { return proof; } return proof = new Proof(request, this); }
        private sealed class Proof(CpuLivePowerNativeRequest request, Provider provider) : CpuLivePowerWriteProof(request)
        {
            internal override bool IsCurrent(CpuLivePowerNativeRequest exactRequest) => true;
            internal override CpuLivePowerTrustedRange? ReadFreshRange(CpuLivePowerNativeRequest exactRequest)
            { provider.OnRange?.Invoke(); return new(0x50001, 0, 125000, 1); }
        }
    }
    private sealed class CurveProvider : ICpuLiveCurveTrustedNativeProvider
    {
        internal Action? OnContext; internal int Calls;
        public CpuLiveCurveContext? ReadFreshContext(CancellationToken token) { Calls++; OnContext?.Invoke(); return CpuLiveCurveTuningTransactionTests.ContextFixture(); }
        public CpuQueryAttendedApproval? CreateQueryApproval(CpuLiveCurveContext context, CancellationToken token) { Calls++; return null; }
        public bool CheckWriteAdmission(CpuLiveCurveContext context, CpuLiveCurveCommand command, CpuLiveCurveCommandPurpose purpose, CancellationToken token) { Calls++; return false; }
        public CpuLiveCurveWriteProof? CreateWriteProof(CpuLiveCurveContext context, CpuLiveCurveNativeRequest request, CancellationToken token) { Calls++; return null; }
    }
    private sealed class Metadata : IRz090528CpuLivePowerRangeMetadataReader
    {
        internal string? Fault; internal int Calls;
        private const CpuLivePowerRangeMetadataStages Stages = CpuLivePowerRangeMetadataStages.InvocationEntered | CpuLivePowerRangeMetadataStages.InvocationReturned
            | CpuLivePowerRangeMetadataStages.OutputEnumerated | CpuLivePowerRangeMetadataStages.PropertiesRead | CpuLivePowerRangeMetadataStages.Disposed;
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion> QueryVersion(Action checkpoint)
        {
            Calls++; checkpoint(); if (Fault == "metadata-throw") { throw new OperationCanceledException(); }
            if (Fault == "metadata-unknown") { throw new NotSupportedException(); }
            return new(new(Fault == "invalid-clean" ? 8u : 7u, false), Fault == "metadata-disposal" ? CpuLivePowerRangeMetadataStatus.DisposalFailed
                : Fault == "metadata-preinvoke-no-disposal" ? CpuLivePowerRangeMetadataStatus.NativeFailure
                : Fault == "invalid-clean" ? CpuLivePowerRangeMetadataStatus.InvalidData : CpuLivePowerRangeMetadataStatus.Complete,
                Fault == "metadata-preinvoke-no-disposal" ? CpuLivePowerRangeMetadataStages.None
                    : Fault == "metadata-missing-disposed" ? Stages & ~CpuLivePowerRangeMetadataStages.Disposed : Stages,
                DateTimeOffset.UtcNow, 123);
        }
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues> GetDValues(Action checkpoint) => throw new InvalidOperationException("Type-One profile cannot call discrete getter.");
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax> GetMinMaxSteppingValues(Action checkpoint)
        { Calls++; checkpoint(); return new(new(0x50001, 0, 0, 125000, 1), CpuLivePowerRangeMetadataStatus.Complete, Stages, DateTimeOffset.UtcNow, 123); }
    }

    private sealed class Platform : ICpuPmWindowsPlatform, ICpuQueryMutex
    {
        internal CpuLivePowerPair Pair = CpuLivePowerTuningTransactionTests.PairFixture();
        internal readonly CpuPmRunOwnedDriverLifetime Lifetime = new(Guid.NewGuid(), Guid.NewGuid(), true);
        internal readonly List<uint> Commands = [];
        internal int Opens, Setters, Duplicates, Arms; internal bool Held, Pinned, InvalidVersion; internal string? CleanupFault; internal Action? BeforeSetter, AtSetter;
        private uint selected, argument, command, refreshReply = 1; private int copies;
        internal int PriorRejectedRefreshReadiness;
        internal CpuQueryFailureCode? RefreshFailure;
        internal uint RefreshResponse = 1;
        internal uint? NextRefreshResponse;
        internal bool CorruptRefreshEcho;
        internal int Calls;
        internal int Copies => copies;
        public DeviceIdentity ReadUsb() => new("RZ09-0528", 0x1532, 0x02C6, "2.02");
        public HostIdentityReadResult ReadHost() => new(new("Razer", "Blade 16 - RZ09-0528", "RZ09-05289EN4", "2.02"), null);
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x1A, 0x24, 0);
        public CpuPmProcessorPackage ReadPackage() => new(0x80000001, 1);
        public bool ControllersAbsent() => true;
        public CpuPmServiceSnapshot ReadService() => new(true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, true);
        public InstalledCpuBackendSnapshot ReadBackend() => new(Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair =>
            new CpuBackendFileMetadata(pair.Key, pair.Value, true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray(),
            true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, true);
        public IDisposable PinBackendFiles() { Assert.True(Held); Pinned = true; return new ReleaseAction(() => { Pinned = false; if (CleanupFault == "unpin") { throw new OperationCanceledException(); } }); }
        public ICpuQueryMutex CreateSharedMutex() => this;
        public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken token) { Assert.False(Held); Held = true; return CpuQueryMutexWait.Acquired; }
        public void Release() { Assert.True(Held); Held = false; }
        public void Dispose() { }
        public ICpuQueryDevice OpenExistingDevice() { Assert.True(Held); Assert.True(Pinned); Opens++; copies = 0; return new Device(this); }
        public ICpuPmContinuityScope ArmContinuity(CpuPmRunOwnedDriverLifetime lifetime) { Arms++; Assert.Equal(Lifetime, lifetime); return new Continuity(this); }
        private sealed class Continuity(Platform platform) : ICpuPmContinuityScope
        {
            private readonly Guid session = Guid.NewGuid(); private bool disposed;
            public CpuPmContinuityEvidence ReadFresh(CpuPmRunOwnedDriverLifetime? lifetime) => new(
                disposed ? CpuPmSessionContinuity.Interrupted : CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime,
                session, platform.Lifetime.DriverLifetimeId, disposed ? platform.CleanupFault == "continuity"
                    ? CpuPmContinuityFailure.UnregisterFailed : CpuPmContinuityFailure.Disposed : CpuPmContinuityFailure.None);
            public void Dispose() { disposed = true; }
        }
        private sealed class Device(Platform platform, bool duplicated = false) : ICpuQueryRetainableDevice
        {
            public ICpuQueryDevice Duplicate() { platform.Duplicates++; return new Device(platform, true); }
            public CpuQueryIoctlReply Invoke(uint code, byte[] input, int length) => platform.Invoke(code, input, length);
            public void Dispose() { if (duplicated && platform.CleanupFault == "device") { throw new IOException("Duplicate cleanup failed."); } }
        }
        private sealed class ReleaseAction(Action action) : IDisposable { public void Dispose() => action(); }
        private CpuQueryIoctlReply Invoke(uint code, byte[] input, int length)
        {
            Assert.True(Held); Assert.True(Pinned); Calls++; var output = new byte[length];
            if (code == 0x81112F18)
            {
                Put(output, 24, 3);
                if (length > 28)
                {
                    uint[][] records = [[0, 0, 0, CpuQueryPciTopology.RootId], [0, 0x18, 0, CpuQueryPciTopology.NodeId], [0, 0x18, 1, CpuQueryPciTopology.OrientationId]];
                    for (int i = 0; i < 3; i++) { for (int j = 0; j < 4; j++) { Put(output, 28 + i * 52 + j * 4, records[i][j]); } }
                }
            }
            else if (code == 0x81112FF8)
            {
                uint value = Word(input, 4);
                if (Word(input, 0) == 0) { selected = value; if (selected == 0x03B10A20 && copies == 1 && command == 0x82) { BeforeSetter?.Invoke(); } }
                else if (selected == 0x03B10A88) { argument = value; }
                else if (selected == 0x03B10A80) { refreshReply = value; }
                else if (selected == 0x03B10A20)
                {
                    command = value; Commands.Add(value);
                    if (value == 0x65) { refreshReply = RefreshResponse; }
                    if (value == 0x65 && RefreshFailure is { } failure) { return new(false, [], failure, 5); }
                    if (value is 0x32 or 0x33) { Setters++; AtSetter?.Invoke(); Assert.True(CpuLivePowerCommand.TryDecode(value, argument, out var typed)); Pair = Pair.With(typed); }
                }
            }
            else if (code == 0x81112F24)
            {
                input.CopyTo(output, 0); uint offset = Word(input, 12);
                uint value = offset == 0 ? Word(input, 4) == 0 ? CpuQueryPciTopology.RootId : Word(input, 8) == 0 ? CpuQueryPciTopology.NodeId : CpuQueryPciTopology.OrientationId
                    : offset == 0x140 ? 0xFF55u : selected switch
                    {
                        0x03820AB0 => (~0xFF55u & 0xFFFF) << 5,
                        0x03820A0F => 4,
                        0x03820A0B => 7u << 28,
                        0x03820A13 or 0x03820A90 => 0,
                        0x03B10A80 => 1,
                        0x03B10A88 => command == 2 ? 0x0B5D1600u
                            : command == 6 ? InvalidVersion ? 0u : 0x005D000Bu : command == 0x82 ? 2u : 1u,
                        _ => throw new InvalidOperationException(),
                    };
                if (offset == 0xC8 && selected == 0x03B10A80 && command == 0x65)
                {
                    value = refreshReply;
                    if (Duplicates == 2 && value == 0xFD && Commands.Count(message => message == 0x65) == 1)
                    { PriorRejectedRefreshReadiness++; }
                    if (CorruptRefreshEcho) { output[0] ^= 1; }
                    if (value == 0xFD)
                    {
                        if (NextRefreshResponse is { } next) { RefreshResponse = next; }
                    }
                }
                Put(output, 20, value);
            }
            else if (code == 0x81112F08)
            {
                Assert.Equal(0xD50u, Word(input, 0)); Assert.Equal(0xD54, length); copies++;
                Put(output, 0, 0xD50); Put(output, 4, Pair.StapmRawBits); Put(output, 12, Pair.Fast.RawBits); Put(output, 20, Pair.Slow.RawBits);
                selected = command = 0;
            }
            else { throw new InvalidOperationException(); }
            return new(true, output);
        }
        private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
}
