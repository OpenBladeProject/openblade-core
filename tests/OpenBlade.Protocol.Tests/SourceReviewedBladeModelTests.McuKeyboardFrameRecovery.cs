using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

// Ownership, recovery and encoder boundaries of the shared RZ09-0301/0421/0423/0427
// keyboard frame contract (evidence/source-reviewed-blades/2026-11-19-rz09-*-keyboard-frames.json).
public sealed partial class SourceReviewedBladeModelTests
{
    public static TheoryData<int, string> McuUnobservedSelectionReads
    {
        get
        {
            var data = new TheoryData<int, string>();
            foreach (int product in new[] { 0x0245, 0x028A, 0x028B, 0x028C })
            {
                data.Add(product, nameof(HidSessionFailureKind.QueuedCanceled));
                data.Add(product, nameof(HidSessionFailureKind.Exhausted));
                data.Add(product, nameof(HidSessionFailureKind.Disposed));
                data.Add(product, nameof(ObjectDisposedException));
            }
            return data;
        }
    }

    // The 03/0B handler clears staging only when row, start and end are all
    // 0xFE. The encoder sends rows 0..5, start 0 and end 15, so it can never
    // form that sentinel; RGB bytes are never compared with it.
    [Fact]
    public void McuFrames_EncoderNeverFormsTheStagingClearSentinelAndPassesRgbFeThrough()
    {
        RgbColor[] sentinelColors = Enumerable.Repeat(new RgbColor(0xFE, 0xFE, 0xFE), 96).ToArray();
        RgbColor[] mixed = Enumerable.Range(0, 96)
            .Select(index => new RgbColor(0xFE, (byte)index, index % 2 == 0 ? (byte)0xFE : (byte)1)).ToArray();
        foreach (RgbColor[] colors in new[] { sentinelColors, mixed })
        {
            for (int row = 0; row < ExperimentalBladeLightingFrame.RowCount; row++)
            {
                byte[] payload = ExperimentalBladeLightingFrame.CreateRow(row, colors, padded: false);
                byte[] report = RazerReport.Create(31, 3, 0x0B, payload).Encode();
                byte[] arguments = report.AsSpan(8, payload.Length).ToArray();
                Assert.Equal(payload, arguments);
                Assert.Equal(new byte[] { 0xFF, (byte)row, 0, 15 }, arguments[..4]);
                Assert.NotEqual(0xFE, arguments[1]);
                Assert.NotEqual(0xFE, arguments[2]);
                Assert.NotEqual(0xFE, arguments[3]);
                Assert.Equal(colors.Skip(row * 16).Take(16).SelectMany(color => new[] { color.Red, color.Green, color.Blue }),
                    arguments[4..]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_UniformFeColorsReachTheRowsUnchanged(int product)
    {
        var session = new Session { McuLighting = McuFrameBaseline.ToArray() };
        await using var port = CreateMcuFrames(product, session);
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 60), default);

        await port.PushUniformLightingFrameAsync(new(new RgbColor(0xFE, 0xFE, 0xFE)), default);

        RazerCommandSpec[] rows = session.Commands.Where(command => command.ClassId == 3 && command.CommandId == 0x0B).ToArray();
        Assert.Equal(6, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.DoesNotContain((byte)0xFE, row.Arguments.Span[1..4].ToArray());
            Assert.All(row.Arguments.Span[4..].ToArray(), value => Assert.Equal(0xFE, value));
        });
    }

    // Replacing one live sampled owner with another keeps the journaled
    // baseline. The owned intent is not a restore destination.
    [Theory]
    [InlineData(0x0245, false)]
    [InlineData(0x0245, true)]
    [InlineData(0x028A, true)]
    public async Task McuFrames_ReplacingALiveSampledOwnerKeepsItsJournaledBaseline(int product, bool afterFirstFrame)
    {
        using var storage = new McuFrameJournalFixture(product);
        var session = new Session { Brightness = 129, McuLighting = McuFrameBaseline.ToArray() };
        var ownership = new ExperimentalLightingOwnershipState { Journal = storage.Journal };
        await using var port = CreateMcuFrames(product, session, ownership: ownership);
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 60), default);
        ExperimentalLightingBaseline original = Assert.IsType<ExperimentalLightingBaseline>(storage.Journal.Load().Baseline);
        Assert.Equal(new ExperimentalLightingBaseline(LightingSettingsFactory.BreathingSingle(new(11, 22, 33), 51), 129),
            original);
        if (afterFirstFrame)
        {
            await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
        }

