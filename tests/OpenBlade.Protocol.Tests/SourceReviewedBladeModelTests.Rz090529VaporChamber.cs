using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    private static readonly byte[] Rz090529StaticKeyboard = [0, 5, 1, 0, 0, 1, 11, 22, 33, 0, 0, 0];

    public static TheoryData<byte[]> Rz090529RestorableVaporChamberBaselines => new()
    {
        new byte[] { 0, 38, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 38, 1, 0, 0, 1, 1, 2, 3, 0, 0, 0 },
        new byte[] { 0, 38, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 38, 2, 0, 0, 1, 1, 2, 3, 0, 0, 0 },
        new byte[] { 0, 38, 2, 0, 0, 2, 1, 2, 3, 4, 5, 6 },
        new byte[] { 0, 38, 4, 1, 40, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 38, 4, 2, 40, 0, 0, 0, 0, 0, 0, 0 },
    };

    [Theory]
    [MemberData(nameof(Rz090529RestorableVaporChamberBaselines))]
    public async Task Rz090529VaporChamber_RestorableBaselineJoinsFramesAndIsRestoredExactly(byte[] vaporChamber)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            Brightness = 129,
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = vaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });

        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);

        Assert.Equal(8, session.McuVaporChamber[2]);
        Assert.Single(session.Commands, command => IsRz090529Row(command, 6));
        Assert.NotNull(storage.Journal.Load().Baseline?.VaporChamber);
        Assert.Contains("\"schemaVersion\":2", File.ReadAllText(storage.JournalPath), StringComparison.Ordinal);

        await port.RelinquishRendererAsync(default);

        Assert.Equal(vaporChamber, session.McuVaporChamber);
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        Assert.Equal(129, session.Brightness);
        // From custom mode 8 the setter bypasses same-mode shortcuts: one write, no bridge.
        RazerCommandSpec restore = Assert.Single(session.Commands, command =>
            IsVaporChamberSet(command) && command.Arguments.Span[2] != 8);
        Assert.Equal(vaporChamber[2], restore.Arguments.Span[2]);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_FrameAddsRowSixAndSelectsRegionAfterKeyboard()
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = [0, 38, 1, 0, 0, 1, 1, 2, 3, 0, 0, 0],
        };
        await using var port = CreateRz090529Frames(session);
        LightingSettings fire = LightingSettingsFactory.Fire(60);

        await port.SetLightingAsync(fire, default);

        int first = session.Commands.FindIndex(command => IsRz090529Row(command, 0));
        string[] sequence = session.Commands.Skip(first).Select(Describe).ToArray();
        Assert.Equal(new[]
        {
            "03/0B row0", "03/0B row1", "03/0B row2", "03/0B row3", "03/0B row4", "03/0B row5",
            "03/0B row6", "0F/02 [0,5,8]", "0F/82 region5", "0F/02 [0,38,8]", "0F/82 region38",
        }, sequence);
        byte[] row = session.Commands.Single(command => IsRz090529Row(command, 6)).Arguments.ToArray();
        Assert.Equal(61, row.Length);
        Assert.Equal(new byte[] { 0xFF, 6, 0, 18, 0, 0, 0 }, row.Take(7));
        IReadOnlyList<RgbColor> expected = new ExperimentalGridLightingEffect(fire, rz090529: true)
            .RenderVaporChamber(TimeSpan.Zero);
        for (int column = 0; column < 18; column++)
        {
            RgbColor color = expected[column];
            Assert.Equal(new[] { color.Red, color.Green, color.Blue }, row.Skip(7 + column * 3).Take(3));
        }
    }

    [Fact]
    public void Rz090529VaporChamber_RowEncodingLeavesTransportColumnZeroBlack()
    {
        RgbColor[] colors = Enumerable.Range(0, 18).Select(index => new RgbColor((byte)(index + 1), 50, 60)).ToArray();
        byte[] row = Rz090529KeyboardLightingFrame.EncodeVaporChamberRow(colors);
        Assert.Equal(Rz090529KeyboardLightingFrame.PayloadLength, row.Length);
        Assert.Equal(new byte[] { 0xFF, 6, 0, 18, 0, 0, 0, 1, 50, 60 }, row.Take(10));
        Assert.Equal(new byte[] { 18, 50, 60 }, row.TakeLast(3));
        Assert.Throws<ArgumentException>(() => Rz090529KeyboardLightingFrame.EncodeVaporChamberRow(colors[..17]));
        byte[] activation = RazerReport.Create(31, 15, 2, new byte[] { 0, 38, 8 }).Encode();
        Assert.Equal(Convert.FromHexString("001F000000030F02002608"), activation.Take(11));
    }

    [Theory]
    [InlineData(new byte[] { 0, 38, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 38, 7, 1, 0, 1, 1, 2, 3, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 38, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 38, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 38, 1, 0, 0, 2, 1, 2, 3, 4, 5, 6 })]
    [InlineData(new byte[] { 0, 38, 4, 1, 39, 0, 0, 0, 0, 0, 0, 0 })]
    public async Task Rz090529VaporChamber_UnrestorableBaselineKeepsFramesKeyboardOnly(byte[] vaporChamber)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = vaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });

        await port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60), default);
        Assert.Null(storage.Journal.Load().Baseline!.VaporChamber);
        Assert.Contains("\"schemaVersion\":1", File.ReadAllText(storage.JournalPath), StringComparison.Ordinal);
        Assert.DoesNotContain("vaporChamber", File.ReadAllText(storage.JournalPath), StringComparison.OrdinalIgnoreCase);
        await port.RelinquishRendererAsync(default);

        Assert.DoesNotContain(session.Commands, command => IsRz090529Row(command, 6));
        Assert.DoesNotContain(session.Commands, IsVaporChamberSet);
        Assert.Equal(vaporChamber, session.McuVaporChamber);
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
    }

    [Theory]
    [InlineData(new byte[] { 0, 5, 1, 0, 0, 1, 1, 2, 3, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 38, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 38, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public async Task Rz090529VaporChamber_MalformedReplyIsReadFailureBeforeAnyWrite(byte[] reply)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = reply,
        };
        await using var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_ExternallyChangedRegionIsLeftUnchanged()
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = [0, 38, 1, 0, 0, 1, 1, 2, 3, 0, 0, 0],
        };
        await using var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        byte[] external = [0, 38, 4, 1, 40, 0, 0, 0, 0, 0, 0, 0];
        session.McuVaporChamber = external.ToArray();

        await port.RelinquishRendererAsync(default);

        Assert.Equal(external, session.McuVaporChamber);
        Assert.Single(session.Commands, IsVaporChamberSet); // Only the [0,38,8] selection.
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_BuiltInTransitionRestoresOwnedRegion()
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = [0, 38, 2, 0, 0, 2, 1, 2, 3, 4, 5, 6],
        };
        await using var port = CreateRz090529Frames(session);
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
        await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
        byte[] row = session.Commands.Single(command => IsRz090529Row(command, 6)).Arguments.ToArray();
        Assert.Equal(new byte[] { 0, 0, 0, 9, 8, 7 }, row.Skip(4).Take(6));
        Assert.Equal(new byte[] { 9, 8, 7 }, row.TakeLast(3));
        Assert.Equal(8, session.McuVaporChamber[2]);

        await port.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default);

        Assert.Equal(new byte[] { 0, 38, 2, 0, 0, 2, 1, 2, 3, 4, 5, 6 }, session.McuVaporChamber);
        Assert.Equal(new byte[] { 0, 5, 4, 2, 40, 0, 0, 0, 0, 0, 0, 0 }, session.McuLighting);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529VaporChamber_KeyboardOnlyEffectRestoresRegionThenFramesCanRetakeIt(bool sequence)
    {
        byte[] baseline = [0, 38, 1, 0, 0, 1, 1, 2, 3, 0, 0, 0];
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = baseline.ToArray(),
        };
        await using var port = CreateRz090529Frames(session);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        Assert.Equal(8, session.McuVaporChamber[2]);
        int beforeKeyboardOnly = session.Commands.Count;

        await port.SetLightingAsync(sequence ? Rz090529Sequence()
            : LightingSettingsFactory.Reactive(new(11, 22, 33), 60, ReactiveLightingDuration.Medium), default);

        Assert.Equal(baseline, session.McuVaporChamber);
        RazerCommandSpec[] keyboardOnly = session.Commands.Skip(beforeKeyboardOnly).ToArray();
        int restore = Array.FindIndex(keyboardOnly, command => IsVaporChamberSet(command) && command.Arguments.Span[2] == 1);
        int firstRow = Array.FindIndex(keyboardOnly, command => IsRz090529Row(command, 0));
        Assert.InRange(restore, 0, firstRow - 1);
        Assert.DoesNotContain(keyboardOnly, command => IsRz090529Row(command, 6));
        Assert.DoesNotContain(keyboardOnly, command => IsVaporChamberSet(command) && command.Arguments.Span[2] == 8);

        int beforeRetake = session.Commands.Count;
        await port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60), default);
        Assert.Equal(8, session.McuVaporChamber[2]);
        Assert.Contains(session.Commands.Skip(beforeRetake), command => IsRz090529Row(command, 6));

        await port.RelinquishRendererAsync(default);
        Assert.Equal(baseline, session.McuVaporChamber);
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529VaporChamber_AmbiguousSelectionFaultsWithoutReplay(bool failConfirmation)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = [0, 38, 1, 0, 0, 1, 1, 2, 3, 0, 0, 0],
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        bool selected = false;
        session.BeforeSubmission = command =>
        {
            if (IsVaporChamberSet(command) && command.Arguments.Span[2] == 8)
            {
                selected = true;
                if (!failConfirmation) { throw new IOException("Simulated LED38 selection uncertainty."); }
            }
            else if (failConfirmation && selected && command.ClassId == 15 && command.CommandId == 0x82
                && command.Arguments.Span[1] == 38)
            {
                throw new IOException("Simulated LED38 confirmation failure.");
            }
        };
        await using var port = CreateRz090529Frames(session, policy, new() { Journal = storage.Journal });

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.NotNull(storage.Journal.Load().Baseline?.VaporChamber);
        int count = session.Commands.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));
        Assert.Equal(LightingRendererOwnershipState.Faulted, await port.RelinquishRendererAsync(default));
        Assert.Equal(count, session.Commands.Count);
        Assert.Equal(failConfirmation ? 1 : 0, session.Commands.Count(command =>
            IsVaporChamberSet(command) && command.Arguments.Span[2] == 8));
    }

    [Fact]
    public async Task Rz090529VaporChamber_KeyTriggeredDuplicateSuppressionCoversBothRows()
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = [0, 38, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        };
        session.EchoLightingAck = true;
        var clock = new VaporChamberClock();
        await using var port = new ExperimentalBladePort(session, new(), Model(0x02C7), new(Model(0x02C7)),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Ripple(new(200, 100, 50), 60), default);
        await clock.WaitForTimerCountAsync(1);
        Assert.Equal(7, session.Commands.Count(IsFrameRow));

        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        Assert.Equal(7, session.Commands.Count(IsFrameRow));

        clock.Advance(TimeSpan.FromMilliseconds(4950));
        await clock.WaitForTimerCountAsync(3);
        Assert.Equal(14, session.Commands.Count(IsFrameRow));
        Assert.Equal(2, session.Commands.Count(command => IsRz090529Row(command, 6)));

        await port.RelinquishRendererAsync(default);
        Assert.Equal(new byte[] { 0, 38, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, session.McuVaporChamber);

        static bool IsFrameRow(RazerCommandSpec command) => command.ClassId == 3 && command.CommandId == 0x0B;
    }

    [Fact]
    public void Rz090529VaporChamber_RendererUsesTopRowAndExcludesKeyboardOnlyEffects()
    {
        foreach (LightingSettings spatial in new[]
        {
            LightingSettingsFactory.Fire(60),
            LightingSettingsFactory.Wheel(LightingRotationDirection.Counterclockwise, 60),
            LightingSettingsFactory.Tidal(new(11, 22, 33), new(44, 55, 66), false, LightingDirection.Leftward, 60),
            LightingSettingsFactory.SpectrumCycling(60),
        })
        {
            var effect = new ExperimentalGridLightingEffect(spatial, rz090529: true);
            Assert.True(effect.IncludesVaporChamber);
            TimeSpan at = TimeSpan.FromMilliseconds(1234);
            IReadOnlyList<RgbColor> keyboard = effect.RenderFrame(at);
            IReadOnlyList<RgbColor> vaporChamber = effect.RenderVaporChamber(at);
            Assert.Equal(18, vaporChamber.Count);
            Assert.Equal(keyboard.Take(18), vaporChamber);
        }

        var stars = new ExperimentalGridLightingEffect(
            LightingSettingsFactory.StarlightRandom(60, StarlightLightingDuration.Short), rz090529: true);
        Assert.Contains(Enumerable.Range(0, 40), second =>
            stars.RenderVaporChamber(TimeSpan.FromSeconds(second)).Any(color => color != default));

        var ripple = new ExperimentalGridLightingEffect(LightingSettingsFactory.Ripple(new(200, 100, 50), 60), rz090529: true);
        Assert.All(ripple.RenderVaporChamber(TimeSpan.Zero), color => Assert.Equal(default, color));
        ripple.Pulse(1, TimeSpan.Zero); // Logical position (0, 1), directly below VC column 0.
        Assert.NotEqual(default, ripple.RenderVaporChamber(TimeSpan.FromSeconds(1.5 / 18))[0]);

        Assert.False(new ExperimentalGridLightingEffect(
            LightingSettingsFactory.Reactive(new(1, 2, 3), 60, ReactiveLightingDuration.Medium), rz090529: true).IncludesVaporChamber);
        var sequence = new ExperimentalGridLightingEffect(Rz090529Sequence(), rz090529: true);
        Assert.False(sequence.IncludesVaporChamber);
        Assert.Throws<InvalidOperationException>(() => sequence.RenderVaporChamber(TimeSpan.Zero));
        Assert.False(new ExperimentalGridLightingEffect(LightingSettingsFactory.Fire(60)).IncludesVaporChamber);
    }

    [Theory]
    [InlineData(0x0245)]
    [InlineData(0x028A)]
    [InlineData(0x028B)]
    [InlineData(0x028C)]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    public async Task Rz090529VaporChamber_OtherMcuModelsNeverAddressRegion38(int product)
    {
        var session = new Session { EchoLightingAck = true, McuLighting = [0, 5, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        await using var port = Create(product, session);
        await port.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Leftward, 50), default);
        await port.RelinquishRendererAsync(default);

        Assert.NotEmpty(session.Commands);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 15 && command.Arguments.Span[1] == 38);
    }

    private static bool IsRz090529Row(RazerCommandSpec command, int row) =>
        command.ClassId == 3 && command.CommandId == 0x0B && command.Arguments.Span[1] == row;

    private static bool IsVaporChamberSet(RazerCommandSpec command) =>
        command.ClassId == 15 && command.CommandId == 2 && command.Arguments.Span[1] == 38;

    private static string Describe(RazerCommandSpec command) => (command.ClassId, command.CommandId) switch
    {
        (3, 0x0B) => $"03/0B row{command.Arguments.Span[1]}",
        (15, 2) => $"0F/02 [{string.Join(',', command.Arguments.ToArray())}]",
        (15, 0x82) => $"0F/82 region{command.Arguments.Span[1]}",
        _ => $"{command.ClassId:X2}/{command.CommandId:X2}",
    };

    private static LightingSettings Rz090529Sequence()
    {
        var slots = new RgbColor[Rz090529KeyboardLightingLayout.SlotCount];
        slots[1] = new RgbColor(12, 34, 56);
        string timeline = KeyboardLightingSequenceCodec.Encode(
            [new KeyboardLightingSequenceFrame(100, slots)], KeyboardLightingLayoutIds.Rz09_0529);
        return LightingSettingsFactory.Sequence(KeyboardLightingLayoutIds.Rz09_0529, timeline, loop: true, brightness: 80);
    }

    private sealed class VaporChamberClock : TimeProvider
    {
        private long ticks;
        private readonly List<Timer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state, GetTimestamp() + dueTime.Ticks);
            lock (timers) { timers.Add(timer); }
            return timer;
        }

        internal void Advance(TimeSpan amount)
        {
            Interlocked.Add(ref ticks, amount.Ticks);
            Timer[] due;
            lock (timers) { due = timers.Where(timer => timer.Due <= GetTimestamp()).ToArray(); }
            foreach (Timer timer in due) { timer.Fire(); }
        }

        internal async Task WaitForTimerCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (timers) { if (timers.Count >= count) { return; } }
                await Task.Delay(1, timeout.Token);
            }
        }

        private sealed class Timer(TimerCallback callback, object? state, long due) : ITimer
        {
            private int completed;
            internal long Due { get; } = due;
            internal void Fire()
            {
                if (Interlocked.Exchange(ref completed, 1) == 0) { callback(state); }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref completed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
