using System.Buffers.Binary;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class FunctionRowDriverTests
{
    private const RazerKeyboardHookPurpose Purpose = RazerKeyboardHookPurpose.FunctionRow;
    private const RazerBladeInputDeviceKind Device = RazerBladeInputDeviceKind.Rz09_0581;

    [Fact]
    public void FunctionSelectorsAreClosedAndDefaultContractRemainsAdOnly()
    {
        Assert.Equal(new ushort[] { 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58 },
            RazerSnapTapDriverProtocol.ScanCodes(Purpose));
        foreach (ushort scan in RazerSnapTapDriverProtocol.ScanCodes(Purpose))
        {
            byte[] hook = RazerSnapTapDriverProtocol.EncodeHook(scan, Purpose);
            Assert.Equal(292, hook.Length);
            Assert.Equal(scan, BinaryPrimitives.ReadUInt16LittleEndian(hook.AsSpan(10)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(hook.AsSpan(4)));
            Assert.Equal(hook[..32], RazerSnapTapDriverProtocol.EncodeClearHook(scan, Purpose));
            Assert.Equal(hook[..32], RazerSnapTapDriverProtocol.EncodeOutput(new(scan, true), Purpose));
            byte[] up = RazerSnapTapDriverProtocol.EncodeOutput(new(scan, false), Purpose);
            Assert.Equal(1, up[12]);
            Assert.Throws<ArgumentOutOfRangeException>(() => RazerSnapTapDriverProtocol.EncodeHook(scan));
        }
        foreach (ushort scan in new ushort[] { 0, 0x1E, 0x20, 0x2C, 0x45, 0x56, 0x59, 0xFFFF })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RazerSnapTapDriverProtocol.EncodeHook(scan, Purpose));
            Assert.Throws<ArgumentOutOfRangeException>(() => RazerSnapTapDriverProtocol.EncodeClearHook(scan, Purpose));
            Assert.Throws<ArgumentOutOfRangeException>(() => RazerSnapTapDriverProtocol.EncodeOutput(new(scan, true), Purpose));
        }
    }

    [Theory]
    [InlineData(0x3B, 0, 0)]
    [InlineData(0x58, 1, 0)]
    [InlineData(0x58, 2, 2)]
    [InlineData(0x2C, 0, 2)]
    [InlineData(0x1E, 0, 2)]
    public void NotificationsRejectUnknownSelectorsAndLeaveDisjointOwnerAlone(
        ushort scan, ushort flags, int expectedValue)
    {
        var expected = (RazerSnapTapInputDecodeResult)expectedValue;
        byte[] packet = new byte[304];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(22), scan);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(24), flags);
        Assert.Equal(expected, RazerSnapTapDriverProtocol.DecodeInput(packet, packet.Length, out var stroke, Purpose));
        if (expected == RazerSnapTapInputDecodeResult.Decoded) { Assert.Equal(new(scan, flags == 0), stroke); }
        Assert.Equal(RazerSnapTapInputDecodeResult.Malformed,
            RazerSnapTapDriverProtocol.DecodeInput(packet, packet.Length - 1, out _, Purpose));
    }

    [Fact]
    public void MediaHoldKeepsSuppressionAcrossFnModeAndKnowledgeChanges()
    {
        List<SnapTapKeyStroke> sent = [];
        List<FunctionRowInputEvent> events = [];
        var processor = new FunctionRowDriverInputProcessor(sent.Add, events.Add, () => { });
        Assert.True(processor.Handle(new(0x42, true), Device, false, FunctionKeyPrimaryMode.MultimediaKeys, true));
        Assert.True(processor.Handle(new(0x42, true), Device, true, FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.True(processor.Handle(new(0x42, true), Device, false, null, false));
        Assert.True(processor.Handle(new(0x42, false), Device, true, null, false));
        Assert.Empty(sent);
        Assert.Equal(new[] { FunctionRowInputPhase.Down, FunctionRowInputPhase.Repeat, FunctionRowInputPhase.Up },
            events.Select(item => item.Phase));
        Assert.False(processor.HasTrackedKeys);
    }

    [Fact]
    public void PlainHoldForwardsEveryOriginalOnceEvenWhenFnOrModeChanges()
    {
        List<SnapTapKeyStroke> sent = [];
        List<FunctionRowInputEvent> events = [];
        var processor = new FunctionRowDriverInputProcessor(sent.Add, events.Add, () => { });
        Assert.True(processor.Handle(new(0x3B, true), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.True(processor.Handle(new(0x3B, true), Device, true, FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.True(processor.Handle(new(0x3B, false), Device, false, null, false));
        Assert.Equal(new SnapTapKeyStroke[] { new(0x3B, true), new(0x3B, true), new(0x3B, false) }, sent);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainFunctionKeyBeforeAnyAdInputUsesTheCurrentLoopTarget(bool snapSelected)
    {
        var ad = new SnapTapDriverInputProcessor(_ => throw new InvalidOperationException());
        Assert.Equal((ushort)0, ad.Winner);
        // The real loop snapshots targetWindow/targetLayout whenever Winner is
        // zero, including Fn-only and combined sessions before any A/D press.
        nint targetWindow = 7;
        nint targetLayout = 9;
        nint currentWindow = targetWindow;
        nint currentLayout = targetLayout;
        var purpose = snapSelected ? RazerKeyboardHookPurpose.Combined : Purpose;
        List<SnapTapKeyStroke> sent = [];
        var function = new FunctionRowDriverInputProcessor(stroke =>
        {
            _ = RazerSnapTapDriverProtocol.EncodeOutput(stroke, purpose);
            if (!SnapTapDriverLoop.IsOutputTargetCurrent(targetWindow, targetLayout,
                currentWindow, currentLayout, outputAllowed: true))
            {
                throw new InvalidOperationException("Foreground changed before output.");
            }
            sent.Add(stroke);
        }, _ => Assert.Fail(), () => { });
        Assert.True(function.Handle(new(0x3B, true), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.Single(sent);
        currentLayout++;
        Assert.False(function.Handle(new(0x3B, false), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.True(function.OutputUncertain);
        currentLayout = targetLayout;
        Assert.False(function.Handle(new(0x3B, false), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.Single(sent);
        Assert.False(SnapTapDriverLoop.IsOutputTargetCurrent(targetWindow, targetLayout,
            currentWindow + 1, currentLayout, outputAllowed: true));
    }

    [Fact]
    public void UnknownModeAndStoppingNewPressesCannotStartMedia()
    {
        List<SnapTapKeyStroke> sent = [];
        List<FunctionRowInputEvent> events = [];
        var processor = new FunctionRowDriverInputProcessor(sent.Add, events.Add, () => { });
        Assert.True(processor.Handle(new(0x43, true), Device, false, FunctionKeyPrimaryMode.MultimediaKeys, false));
        Assert.True(processor.Handle(new(0x43, false), Device, false, FunctionKeyPrimaryMode.MultimediaKeys, true));
        Assert.True(processor.Handle(new(0x43, true), Device, false, FunctionKeyPrimaryMode.MultimediaKeys, true, stopping: true));
        Assert.Equal(3, sent.Count);
        Assert.Empty(events);
    }

    [Fact]
    public void AmbiguousOutputStopsAllFurtherOutputAndCannotBecomeCleanOnRelease()
    {
        int submissions = 0;
        var processor = new FunctionRowDriverInputProcessor(_ => { submissions++; throw new IOException("uncertain"); },
            _ => Assert.Fail(), () => { });
        Assert.False(processor.Handle(new(0x3B, true), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.False(processor.Handle(new(0x3B, false), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.True(processor.OutputUncertain);
        Assert.True(processor.Faulted);
        Assert.True(processor.Failure?.MayHaveSubmitted);
        Assert.Equal(1, submissions);
    }

    [Fact]
    public void MediaCleanupPublishesMatchingUpAndResetsWithoutGuessingKeyboardOutput()
    {
        List<FunctionRowInputEvent> events = [];
        int resets = 0;
        var processor = new FunctionRowDriverInputProcessor(_ => Assert.Fail(), events.Add, () => resets++);
        processor.Handle(new(0x43, true), Device, false, FunctionKeyPrimaryMode.MultimediaKeys, true);
        processor.ReleaseMedia();
        Assert.Equal(new[] { FunctionRowInputPhase.Down, FunctionRowInputPhase.Up }, events.Select(item => item.Phase));
        Assert.Equal(1, resets);
        Assert.True(processor.HasTrackedKeys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StopDrainsObservedReleaseOrStopsAtItsBound(bool releaseArrives)
    {
        var time = new ManualTime();
        List<SnapTapKeyStroke> sent = [];
        var processor = new FunctionRowDriverInputProcessor(sent.Add, _ => { }, () => { });
        processor.Handle(new(0x3B, true), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true);
        int reads = 0;
        bool clean = FunctionRowDriverLoop.Pump(processor, () =>
            {
                reads++;
                time.Timestamp += 1000;
                return releaseArrives ? new SnapTapKeyStroke(0x3B, false) : null;
            }, () => true, () => false, () => FunctionKeyPrimaryMode.FunctionKeys,
            () => new(KeyboardDriverModeKnowledge.Active, 1), () => Device, Device, time);
        Assert.Equal(releaseArrives, clean);
        Assert.InRange(reads, 1, 2);
        Assert.Equal(releaseArrives ? 2 : 1, sent.Count);
    }

    [Fact]
    public void CombinedSessionOwnsAndRestoresOneUnionWithOneSubscription()
    {
        var transport = new FakeTransport();
        List<IReadOnlyList<byte[]>> evidence = [];
        var session = new RazerSnapTapHookSession(transport, RazerKeyboardHookPurpose.Combined, evidence.Add);
        Assert.True(session.Start());
        Assert.Equal(14, transport.Hooks.Count);
        Assert.Equal(1, transport.Subscriptions);
        Assert.Equal(Enumerable.Range(1, 14), evidence.Select(records => records.Count));
        Assert.True(session.Stop());
        Assert.True(SnapTapDriverSupervisor.TryReconcileHooks(transport, session.BaselineHooks,
            _ => { }, out _, RazerKeyboardHookPurpose.Combined, evidence[^1]));
        Assert.Empty(transport.Hooks);
    }

    [Fact]
    public void CombinedGuardianRefusesUnconfirmedSetAndPreservesUnrelatedRecords()
    {
        var transport = new FakeTransport();
        byte[] unrelated = new byte[292];
        unrelated[4] = 2;
        transport.Hooks.Add(unrelated);
        var session = new RazerSnapTapHookSession(transport, RazerKeyboardHookPurpose.Combined);
        Assert.True(session.Start());
        Assert.True(session.Stop());
        Assert.False(SnapTapDriverSupervisor.TryReconcileHooks(transport, session.BaselineHooks,
            _ => Assert.Fail(), out _, RazerKeyboardHookPurpose.Combined, session.OwnedHooks.Take(13).ToArray()));
        Assert.Equal(15, transport.Hooks.Count);
        Assert.True(SnapTapDriverSupervisor.TryReconcileHooks(transport, session.BaselineHooks,
            _ => { }, out _, RazerKeyboardHookPurpose.Combined, session.OwnedHooks));
        Assert.Equal(unrelated, Assert.Single(transport.Hooks));
    }

    [Fact]
    public void PreexistingOrUnconfirmedFunctionRecordsCannotBecomeOwned()
    {
        var transport = new FakeTransport();
        transport.Hooks.Add(RazerSnapTapDriverProtocol.EncodeHook(0x42, Purpose));
        var session = new RazerSnapTapHookSession(transport, Purpose);
        Assert.False(session.Start());
        Assert.Empty(session.OwnedHooks);
        Assert.False(SnapTapDriverSupervisor.TryReconcileHooks(transport, [], _ => Assert.Fail(), out _, Purpose, []));
        Assert.Single(transport.Hooks);
    }

    [Fact]
    public void InterruptedJournalRequiresDifferentBootAndNoFunctionSelectors()
    {
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal("one", "one", []));
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal(null, "two", []));
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal("one", "two",
            [RazerSnapTapDriverProtocol.EncodeHook(0x42, Purpose)]));
        Assert.False(SnapTapDriverSupervisor.CanDiscardJournal("one", "two",
            [RazerSnapTapDriverProtocol.EncodeHook(0x1E)]));
        Assert.True(SnapTapDriverSupervisor.CanDiscardJournal("one", "two", []));
    }

    [Fact]
    public void OwnershipJournalLegacyScopeDefaultsToAdAndCombinedRoundTrips()
    {
        var legacy = JsonSerializer.Deserialize<SnapTapDriverSupervisor.OwnershipJournal>(
            """{"BootIdentity":"boot","BaselineHooks":[]}""");
        Assert.NotNull(legacy);
        Assert.Equal(KeyboardDriverFeatures.SnapTap, legacy.Features);
        Assert.True(SnapTapDriverSupervisor.IsValidOwnershipJournalScope("snaptap-driver-ownership.json", legacy.Features));
        var combined = new SnapTapDriverSupervisor.OwnershipJournal("boot", [],
            KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow);
        var restored = JsonSerializer.Deserialize<SnapTapDriverSupervisor.OwnershipJournal>(JsonSerializer.Serialize(combined));
        Assert.NotNull(restored);
        Assert.Equal(combined.Features, restored.Features);
        Assert.True(SnapTapDriverSupervisor.IsValidOwnershipJournalScope("snaptap-driver-ownership.json", restored.Features));
        Assert.False(SnapTapDriverSupervisor.IsValidOwnershipJournalScope("function-row-driver-ownership.json", legacy.Features));
        Assert.True(SnapTapDriverSupervisor.IsValidOwnershipJournalScope("function-row-driver-ownership.json", KeyboardDriverFeatures.FunctionRow));
        Assert.False(SnapTapDriverSupervisor.IsValidOwnershipJournalScope("snaptap-driver-ownership.json", KeyboardDriverFeatures.FunctionRow));
        Assert.False(SnapTapDriverSupervisor.IsValidOwnershipJournalScope("snaptap-driver-ownership.json", (KeyboardDriverFeatures)4));
    }

    [Fact]
    public void FixedCombinedScopeRequiresExactlyReviewedFeatureMask()
    {
        Assert.Equal(RazerKeyboardHookPurpose.Combined,
            RazerKeyboardHookScope.Purpose(KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow));
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerKeyboardHookScope.Purpose(KeyboardDriverFeatures.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerKeyboardHookScope.Purpose((KeyboardDriverFeatures)4));
        Assert.Equal(14, RazerSnapTapDriverProtocol.ScanCodes(RazerKeyboardHookPurpose.Combined).Count);
    }

    [Fact]
    public void ProductionAdmissionRejectsEveryPreexistingGlobalHookBeforeEnablingSubscription()
    {
        Assert.True(SnapTapDriverSupervisor.CanStartWithHooks([]));
        Assert.False(SnapTapDriverSupervisor.CanStartWithHooks([RazerSnapTapDriverProtocol.EncodeHook(0x1E)]));
        Assert.False(SnapTapDriverSupervisor.CanStartWithHooks([RazerSnapTapDriverProtocol.EncodeHook(0x42, Purpose)]));
        byte[] mouse = new byte[292];
        mouse[4] = 2;
        Assert.False(SnapTapDriverSupervisor.CanStartWithHooks([mouse]));
        byte[] unknownKeyboard = RazerSnapTapDriverProtocol.EncodeHook(0x1E);
        unknownKeyboard[10] = 0x2C;
        Assert.False(SnapTapDriverSupervisor.CanStartWithHooks([unknownKeyboard]));
    }

    [Fact]
    public void EitherSingleFeatureRefusesOrphanRecordsOfOtherFeature()
    {
        var function = new FakeTransport();
        function.Hooks.Add(RazerSnapTapDriverProtocol.EncodeHook(0x1E));
        Assert.False(new RazerSnapTapHookSession(function, Purpose).Start());
        Assert.Equal(0, function.Subscriptions);
        var snap = new FakeTransport();
        snap.Hooks.Add(RazerSnapTapDriverProtocol.EncodeHook(0x42, Purpose));
        Assert.False(new RazerSnapTapHookSession(snap).Start());
        Assert.Equal(0, snap.Subscriptions);
    }

    [Fact]
    public void OwnershipEvidenceMustMatchExactChildFreshnessPurposeAndUniqueCanonicalRecords()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        byte[][] valid = [RazerSnapTapDriverProtocol.EncodeHook(0x1E), RazerSnapTapDriverProtocol.EncodeHook(0x42, Purpose)];
        bool Valid(int pid, DateTimeOffset updated, IReadOnlyList<byte[]>? hooks,
            RazerKeyboardHookPurpose purpose = RazerKeyboardHookPurpose.Combined) =>
            RazerKeyboardOwnedHookEvidence.IsValid(pid, updated, hooks, 123, start, purpose);
        Assert.True(Valid(123, start, valid));
        Assert.False(Valid(124, start, valid));
        Assert.False(Valid(123, start.AddTicks(-1), valid));
        Assert.False(Valid(123, start, null));
        Assert.False(Valid(123, start, [valid[0], valid[0]]));
        Assert.False(Valid(123, start, valid, Purpose));
        byte[] changed = (byte[])valid[1].Clone();
        changed[16] = 1;
        Assert.False(Valid(123, start, [changed]));
    }

    [Fact]
    public void OwnershipEvidenceWriteFailurePreventsSubscriptionAndFurtherHookWrites()
    {
        var transport = new FakeTransport();
        var session = new RazerSnapTapHookSession(transport, RazerKeyboardHookPurpose.Combined,
            _ => throw new IOException("Evidence write failed."));
        Assert.False(session.Start());
        Assert.True(session.FailureMayHaveSubmitted);
        Assert.Single(session.OwnedHooks);
        Assert.Single(transport.Hooks);
        Assert.Equal(0, transport.Subscriptions);
    }

    [Fact]
    public async Task QueuedMediaFailureBlocksSubsequentPressesBeforeWorkerPollAndAllowsCleanup()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<FunctionRowInputEvent> executed = [];
        int resets = 0;
        var guard = new FunctionRowDriverActionGuard(async (input, _) =>
        {
            executed.Add(input);
            if (input.Phase == FunctionRowInputPhase.Down)
            {
                firstStarted.SetResult();
                await failFirst.Task;
                throw new IOException("Action completion is uncertain.");
            }
            released.SetResult();
        }, _ => { resets++; return Task.CompletedTask; });
        var pump = new FunctionRowInputPump(guard.ExecuteAsync, guard.ResetAsync);
        Assert.True(pump.TryEnqueue(new(0x42, FunctionRowAction.DisplayBrightnessDown, FunctionRowInputPhase.Down)));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pump.TryEnqueue(new(0x43, FunctionRowAction.DisplayBrightnessUp, FunctionRowInputPhase.Down)));
        Assert.True(pump.TryEnqueue(new(0x42, FunctionRowAction.DisplayBrightnessDown, FunctionRowInputPhase.Repeat)));
        Assert.True(pump.TryEnqueue(new(0x42, FunctionRowAction.DisplayBrightnessDown, FunctionRowInputPhase.Up)));
        failFirst.SetResult();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pump.DisposeAsync();
        Assert.True(guard.Failed);
        Assert.Equal(new[] { FunctionRowInputPhase.Down, FunctionRowInputPhase.Up }, executed.Select(input => input.Phase));
        Assert.Equal(1, resets);
    }

    private sealed class ManualTime : TimeProvider
    {
        internal long Timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }

    private sealed class FakeTransport : IRazerSnapTapHookTransport
    {
        internal List<byte[]> Hooks { get; } = [];
        internal int Subscriptions;
        public IReadOnlyList<byte[]> ReadHooks() => Hooks.Select(hook => (byte[])hook.Clone()).ToArray();
        public void SetHook(byte[] hook) => Hooks.Add((byte[])hook.Clone());
        public void ClearHook(byte[] clear) => Hooks.RemoveAll(hook => hook.AsSpan(0, 32).SequenceEqual(clear));
        public void SetHookEnabled(bool enabled) { Assert.True(enabled); Subscriptions++; }
        public void CloseSubscription() { }
    }
}
