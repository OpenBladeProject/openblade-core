using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    [Fact]
    public void Rz090529_AllAdvertisedEffectsHaveTypedBackends()
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(Model(0x02C7), true);
        Assert.True(capabilities.SupportsUniformLightingFrames);
        Assert.Equal(KeyboardLightingLayoutIds.Rz09_0529, capabilities.KeyboardLightingLayoutId);
        Assert.True(capabilities.LightingEffects.SetEquals(Enum.GetValues<LightingEffect>()
            .Where(effect => effect != LightingEffect.Sequence)));
        Assert.True(capabilities.ObservedLightingEffects.SetEquals(Enum.GetValues<LightingEffect>()
            .Where(effect => effect is not (LightingEffect.Sequence or LightingEffect.Off))));
        DeviceCapabilities paused = ExperimentalBladePort.CreateCapabilities(Model(0x02C7), false);
        Assert.Empty(paused.LightingEffects);
        Assert.True(paused.ObservedLightingEffects.SetEquals(capabilities.ObservedLightingEffects));
    }

    [Fact]
    public void Rz090529_KeyboardRowsNeverAddressAuxiliaryRegion()
    {
        var colors = Enumerable.Range(0, 114).Select(index => new RgbColor((byte)index, 17, 33)).ToArray();
        byte[] row = Rz090529KeyboardLightingFrame.EncodeRow(5, colors);
        Assert.Equal(61, row.Length);
        Assert.Equal(new byte[] { 255, 5, 0, 18, 95, 17, 33 }, row.Take(7));
        Assert.Equal(new byte[] { 113, 17, 33 }, row.TakeLast(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rz090529KeyboardLightingFrame.EncodeRow(6, colors));
        Assert.Throws<ArgumentException>(() => Rz090529KeyboardLightingFrame.EncodeRow(0, colors[..96]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529_UnsampledRendererUsesActualBreathingColorsForBuiltinTransition(bool audio)
    {
        var session = new Session
        {
            McuLighting = [0, 5, 2, 0, 0, 1, 11, 22, 33, 0, 0, 0],
        };
        await using var port = CreateRz090529Frames(session);
        await port.SetLightingAsync(audio ? LightingSettingsFactory.AudioMeter(1, 50)
            : LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 3 && command.CommandId == 0x0B);
        await port.SetLightingAsync(LightingSettingsFactory.BreathingSingle(new(44, 55, 66), 50), default);
        Assert.Equal(new byte[] { 0, 5, 2, 0, 0, 1, 44, 55, 66, 0, 0, 0 }, session.McuLighting);
        Assert.Contains(session.Commands, command => command.ClassId == 15 && command.CommandId == 2
            && command.Arguments.Span[2] == 2 && command.Arguments.Span[5] == 0);
    }

    [Fact]
    public void Rz090529_RowAndKeyboardActivationHaveIndependentKnownAnswers()
    {
        byte[] row = Rz090529KeyboardLightingFrame.EncodeRow(0,
            Enumerable.Repeat(new RgbColor(1, 2, 3), 114).ToArray());
        byte[] frame = RazerReport.Create(31, 3, 0x0B, row).Encode();
        Assert.Equal(Convert.FromHexString("001F0000003D030BFF000012010203"), frame.Take(15));
        Assert.Equal(0xD8, frame[88]);
        byte[] activation = RazerReport.Create(31, 15, 2, new byte[] { 0, 5, 8 }).Encode();
        Assert.Equal(Convert.FromHexString("001F000000030F02000508"), activation.Take(11));
        Assert.Equal(3, activation[88]);
    }

    public static TheoryData<byte[]> Rz090529RestorableBaselines => new()
    {
        new byte[] { 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 5, 1, 0, 0, 1, 11, 22, 33, 0, 0, 0 },
        new byte[] { 0, 5, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 5, 2, 0, 0, 1, 11, 22, 33, 0, 0, 0 },
        new byte[] { 0, 5, 2, 0, 0, 2, 11, 22, 33, 44, 55, 66 },
        new byte[] { 0, 5, 4, 1, 40, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 5, 4, 2, 40, 0, 0, 0, 0, 0, 0, 0 },
    };

    [Theory]
    [MemberData(nameof(Rz090529RestorableBaselines))]
    public async Task Rz090529_RestoresAllBuiltinParametersAndExactRawBrightness(byte[] baseline)
    {
        var session = new Session { Brightness = 129, McuLighting = baseline.ToArray() };
        await using var port = CreateRz090529Frames(session);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), default);
        await port.RelinquishRendererAsync(default);
        Assert.Equal(baseline, session.McuLighting);
        Assert.Equal(129, session.Brightness);
        Assert.False(session.McuCustomOverlay);
        if (baseline[2] == 0)
        {
            Assert.DoesNotContain(session.Commands, command => command.ClassId == 15
                && command.CommandId == 2 && command.Arguments.Span[2] == 2);
        }
    }

    [Fact]
    public async Task Rz090529_FailedRestoreLeavesFaultedOwnerAndDoesNotReplay()
    {
        var session = new Session { McuLighting = [0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = CreateRz090529Frames(session, policy);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), default);
        session.IgnoreWrites = true;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.RelinquishRendererAsync(default));
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(8, session.McuLighting[2]);
    }

    [Theory]
    [InlineData(LightingEffect.Fire)]
    [InlineData(LightingEffect.Wheel)]
    [InlineData(LightingEffect.Starlight)]
    [InlineData(LightingEffect.Reactive)]
    [InlineData(LightingEffect.Ripple)]
    [InlineData(LightingEffect.Tidal)]
    [InlineData(LightingEffect.Spectrum)]
    public async Task Rz090529_HostEffectsActivateKeyboardOnlyAndRestoreExactBaseline(LightingEffect effect)
    {
        var session = new Session
        {
            Brightness = 129,
            McuLighting = [0, 5, 1, 0, 0, 1, 11, 22, 33, 0, 0, 0],
        };
        await using var port = CreateRz090529Frames(session);
        LightingSettings requested = effect switch
        {
            LightingEffect.Fire => LightingSettingsFactory.Fire(60),
            LightingEffect.Wheel => LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60),
            LightingEffect.Starlight => LightingSettingsFactory.StarlightRandom(60, StarlightLightingDuration.Medium),
            LightingEffect.Reactive => LightingSettingsFactory.Reactive(new(11, 22, 33), 60, ReactiveLightingDuration.Medium),
            LightingEffect.Ripple => LightingSettingsFactory.Ripple(new(11, 22, 33), 60),
            LightingEffect.Tidal => LightingSettingsFactory.Tidal(new(11, 22, 33), new(44, 55, 66), false, LightingDirection.Rightward, 60),
            _ => LightingSettingsFactory.SpectrumCycling(60),
        };
        await port.SetLightingAsync(requested, default);
        Assert.True(session.McuCustomOverlay);
        Assert.Equal(8, session.McuLighting[2]);
        Assert.Equal(6, session.Commands.Count(command => command.ClassId == 3 && command.CommandId == 0x0B));
        Assert.All(session.Commands.Where(command => command.ClassId == 3 && command.CommandId == 0x0B),
            command => Assert.InRange(command.Arguments.Span[1], (byte)0, (byte)5));
        Assert.Contains(session.Commands, command => command.ClassId == 15 && command.CommandId == 2
            && command.Arguments.Span.SequenceEqual(new byte[] { 0, 5, 8 }));
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 3 && command.CommandId == 0x0A);
        Assert.Null(await port.ReadStateAsync(default)); // Intent is not pixel readback.
        await port.RelinquishRendererAsync(default);
        Assert.False(session.McuCustomOverlay);
        Assert.Equal(129, session.Brightness);
        Assert.Equal(new byte[] { 0, 5, 1, 0, 0, 1, 11, 22, 33, 0, 0, 0 }, session.McuLighting);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 3 && command.CommandId == 3
            && command.Arguments.Span[0] != 0);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public async Task Rz090529_UnreadableBaselineBlocksFramesBeforeAnyWrite(int mode)
    {
        var session = new Session { McuLighting = [0, 5, (byte)mode, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        await using var port = CreateRz090529Frames(session);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(LightingSettingsFactory.Fire(50), default));
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Fact]
    public async Task Rz090529_PartialFrameStopsWithoutReplayOrAuxiliaryActivation()
    {
        var session = new Session { McuLighting = [0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        session.BeforeSubmission = command =>
        {
            if (command.ClassId == 3 && command.CommandId == 0x0B && command.Arguments.Span[1] == 1)
            {
                throw new IOException("Simulated second-row submission uncertainty.");
            }
        };
        await using var port = CreateRz090529Frames(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(50), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 15 && command.CommandId == 2);
        int count = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(LightingSettingsFactory.Fire(50), default));
        Assert.Equal(count, session.Commands.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529_AmbientAndAudioSamplesUseTheSameKeyboardOnlyTransport(bool audio)
    {
        var session = new Session
        {
            Brightness = 129,
            McuLighting = [0, 5, 1, 0, 0, 1, 11, 22, 33, 0, 0, 0],
        };
        await using var port = CreateRz090529Frames(session);
        await port.SetLightingAsync(audio ? LightingSettingsFactory.AudioMeter(1, 50)
            : LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
        await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
        RazerCommandSpec[] rows = session.Commands.Where(command => command.ClassId == 3 && command.CommandId == 0x0B).ToArray();
        Assert.Equal(6, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal(61, row.Arguments.Length);
            Assert.InRange(row.Arguments.Span[1], (byte)0, (byte)5);
            Assert.Equal(new byte[] { 9, 8, 7 }, row.Arguments.Span.Slice(4, 3).ToArray());
        });
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 3 && command.CommandId == 0x0A);
        await port.RelinquishRendererAsync(default);
        Assert.Equal(129, session.Brightness);
        Assert.Equal(1, session.McuLighting[2]);
    }

    private static ExperimentalBladePort CreateRz090529Frames(Session session, ExperimentalFeaturesPolicy? policy = null)
    {
        session.EchoLightingAck = true;
        return new(session, new(), Model(0x02C7), policy ?? new(Model(0x02C7)), () => true,
            () => PowerSource.PluggedIn, lightingTimeProvider: new FrozenLightingClock());
    }

    private sealed class FrozenLightingClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FrozenTimer();
        private sealed class FrozenTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