        await port.SetLightingAsync(LightingSettingsFactory.AudioMeter(1, 60), default);

        Assert.Equal(LightingRendererOwnershipState.AudioMeter, port.OwnershipState);
        Assert.Equal(original, ownership.Baseline);
        Assert.Equal(original, storage.Journal.Load().Baseline);
        await port.PushUniformLightingFrameAsync(new(new RgbColor(1, 2, 3)), default);
        await port.RelinquishRendererAsync(default);
        Assert.Equal(McuFrameBaseline, session.McuLighting);
        Assert.Equal(129, session.Brightness);
        Assert.Null(storage.Journal.Load().Baseline);
        AssertMcuFrameContract(session);
    }

    [Fact]
    public async Task Rz090529_ReplacingALiveSampledOwnerKeepsItsJournaledBaseline()
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session { Brightness = 129, McuLighting = [0, 5, 2, 0, 0, 1, 11, 22, 33, 0, 0, 0] };
        var ownership = new ExperimentalLightingOwnershipState { Journal = storage.Journal };
        await using var port = CreateRz090529Frames(session, ownership: ownership);
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
        ExperimentalLightingBaseline original = Assert.IsType<ExperimentalLightingBaseline>(storage.Journal.Load().Baseline);
        await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);

        await port.SetLightingAsync(LightingSettingsFactory.AudioMeter(1, 50), default);

        Assert.Equal(LightingRendererOwnershipState.AudioMeter, port.OwnershipState);
        Assert.Equal(original, ownership.Baseline);
        Assert.Equal(original, storage.Journal.Load().Baseline);
        await port.RelinquishRendererAsync(default);
        Assert.Equal(new byte[] { 0, 5, 2, 0, 0, 1, 11, 22, 33, 0, 0, 0 }, session.McuLighting);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    // After a GPIO bit-8 edge or an MCU reset the firmware reports the saved
    // effect. When that is exactly the journaled baseline, restoration still
    // completes through the flag-clearing legacy setter: the GPIO edge leaves
    // the custom flag and staging in place, so no write would show a stale frame.
    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_ReportedBaselineCompletesRestoreThroughTheFlagClearingSetter(int product)
    {
        using var storage = new McuFrameJournalFixture(product);
        var session = new Session { McuLighting = McuFrameBaseline.ToArray() };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = CreateMcuFrames(product, session, policy, new() { Journal = storage.Journal });
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        session.McuLighting = McuFrameBaseline.ToArray();
        Assert.True(session.McuCustomOverlay);
        int before = session.Commands.Count;

        Assert.Equal(LightingRendererOwnershipState.Fire, await port.RelinquishRendererAsync(default));

        Assert.Contains("03/0A [3,1,11,22,33]", session.Commands.Skip(before).Select(DescribeMcu));
        Assert.False(session.McuCustomOverlay);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
        Assert.Contains(policy.Snapshot().Observations, observation => observation.Outcome == "ExternallyRestoredToBaseline");
        AssertMcuFrameContract(session);
    }

    // The monitor still writes nothing when mode 8 is lost. A later renderer
    // request that freshly reads exactly the journaled baseline is no longer
    // refused for the pending journal; any other effect keeps the refusal.
    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_FaultedOwnerReplacedWithItsJournaledBaselineWhenReportedAgain(int product)
    {
        using var storage = new McuFrameJournalFixture(product);
        var session = new Session { Brightness = 129, McuLighting = McuFrameBaseline.ToArray() };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        var clock = new VaporChamberClock();
        session.EchoLightingAck = true;
        var ownership = new ExperimentalLightingOwnershipState { Journal = storage.Journal };
        await using var port = new ExperimentalBladePort(session, new(), Model(product), policy, () => true,
            () => PowerSource.PluggedIn, sharedLightingOwnership: ownership, lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        await clock.WaitForTimerCountAsync(1);
        ExperimentalLightingBaseline original = Assert.IsType<ExperimentalLightingBaseline>(storage.Journal.Load().Baseline);

        session.McuLighting = McuFrameBaseline.ToArray();
        int beforeLoss = session.Commands.Count;
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await WaitForOwnershipAsync(port, LightingRendererOwnershipState.Faulted);
        Assert.Equal(new[] { "0F/82 region5" }, session.Commands.Skip(beforeLoss).Select(DescribeMcu).ToArray());
        Assert.Equal(original, storage.Journal.Load().Baseline);
        Assert.Contains(policy.Snapshot().Observations, observation => observation.Outcome == "ExternallyRestoredToBaseline");

        // Brightness is a separate field and does not block reconciliation.
        session.Brightness = 140;
        await port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60), default);

        Assert.Equal(LightingRendererOwnershipState.Wheel, port.OwnershipState);
        // The reconciled journal keeps its original destination.
        Assert.Equal(original, storage.Journal.Load().Baseline);
        Assert.Equal(original, ownership.Baseline);
        Assert.Contains("0F/02 [0,5,8]", session.Commands.Skip(beforeLoss).Select(DescribeMcu));
        await port.RelinquishRendererAsync(default);
        Assert.Equal(McuFrameBaseline, session.McuLighting);
        Assert.Equal(129, session.Brightness);
        Assert.Null(storage.Journal.Load().Baseline);
        AssertMcuFrameContract(session);
    }

    // A journal left by an earlier run loads as Faulted. After an MCU reset
    // the keyboard reports its saved effect; if that is the baseline, a fresh
    // owner adopts the pending record before its first write.
    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_PendingJournalFromAnEarlierRunAdmitsAFreshOwnerOnlyForItsBaseline(int product)
    {
        foreach (bool baselineReported in new[] { false, true })
        {
            using var storage = new McuFrameJournalFixture(product);
            var saved = new ExperimentalLightingBaseline(LightingSettingsFactory.BreathingSingle(new(11, 22, 33), 51), 129);
            storage.Journal.SavePending(saved);
            var ownership = new ExperimentalLightingOwnershipState
            {
                Journal = storage.Journal,
                Baseline = saved,
                Ownership = (int)LightingRendererOwnershipState.Faulted,
            };
            byte[] reported = baselineReported ? McuFrameBaseline.ToArray() : [0, 5, 4, 1, 40, 0, 0, 0, 0, 0, 0, 0];
            var session = new Session { Brightness = 129, McuLighting = reported.ToArray() };
            bool journalledBeforeFirstWrite = false;
            session.BeforeSubmission = command =>
            {
                if (command.CommandId < 0x80 && !session.Commands.Any(prior => prior.CommandId < 0x80))
                {
                    journalledBeforeFirstWrite = storage.Journal.Load().Baseline == ownership.Baseline;
                }
            };
            await using var port = CreateMcuFrames(product, session, ownership: ownership);

            if (!baselineReported)
            {
                CapabilityUnavailableException refused = await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
                    port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));
                Assert.Equal("Lighting recovery requires a confirmed built-in destination before starting another renderer.",
                    refused.Message);
                Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
                Assert.Equal(saved, storage.Journal.Load().Baseline);
                Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
                continue;
            }

            await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);

            Assert.True(journalledBeforeFirstWrite);
            Assert.Equal(saved, storage.Journal.Load().Baseline);
            Assert.Equal(LightingRendererOwnershipState.Fire, port.OwnershipState);
            await port.RelinquishRendererAsync(default);
            Assert.Null(storage.Journal.Load().Baseline);
            AssertMcuFrameContract(session);
        }
    }

    // A read refused before it reached the device observed nothing: the frame
    // is skipped as KnownNotApplied, with no fault and no journal change.
    [Theory]
    [MemberData(nameof(McuUnobservedSelectionReads))]
    public async Task McuFrames_UnobservedSelectionReadSkipsAUniformFrameWithoutFault(int product, string failure)
    {
        using var storage = new McuFrameJournalFixture(product);
        var session = new Session { McuLighting = McuFrameBaseline.ToArray() };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = CreateMcuFrames(product, session, policy, new() { Journal = storage.Journal },
            clock: TimeProvider.System);
        await port.SetLightingAsync(LightingSettingsFactory.AudioMeter(1, 51), default);
        ExperimentalLightingBaseline? journaled = storage.Journal.Load().Baseline;
        foreach (bool firstFrame in new[] { true, false })
        {
            InjectSelectionReadFailure(session, failure);
            int before = session.Commands.Count;

            BladeWriteException skipped = await Assert.ThrowsAsync<BladeWriteException>(() =>
                port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default));

            Assert.Equal(WriteSubmissionState.KnownNotApplied, skipped.SubmissionState);
            Assert.Equal("The keyboard lighting state could not be read, so this frame was skipped.", skipped.Message);
            Assert.Equal(new[] { "0F/82 region5" }, session.Commands.Skip(before).Select(DescribeMcu).ToArray());
            Assert.Equal(LightingRendererOwnershipState.AudioMeter, port.OwnershipState);
            Assert.True(policy.WritesAllowed);
            Assert.Equal(journaled, storage.Journal.Load().Baseline);

            await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
            Assert.Equal(firstFrame ? 9 : 7,
                session.Commands.Skip(before + 1).Count());
        }
        AssertMcuFrameContract(session);
    }

    [Theory]
    [MemberData(nameof(McuUnobservedSelectionReads))]
    public async Task McuFrames_UnobservedSelectionReadSkipsAnAnimationBatchWithoutFault(int product, string failure)
    {
        using var storage = new McuFrameJournalFixture(product);
        var session = new Session { McuLighting = McuFrameBaseline.ToArray() };
        session.EchoLightingAck = true;
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        var clock = new VaporChamberClock();
        await using var port = new ExperimentalBladePort(session, new(), Model(product), policy, () => true,
            () => PowerSource.PluggedIn, sharedLightingOwnership: new() { Journal = storage.Journal },
            lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        await clock.WaitForTimerCountAsync(1);
        InjectSelectionReadFailure(session, failure);
        int before = session.Commands.Count;

        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);

        Assert.Equal(new[] { "0F/82 region5" }, session.Commands.Skip(before).Select(DescribeMcu).ToArray());
        Assert.Equal(LightingRendererOwnershipState.Fire, port.OwnershipState);
        Assert.NotNull(storage.Journal.Load().Baseline);
        int beforeNext = session.Commands.Count;
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(3);
        Assert.Equal(new[] { "0F/82 region5", "03/0B row0", "03/0B row1", "03/0B row2", "03/0B row3", "03/0B row4", "03/0B row5" },
            session.Commands.Skip(beforeNext).Select(DescribeMcu).ToArray());
        Assert.Equal(LightingRendererOwnershipState.Fire, port.OwnershipState);
        AssertMcuFrameContract(session);
    }

    [Theory]
    [MemberData(nameof(McuUnobservedSelectionReads))]
    public async Task McuFrames_UnobservedSelectionReadLeavesTheReplacedRendererRunning(int product, string failure)
    {
        using var storage = new McuFrameJournalFixture(product);
        var session = new Session { McuLighting = McuFrameBaseline.ToArray() };
        session.EchoLightingAck = true;
        var clock = new VaporChamberClock();
        await using var port = new ExperimentalBladePort(session, new(), Model(product), new(Model(product)), () => true,
            () => PowerSource.PluggedIn, sharedLightingOwnership: new() { Journal = storage.Journal },
            lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        await clock.WaitForTimerCountAsync(1);
        InjectSelectionReadFailure(session, failure);
        int before = session.Commands.Count;

        BladeWriteException skipped = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60), default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, skipped.SubmissionState);
        Assert.Equal(new[] { "0F/82 region5" }, session.Commands.Skip(before).Select(DescribeMcu).ToArray());
        Assert.Equal(LightingRendererOwnershipState.Fire, port.OwnershipState);
        Assert.NotNull(storage.Journal.Load().Baseline);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        Assert.Equal(6, session.Commands.Skip(before + 1).Count(command => command.ClassId == 3 && command.CommandId == 0x0B));
        AssertMcuFrameContract(session);
    }

    // Write admission precedes every ownership read.
    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_RefusedWriteAdmissionSpendsNoSelectionRead(int product)
    {
        var session = new Session { McuLighting = McuFrameBaseline.ToArray() };
        session.EchoLightingAck = true;
        bool hostPresent = true;
        await using var port = new ExperimentalBladePort(session, new(), Model(product), new(Model(product)),
            () => hostPresent, () => PowerSource.PluggedIn, lightingTimeProvider: TimeProvider.System);
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 60), default);
        foreach (bool firstFrame in new[] { true, false })
        {
            if (!firstFrame)
            {
                hostPresent = true;
                await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
            }
            hostPresent = false;
            int before = session.Commands.Count;

            BladeWriteException refused = await Assert.ThrowsAsync<BladeWriteException>(() =>
                port.PushUniformLightingFrameAsync(new(new RgbColor(1, 2, 3)), default));

            Assert.Equal(WriteSubmissionState.KnownNotApplied, refused.SubmissionState);
            Assert.Equal(before, session.Commands.Count);
        }
    }

    // A sampled owner selects mode 8 only with its first frame. If another
    // controller changes the effect first, OpenBlade neither draws nor restores.
    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_SampledOwnerStopsWithoutRestoreWhenTheEffectChangesBeforeItsFirstFrame(int product)
    {
        byte[] external = [0, 5, 4, 2, 40, 0, 0, 0, 0, 0, 0, 0];
        foreach (bool relinquish in new[] { false, true })
        {
            using var storage = new McuFrameJournalFixture(product);
            var session = new Session { Brightness = 129, McuLighting = McuFrameBaseline.ToArray() };
            await using var port = CreateMcuFrames(product, session, ownership: new() { Journal = storage.Journal },
                clock: TimeProvider.System);
            await port.SetLightingAsync(LightingSettingsFactory.AudioMeter(1, 60), default);
            session.McuLighting = external.ToArray();
            int before = session.Commands.Count;

            if (relinquish)
            {
                BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
                    port.RelinquishRendererAsync(default));
                Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
                Assert.Equal("Another app changed the keyboard lighting, so OpenBlade left it unchanged. Choose a lighting effect to continue.",
                    failure.Message);
            }
            else
            {
                BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
                    port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default));
                Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
                Assert.Equal("Another app changed the keyboard lighting, so host lighting stopped. Choose a lighting effect to continue.",
                    failure.Message);
            }

            Assert.DoesNotContain(session.Commands.Skip(before), command => command.CommandId < 0x80);
            Assert.Equal(external, session.McuLighting);
            Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
            Assert.NotNull(storage.Journal.Load().Baseline);
            AssertMcuFrameContract(session);
        }
    }

    // Before its first frame a sampled owner never selected mode 8. If the
    // baseline effect is still shown, only brightness may need restoring.
    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_SampledOwnerWithoutFramesRestoresOnlyChangedBrightness(int product)
    {
        using var storage = new McuFrameJournalFixture(product);
        var session = new Session { Brightness = 129, McuLighting = McuFrameBaseline.ToArray() };
        await using var port = CreateMcuFrames(product, session, ownership: new() { Journal = storage.Journal });
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 60), default);
        Assert.NotEqual(129, session.Brightness);
        int before = session.Commands.Count;

        await port.RelinquishRendererAsync(default);

        Assert.Equal(new[] { "03/83", "0F/82 region5", "03/03", "03/83" },
            session.Commands.Skip(before).Select(DescribeMcu).ToArray());
        Assert.Equal(129, session.Brightness);
        Assert.Equal(McuFrameBaseline, session.McuLighting);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    private static void InjectSelectionReadFailure(Session session, string failure)
    {
        if (failure == nameof(ObjectDisposedException))
        {
            session.McuSelectionReadException = new ObjectDisposedException("session");
        }
        else
        {
            session.McuSelectionReadFailure = Enum.Parse<HidSessionFailureKind>(failure);
        }
    }
}
