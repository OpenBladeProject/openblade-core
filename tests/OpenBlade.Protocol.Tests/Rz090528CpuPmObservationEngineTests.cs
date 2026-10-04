using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuPmObservationEngineTests
{
    [Fact]
    public void PriorTerminalErrorAllowsRangeGetterWithIdenticalTraffic()
    {
        var baseline = new Fake();
        Assert.True(baseline.Engine(CpuPmObservationProfile.RangeSingleSample, new MetadataFake(baseline)).Observe().Complete);
        var fake = new Fake { IdleResponse = 0xFD };
        Assert.True(fake.Engine(CpuPmObservationProfile.RangeSingleSample, new MetadataFake(fake)).Observe().Complete);
        Assert.Equal(baseline.Codes, fake.Codes);
        Assert.Equal(baseline.Commands, fake.Commands);
        Assert.Equal(baseline.Copies, fake.Copies);
    }

    [Theory]
    [InlineData(0u, 1u, 0)]
    [InlineData(1u, 0xFDu, 1)]
    public void BusyReadinessAndFailedCompletionCannotProducePowerData(uint prior, uint completion, int commands)
    {
        var fake = new Fake { IdleResponse = prior, CompletionResponse = completion };
        var result = fake.Engine(CpuPmObservationProfile.RangeSingleSample, new MetadataFake(fake)).Observe();
        Assert.False(result.Complete);
        Assert.Equal(commands, fake.Commands.Count);
        Assert.Equal(0, fake.Copies);
        Assert.Empty(result.CopySamples());
    }

    [Theory]
    [InlineData("2.02")]
    [InlineData("2.03")]
    [InlineData("Unknown")]
    public void FixedIdleBatchOwnsOneThreadLeaseAndUsesOnlyExactProfile(string bios)
    {
        var fake = new Fake { Bios = bios };
        var result = fake.Engine().Observe();
        Assert.True(result.Complete);
        Assert.Null(result.Diagnostic);
        Assert.Equal(3, result.CopySamples().Length);
        Assert.Equal(new uint[] { 6, 0x65, 6, 6, 0x65, 6, 6, 0x65, 6 }, fake.Commands);
        Assert.Equal(3, fake.Copies);
        Assert.Equal(2, fake.IdleDelays);
        Assert.Equal(0xFF55u, result.OrientationMask);
        Assert.Equal(result.OrientationMask, result.FuseMask);
        Assert.All(result.CopySamples(), sample => Assert.Equal(0x12345678u, sample.RefreshReturnedWord));
        Assert.Equal(1, fake.Acquires);
        Assert.Equal(1, fake.Disposals);
        Assert.All(fake.Threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
        Assert.All(fake.Codes, code => Assert.Contains(code, new uint[] { 0x81112F18, 0x81112F24, 0x81112FF8, 0x81112F08 }));
        result.CopyPciRecords()[24] = 0;
        result.CopySamples()[0] = null!;
        Assert.Equal(3, result.CopyPciRecords()[24]);
        Assert.NotNull(result.CopySamples()[0]);
        // Successful copy intentionally clears the fake's native SMN index. A stale
        // index read would fail, so full success proves every post-copy reselection.
        Assert.Equal(3, fake.PostCopyResponseSelections);
        Assert.Equal(6, fake.SnapshotChecks);
        Assert.Equal(95u, result.FusedCap!.Candidate!.Watts);
        Assert.Equal(new uint[] { 0x03820A0F, 0x03820A0B }, fake.CapSelections);
        Assert.True(result.FusedCap.A0F!.PostReadAdmissionPassed);
        Assert.True(result.FusedCap.A0B!.PostReadAdmissionPassed);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("extended")]
    [InlineData("manufacturer")]
    [InlineData("sku")]
    [InlineData("usb")]
    [InlineData("cpu")]
    [InlineData("backend")]
    [InlineData("continuity")]
    [InlineData("controllers")]
    public void BadInitialEvidenceDoesNotAcquireOrInvoke(string failure)
    {
        var fake = new Fake { AdmissionFailure = failure };
        var result = fake.Engine().Observe();
        Assert.False(result.Complete);
        Assert.Equal(CpuPmObservationFailure.AdmissionDenied, result.Diagnostic!.Failure);
        Assert.Empty(fake.Codes);
        Assert.Equal(0, fake.Acquires);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("topology")]
    [InlineData("root")]
    [InlineData("mask")]
    [InlineData("fuse")]
    [InlineData("prefix")]
    [InlineData("length")]
    [InlineData("copy-failure")]
    [InlineData("copy-throw")]
    [InlineData("refresh-failure")]
    [InlineData("version")]
    [InlineData("post-version")]
    public void MalformedTransportAndProfileStopWithoutRetry(string failure)
    {
        var fake = new Fake { WireFailure = failure };
        var result = fake.Engine().Observe();
        Assert.False(result.Complete);
        Assert.NotNull(result.Diagnostic);
        Assert.Empty(result.CopySamples());
        Assert.InRange(fake.Copies, 0, 1);
        Assert.Equal(1, fake.Disposals);
        if (failure.StartsWith("copy-", StringComparison.Ordinal) || failure is "prefix" or "length")
        { Assert.Equal(0x81112F08u, fake.Codes.Last()); }
        if (failure == "version") { Assert.DoesNotContain(0x65u, fake.Commands); }
    }

    [Fact]
    public void SecondCopyFailureRetainsFirstCompletedImmutableSnapshot()
    {
        var fake = new Fake { FailCopyNumber = 2 };
        var result = fake.Engine().Observe();
        Assert.False(result.Complete);
        var sample = Assert.Single(result.CopySamples());
        Assert.Equal(0x12345678u, sample.RefreshReturnedWord);
        Assert.Equal(2, fake.Copies);
        Assert.Equal(0x81112F08u, fake.Codes.Last());
        Assert.Equal(1, fake.Disposals);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("elapsed")]
    [InlineData("drift")]
    public void BoundaryFailureAfterCopyPreventsAnyFollowingIoctl(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new Fake();
        fake.AfterCopy = () =>
        {
            if (failure == "cancel") { cancellation.Cancel(); }
            if (failure == "elapsed") { fake.Now = TimeSpan.FromSeconds(90); }
            if (failure == "drift") { fake.AdmissionFailure = "continuity"; }
        };
        var result = fake.Engine().Observe(cancellation.Token);
        Assert.False(result.Complete);
        Assert.Equal(0x81112F08u, fake.Codes.Last());
        Assert.Empty(result.CopySamples()); // post-version was never confirmed
        Assert.Equal(1, fake.Disposals);
    }

    [Fact]
    public void MutexWaitCountsTowardBudgetAndTimeoutNeverInvokes()
    {
        var fake = new Fake { AcquireElapsed = TimeSpan.FromSeconds(90) };
        Assert.Equal(CpuPmObservationFailure.BudgetExpired, fake.Engine().Observe().Diagnostic!.Failure);
        Assert.Empty(fake.Codes);
        Assert.Equal(1, fake.Disposals);
        fake = new Fake { MutexBusy = true };
        Assert.Equal(CpuPmObservationFailure.Busy, fake.Engine().Observe().Diagnostic!.Failure);
        Assert.Empty(fake.Codes);
        Assert.Equal(0, fake.Disposals);
    }

    [Fact]
    public void IdleIntervalAndSingleUseAreEnforcedAndCleanupFailureCannotClaimCompletion()
    {
        var fake = new Fake { SkipIdleDelay = true };
        var result = fake.Engine().Observe();
        Assert.False(result.Complete);
        Assert.Single(result.CopySamples());
        Assert.Equal(1, fake.Copies);
        fake = new Fake { CleanupThrows = true };
        var engine = fake.Engine();
        result = engine.Observe();
        Assert.Equal(CpuPmObservationFailure.CleanupFailed, result.Diagnostic!.Failure);
        Assert.False(result.Complete);
        int calls = fake.Codes.Count;
        Assert.False(engine.Observe().Complete);
        Assert.Equal(calls, fake.Codes.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(22)]
    public void CancellationDuringTransactionStopsAtExactCompletedFrame(int frame)
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new Fake();
        fake.OnInvoke = count => { if (count == frame) { cancellation.Cancel(); } };
        var result = fake.Engine().Observe(cancellation.Token);
        Assert.False(result.Complete);
        Assert.Equal(CpuPmObservationFailure.Cancelled, result.Diagnostic!.Failure);
        Assert.Equal(frame, fake.Codes.Count);
        Assert.Equal(1, fake.Disposals);
    }

    [Fact]
    public void CancellationInsideFreshProbePreservesPriorSampleAndPreventsNextExchange()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new Fake();
        int callsAtCancellation = -1;
        fake.OnReadFresh = () =>
        {
            // The interval boundary has passed; cancel inside the fresh probe
            // immediately before the next mailbox exchange, after sample one.
            if (fake.IdleDelays == 1 && ++fake.ProbesAfterIdle == 4)
            {
                callsAtCancellation = fake.Codes.Count;
                cancellation.Cancel();
            }
        };
        var result = fake.Engine().Observe(cancellation.Token);
        Assert.False(result.Complete);
        Assert.Equal(CpuPmObservationFailure.Cancelled, result.Diagnostic!.Failure);
        Assert.True(callsAtCancellation > 0);
        Assert.Equal(callsAtCancellation, fake.Codes.Count);
        Assert.Single(result.CopySamples());
        Assert.Equal(1, fake.Copies);
        Assert.Equal(1, fake.Disposals);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    public void FullSnapshotAdmissionFailureStopsAndRetainsOnlyCompletedSamples(int boundary, int retained)
    {
        var fake = new Fake();
        int callsAtFailure = -1;
        fake.OnSnapshotCheck = () =>
        {
            if (fake.SnapshotChecks != boundary) { return true; }
            callsAtFailure = fake.Codes.Count;
            return false;
        };
        var result = fake.Engine().Observe();
        Assert.False(result.Complete);
        Assert.Equal(CpuPmObservationStage.SnapshotAdmission, result.Diagnostic!.Stage);
        Assert.Equal(CpuPmObservationFailure.AdmissionDenied, result.Diagnostic.Failure);
        Assert.Equal(callsAtFailure, fake.Codes.Count);
        Assert.Equal(retained, result.CopySamples().Length);
        Assert.Equal(1, fake.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SlowOrCancelledFullSnapshotProbeCannotGrantAnotherIoctl(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new Fake();
        int callsAtProbe = -1;
        fake.OnSnapshotCheck = () =>
        {
            callsAtProbe = fake.Codes.Count;
            if (cancel) { cancellation.Cancel(); } else { fake.Now = TimeSpan.FromSeconds(90); }
            return true;
        };
        var result = fake.Engine().Observe(cancellation.Token);
        Assert.False(result.Complete);
        Assert.Equal(cancel ? CpuPmObservationFailure.Cancelled : CpuPmObservationFailure.BudgetExpired, result.Diagnostic!.Failure);
        Assert.Equal(callsAtProbe, fake.Codes.Count);
        Assert.Empty(result.CopySamples());
        Assert.Equal(1, fake.Disposals);
    }

    [Theory]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(11, 1)]
    [InlineData(12, 2)]
    public void EachCapTransportFailureStopsAtThatFrameAndPreservesOnlyReturnedWords(int frame, int retained)
    {
        var fake = new Fake { FailFrame = frame };
        var result = fake.Engine().Observe();
        Assert.False(result.Complete); Assert.Equal(CpuPmObservationStage.FusedCap, result.Diagnostic!.Stage);
        Assert.Equal(frame, fake.Codes.Count); Assert.Empty(fake.Commands); Assert.Equal(1, fake.Disposals);
        Assert.Equal(retained, (result.FusedCap?.A0F is null ? 0 : 1) + (result.FusedCap?.A0B is null ? 0 : 1));
        Assert.Null(result.FusedCap?.Candidate);
        if (frame is 10 or 12) { Assert.False((frame == 10 ? result.FusedCap!.A0F : result.FusedCap!.A0B)!.TransportSucceeded); }
    }

    [Theory]
    [InlineData("cancel", 10)]
    [InlineData("budget", 10)]
    [InlineData("drift", 10)]
    [InlineData("cancel", 12)]
    [InlineData("budget", 12)]
    [InlineData("drift", 12)]
    public void CapReturnedBeforePostReadLossRetainsRawWordButCannotGrantNextIo(string failure, int stopFrame)
    {
        using var cancellation = new CancellationTokenSource(); var fake = new Fake();
        fake.OnInvoke = frame =>
        {
            if (frame != stopFrame) { return; }
            if (failure == "cancel") { cancellation.Cancel(); }
            if (failure == "budget") { fake.Now = TimeSpan.FromSeconds(90); }
            if (failure == "drift") { fake.AdmissionFailure = "continuity"; }
        };
        var result = fake.Engine().Observe(cancellation.Token);
        Assert.False(result.Complete); Assert.Equal(stopFrame, fake.Codes.Count); Assert.Empty(fake.Commands);
        Assert.Equal(5u, result.FusedCap!.A0F!.RawValue); Assert.True(result.FusedCap.A0F.TransportSucceeded);
        var last = stopFrame == 10 ? result.FusedCap.A0F : result.FusedCap.A0B!;
        Assert.False(last.PostReadAdmissionPassed); Assert.Null(result.FusedCap.Candidate);
        if (stopFrame == 10) { Assert.Null(result.FusedCap.A0B); }
        else { Assert.True(result.FusedCap.A0F.PostReadAdmissionPassed); Assert.Equal(0xF0000000u, last.RawValue); }
        Assert.Equal(fake.Now.Ticks, last.MonotonicTimestamp);
        Assert.Equal(DateTimeOffset.UnixEpoch + fake.Now, last.CapturedUtc);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 0u, 10)]
    [InlineData(5u, 0xFFFFFFFFu, 12)]
    [InlineData(0u, 0u, 12)]
    public void InvalidCapIsRetainedButStopsBeforeAnyMailbox(uint first, uint second, int frames)
    {
        var fake = new Fake { CapFirst = first, CapSecond = second };
        var result = fake.Engine().Observe();
        Assert.False(result.Complete); Assert.Equal(frames, fake.Codes.Count); Assert.Empty(fake.Commands);
        Assert.Equal(first, result.FusedCap!.A0F!.RawValue);
        if (frames == 12) { Assert.Equal(second, result.FusedCap.A0B!.RawValue); }
        Assert.Null(result.FusedCap.Candidate); Assert.Equal(CpuPmObservationFailure.ReplyRejected, result.Diagnostic!.Failure);
    }

    [Fact]
    public void RangeCapturesFourRegistersOneCapabilityOnePmThenFixedMetadataUnderLease()
    {
        var fake = new Fake(); var metadata = new MetadataFake(fake);
        var result = fake.Engine(CpuPmObservationProfile.RangeSingleSample, metadata).Observe();
        Assert.True(result.Complete);
        Assert.Equal(CpuPmObservationProfile.RangeSingleSample, result.Profile);
        Assert.Single(result.CopySamples());
        Assert.Equal(new uint[] { 2, 0x82, 6, 0x65, 6 }, fake.Commands);
        Assert.Equal(0x0B5D1600u, result.FirmwareVersion);
        Assert.Equal(new uint[] { 0x03820A0F, 0x03820A0B, 0x03820A13, 0x03820A90 }, fake.CapSelections);
        Assert.Equal(new[] { "version", "minmax" }, metadata.Calls);
        Assert.Equal(0, fake.IdleDelays);
        Assert.True(result.RangeObservation!.Capability!.InvocationEntered);
        Assert.True(result.RangeObservation.Capability.ResponseAcknowledged);
        Assert.True(result.RangeObservation.Capability.PostReadAdmissionPassed);
        Assert.True(result.RangeObservation.MinMax!.PostAdmissionPassed);
        Assert.Null(result.RangeObservation.DValues);
        Assert.Equal(CpuLivePowerRangeMetadataApplicability.SourceMappedContinuousTypeOnePpt50001, result.RangeObservation.MetadataApplicability);
        Assert.True(result.RangeObservation.MetadataComplete);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x005D000Bu)]
    [InlineData(0x0B5D1700u)]
    [InlineData(uint.MaxValue)]
    public void WrongFirmwareStopsBeforeBaselineCopyOrMetadata(uint version)
    {
        var fake = new Fake { FirmwareVersion = version }; var metadata = new MetadataFake(fake);
        var result = fake.Engine(CpuPmObservationProfile.RangeSingleSample, metadata).Observe();
        Assert.False(result.Complete);
        Assert.Equal(CpuPmObservationStage.FirmwareVersion, result.Diagnostic!.Stage);
        Assert.Equal(CpuPmObservationFailure.UnsupportedVersion, result.Diagnostic.Failure);
        Assert.Equal(new uint[] { 2 }, fake.Commands);
        Assert.Equal(0, fake.Copies); Assert.Empty(metadata.Calls); Assert.Null(result.FirmwareVersion);
        Assert.Equal(1, fake.Disposals);
    }

    [Theory]
    [InlineData("firmware-ack")]
    [InlineData("firmware-native")]
    public void FirmwareRequiresAcknowledgementAndSuccessfulReturnedWord(string failure)
    {
        var fake = new Fake { WireFailure = failure }; var metadata = new MetadataFake(fake);
        var result = fake.Engine(CpuPmObservationProfile.RangeSingleSample, metadata).Observe();
        Assert.False(result.Complete);
        Assert.Equal(CpuPmObservationStage.FirmwareVersion, result.Diagnostic!.Stage);
        Assert.Equal(new uint[] { 2 }, fake.Commands);
        Assert.Equal(0, fake.Copies); Assert.Empty(metadata.Calls); Assert.Null(result.FirmwareVersion);
        Assert.Equal(1, fake.Disposals);
    }

    [Fact]
    public void EachNewRangeLeaseRequiresFreshFirmware()
    {
        var fake = new Fake(); var metadata = new MetadataFake(fake);
        Assert.True(fake.Engine(CpuPmObservationProfile.RangeSingleSample, metadata).Observe().Complete);
        var next = new Fake { FirmwareVersion = 0x0B5D1700 };
        Assert.False(next.Engine(CpuPmObservationProfile.RangeSingleSample, new MetadataFake(next)).Observe().Complete);
        Assert.Equal(new uint[] { 2 }, next.Commands); Assert.Equal(0, next.Copies);
    }

    [Theory]
    [InlineData("version", 1)]
    [InlineData("minmax", 2)]
    public void MetadataFailureRetainsCompletedPmAndStopsLaterGetters(string fail, int calls)
    {
        var fake = new Fake(); var metadata = new MetadataFake(fake) { Fail = fail };
        var result = fake.Engine(CpuPmObservationProfile.RangeSingleSample, metadata).Observe();
        Assert.False(result.Complete); Assert.Single(result.CopySamples());
        Assert.Equal(calls, metadata.Calls.Count);
        Assert.Equal(CpuPmObservationFailure.InvalidData, result.Diagnostic!.Failure);
        Assert.True(result.RangeObservation!.Capability!.PostReadAdmissionPassed);
    }

    [Fact]
    public void IncompleteMinMaxRetainsItsActualRawFieldsAndUnknownApplicabilityCannotComplete()
    {
        var fake = new Fake(); var metadata = new MetadataFake(fake) { Fail = "minmax" };
        var result = fake.Engine(CpuPmObservationProfile.RangeSingleSample, metadata).Observe();
        Assert.False(result.Complete); Assert.Single(result.CopySamples());
        var range = result.RangeObservation!;
        Assert.Equal(CpuLivePowerRangeReturnValueState.Absent, range.MinMax!.Raw!.ReturnValueState);
        Assert.Equal(125000u, range.MinMax.Raw.Max);
        Assert.False(range.MinMax.PostAdmissionPassed); Assert.Null(range.DValues);
        fake = new Fake(); metadata = new MetadataFake(fake);
        range = fake.Engine(CpuPmObservationProfile.RangeSingleSample, metadata).Observe().RangeObservation!;
        Assert.True(range.MetadataComplete);
        Assert.False((range with { MetadataApplicability = CpuLivePowerRangeMetadataApplicability.Unknown }).MetadataComplete);
        Assert.False(new CpuLivePowerRangeObservation(range.A0F, range.A0B, range.A13, range.A90, range.Capability,
            range.QueryVersion, null, range.MinMax).MetadataComplete);
        Assert.False((range with
        {
            DValues = new(new(0x50001, 0, 0, []), CpuLivePowerRangeMetadataStatus.Complete,
            range.MinMax!.Stages, default, 0, PostAdmissionPassed: true)
        }).MetadataComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataLateReturnCancellationOrDeadlineRetainsEvidenceAndStops(bool expired)
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new Fake(); var metadata = new MetadataFake(fake);
        metadata.AfterCall = () => { if (expired) { fake.Now = TimeSpan.FromSeconds(90); } else { cancellation.Cancel(); } };
        var result = fake.Engine(CpuPmObservationProfile.RangeSingleSample, metadata).Observe(cancellation.Token);
        Assert.False(result.Complete); Assert.Single(result.CopySamples());
        Assert.Single(metadata.Calls); Assert.NotNull(result.RangeObservation!.QueryVersion);
        Assert.False(result.RangeObservation.QueryVersion!.PostAdmissionPassed);
        Assert.Equal(expired ? CpuPmObservationFailure.BudgetExpired : CpuPmObservationFailure.Cancelled, result.Diagnostic!.Failure);
    }

    [Fact]
    public void IdleProfileNeverInvokesMetadataAndRangeWithoutReaderCannotAcquire()
    {
        var fake = new Fake(); var metadata = new MetadataFake(fake);
        Assert.True(fake.Engine(metadata: metadata).Observe().Complete);
        Assert.Empty(metadata.Calls); Assert.DoesNotContain(0x82u, fake.Commands); Assert.DoesNotContain(2u, fake.Commands);
        Assert.DoesNotContain(0x03820A13u, fake.CapSelections); Assert.DoesNotContain(0x03820A90u, fake.CapSelections);
        fake = new Fake();
        Assert.False(fake.Engine(CpuPmObservationProfile.RangeSingleSample).Observe().Complete);
        Assert.Equal(0, fake.Acquires); Assert.Empty(fake.Codes);
    }

    private sealed class MetadataFake(Fake fake) : IRz090528CpuLivePowerRangeMetadataReader
    {
        internal string? Fail;
        internal Action? AfterCall;
        internal List<string> Calls { get; } = [];
        private CpuLivePowerRangeMetadataCall<T> Call<T>(string name, T value, Action checkpoint) where T : class
        {
            checkpoint(); Assert.True(fake.Leased); Assert.Equal(1, fake.Copies);
            Assert.Equal(6u, fake.Commands[^1]); Calls.Add(name); AfterCall?.Invoke();
            return new(Fail == name ? null : value, Fail == name ? CpuLivePowerRangeMetadataStatus.InvalidData : CpuLivePowerRangeMetadataStatus.Complete,
                CpuLivePowerRangeMetadataStages.InvocationEntered | CpuLivePowerRangeMetadataStages.InvocationReturned
                    | CpuLivePowerRangeMetadataStages.OutputEnumerated | CpuLivePowerRangeMetadataStages.PropertiesRead | CpuLivePowerRangeMetadataStages.Disposed,
                fake.UtcNow, fake.MonotonicTimestamp, Raw: name == "minmax"
                    ? new(returnValue: Fail != name ? true : null, value: 0x50001, current: 0, min: 0, max: 125000, stepping: 1,
                        returnValueState: Fail == name ? CpuLivePowerRangeReturnValueState.Absent : CpuLivePowerRangeReturnValueState.PresentBoolean) : null);
        }
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion> QueryVersion(Action checkpoint) => Call("version", new CpuLivePowerRangeVersion(7, false), checkpoint);
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues> GetDValues(Action checkpoint) =>
            throw new InvalidOperationException("The pinned type-One PPT entry has no discrete getter acquisition.");
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax> GetMinMaxSteppingValues(Action checkpoint) => Call("minmax", new CpuLivePowerRangeMinMax(0x50001, 0, 0, 125000, 1), checkpoint);
    }

    private sealed class Fake : IRz090528CpuQueryIoctl, IRz090528CpuQuerySynchronization,
        ICpuPmObservationSessionAdmission, ICpuPmObservationClock
    {
        private uint selected, command;
        internal bool Leased { get; private set; }
        private bool leased, awaitingPostCopySelection;
        private readonly Guid session = Guid.NewGuid(), lifetime = Guid.NewGuid();
        internal string? AdmissionFailure, WireFailure;
        internal string Bios = "2.02";
        internal int FailCopyNumber, Copies, Acquires, Disposals, IdleDelays, PostCopyResponseSelections;
        internal bool MutexBusy, SkipIdleDelay, CleanupThrows;
        internal TimeSpan Now, AcquireElapsed;
        internal Action? AfterCopy;
        internal Action? OnReadFresh;
        internal Func<bool>? OnSnapshotCheck;
        internal int SnapshotChecks;
        internal int ProbesAfterIdle;
        internal int FailFrame;
        internal uint CapFirst = 5, CapSecond = 0xF0000000;
        internal uint FirmwareVersion = 0x0B5D1600;
        internal uint IdleResponse = 1, CompletionResponse = 1;
        internal List<uint> CapSelections { get; } = [];
        internal Action<int>? OnInvoke;
        internal List<uint> Codes { get; } = [];
        internal List<uint> Commands { get; } = [];
        internal List<int> Threads { get; } = [];
        internal Rz090528CpuPmObservationEngine Engine(CpuPmObservationProfile profile = CpuPmObservationProfile.IdleThreeSamples,
            IRz090528CpuLivePowerRangeMetadataReader? metadata = null) => new(this, this, this, this, profile, metadata);
        public TimeSpan MonotonicNow => Now;
        public long MonotonicTimestamp => Now.Ticks;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Now;
        public void DelayOneSecond(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Assert.True(leased); IdleDelays++; if (!SkipIdleDelay) { Now += TimeSpan.FromSeconds(1); } }
        public void Delay(TimeSpan duration, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Assert.Equal(TimeSpan.FromMilliseconds(1), duration); Assert.True(leased); Now += duration; }
        public IDisposable? Acquire(TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(TimeSpan.FromSeconds(20), timeout);
            Acquires++;
            Now += AcquireElapsed;
            if (MutexBusy) { return null; }
            leased = Leased = true;
            Threads.Add(Environment.CurrentManagedThreadId);
            return new Lease(this);
        }
        private sealed class Lease(Fake fake) : IDisposable
        {
            public void Dispose()
            {
                fake.Threads.Add(Environment.CurrentManagedThreadId);
                fake.Disposals++;
                fake.leased = fake.Leased = false;
                if (fake.CleanupThrows) { throw new IOException("private cleanup evidence"); }
            }
        }
        public CpuPmSessionEvidence ReadFresh()
        {
            OnReadFresh?.Invoke();
            var files = Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(entry =>
                new CpuBackendFileMetadata(entry.Key, entry.Value, true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray();
            var backend = new InstalledCpuBackendSnapshot(files, true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, true);
            return new(new("RZ09-0528", 0x1532, AdmissionFailure == "usb" ? (ushort)0x02E0 : (ushort)0x02C6, "2.02"),
                new(new(AdmissionFailure == "manufacturer" ? "other" : "Razer", "Blade 16 - RZ09-0528", AdmissionFailure == "sku" ? "wrong" : "RZ09-05289EN4", Bios), null),
                new("AuthenticAMD", 0x1A, AdmissionFailure == "cpu" ? 0x25 : 0x24, 0),
                AdmissionFailure == "extended" ? 0x80000000u : 0x80000001u,
                AdmissionFailure == "package" ? 0 : 1,
                AdmissionFailure == "backend" ? backend with { Running = false } : backend,
                AdmissionFailure != "controllers",
                AdmissionFailure == "continuity" ? CpuPmSessionContinuity.Interrupted : CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime,
                session, lifetime);
        }
        public bool ValidateSnapshotBoundary()
        {
            SnapshotChecks++;
            return OnSnapshotCheck?.Invoke() ?? true;
        }
        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength)
        {
            Assert.True(leased);
            Threads.Add(Environment.CurrentManagedThreadId);
            Codes.Add(code);
            OnInvoke?.Invoke(Codes.Count);
            byte[] reply = new byte[outputLength];
            if (code == 0x81112F18)
            {
                Put(reply, 24, WireFailure == "count" ? 0u : 3u);
                if (outputLength > 28)
                {
                    uint[][] nodes = [[0, 0, 0, CpuQueryPciTopology.RootId], [0, 0x18, 0, CpuQueryPciTopology.NodeId], [0, 0x18, 1, CpuQueryPciTopology.OrientationId]];
                    for (int i = 0; i < 3; i++) { for (int j = 0; j < 4; j++) { Put(reply, 28 + i * 52 + j * 4, nodes[i][j]); } }
                    if (WireFailure == "topology") { Put(reply, 28 + 12, CpuQueryPciTopology.NodeId); }
                }
            }
            else if (code == 0x81112FF8)
            {
                if (Word(input, 0) == 0)
                {
                    selected = Word(input, 4);
                    if (selected is 0x03820A0F or 0x03820A0B or 0x03820A13 or 0x03820A90) { CapSelections.Add(selected); }
                    if (awaitingPostCopySelection && selected == 0x03B10A80)
                    { awaitingPostCopySelection = false; PostCopyResponseSelections++; }
                }
                else if (selected == 0x03B10A20)
                {
                    command = Word(input, 4);
                    Commands.Add(command);
                    if (WireFailure == "refresh-failure" && command == 0x65) { return new(false, []); }
                }
            }
            else if (code == 0x81112F24)
            {
                input.CopyTo(reply, 0);
                uint d = Word(input, 4), f = Word(input, 8), offset = Word(input, 12);
                uint value = offset == 0 ? d == 0 ? CpuQueryPciTopology.RootId : f == 0 ? CpuQueryPciTopology.NodeId : CpuQueryPciTopology.OrientationId
                    : offset == 0x140 ? WireFailure == "mask" ? 0xFFFFu : 0xFF55u
                    : selected == 0x03820AB0 ? (WireFailure == "fuse" ? ~0xFFFFu : ~0xFF55u) << 5
                    : selected == 0x03820A0F ? CapFirst : selected == 0x03820A0B ? CapSecond
                    : selected == 0x03820A13 ? 1u : selected == 0x03820A90 ? 4u
                    : selected == 0x03B10A80 ? command == 0 ? IdleResponse : command == 2 && WireFailure == "firmware-ack" ? 2u : CompletionResponse
                    : selected == 0x03B10A88 ? command == 2 ? FirmwareVersion : command == 6 ? WireFailure == "version" || WireFailure == "post-version" && Copies > 0 ? 0x005D000Cu : 0x005D000Bu : 0x12345678u
                    : throw new InvalidOperationException("stale index selection");
                if (selected == 0x03B10A88 && command == 2 && WireFailure == "firmware-native") { return new(false, reply); }
                if (WireFailure == "root" && offset == 0 && d == 0) { value = CpuQueryPciTopology.NodeId; }
                Put(reply, 20, value);
            }
            else if (code == 0x81112F08)
            {
                Assert.Equal(4, input.Length);
                Assert.Equal(0xD50u, Word(input, 0));
                Assert.Equal(0xD54, outputLength);
                Copies++;
                selected = 0;
                awaitingPostCopySelection = true;
                AfterCopy?.Invoke();
                if (WireFailure == "copy-throw") { throw new IOException("private native evidence"); }
                if (WireFailure == "copy-failure" || Copies == FailCopyNumber) { return new(false, reply); }
                Put(reply, 0, WireFailure == "prefix" ? 0u : 0xD50u);
                for (int i = 0; i < 6; i++) { Put(reply, 4 + i * 4, 0x3F800000); }
                if (WireFailure == "length") { return new(true, reply[..^1]); }
            }
            else { throw new InvalidOperationException("unapproved IOCTL"); }
            return new(Codes.Count != FailFrame, reply);
        }
        private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
}
