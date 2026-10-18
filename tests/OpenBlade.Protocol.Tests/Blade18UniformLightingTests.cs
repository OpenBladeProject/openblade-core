using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(LightingEffect.AmbientAwareness)]
    [InlineData(LightingEffect.AudioMeter)]
    public async Task Blade18UniformHostFrames_UseSetOnlyBatchesAndActivateOnlyOnEntry(
        LightingEffect effect)
    {
        var session = new Session();
        var policy = Enabled();
        await using var port = Create(session, policy);
        LightingSettings settings = effect switch
        {
            LightingEffect.AmbientAwareness => LightingSettingsFactory.AmbientAwareness(
                AmbientAwarenessRegion.Full,
                50),
            LightingEffect.AudioMeter => LightingSettingsFactory.AudioMeter(1, 50),
            _ => throw new InvalidOperationException(),
        };

        await port.SetLightingAsync(settings, CancellationToken.None);
        Assert.Equal(effect == LightingEffect.AmbientAwareness
                ? LightingRendererOwnershipState.AmbientAwareness
                : LightingRendererOwnershipState.AudioMeter,
            port.OwnershipState);
        Assert.DoesNotContain(session.Commands, IsBlade18FrameCommand);

        var first = new RgbColor(9, 8, 7);
        var second = new RgbColor(6, 5, 4);
        await port.PushUniformLightingFrameAsync(
            new UniformLightingFrame(first),
            CancellationToken.None);
        await port.PushUniformLightingFrameAsync(
            new UniformLightingFrame(second),
            CancellationToken.None);

        Assert.Equal(2, session.AnimationBatches);
        RazerCommandSpec[] rows = session.Commands
            .Where(command => command.ClassId == 0x03 && command.CommandId == 0x0B)
            .ToArray();
        Assert.Equal(12, rows.Length);
        AssertBlade18UniformRows(rows[..6], first);
        AssertBlade18UniformRows(rows[6..], second);
        Assert.Single(session.Commands, command =>
            command.ClassId == 0x03
            && command.CommandId == 0x0A
            && command.Arguments.Span.SequenceEqual(new byte[] { 5, 0 }));
        Assert.Contains(policy.Snapshot().Observations, observation =>
            observation.Operation == "KeyboardUniformFrame"
            && observation.ConfirmedValue == "AppliedWithoutColorReadback;Frames=2");
        Assert.Equal(effect == LightingEffect.AmbientAwareness
                ? LightingRendererOwnershipState.AmbientAwareness
                : LightingRendererOwnershipState.AudioMeter,
            await port.RelinquishRendererAsync(CancellationToken.None));
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Equal(3, session.Effect);
    }

    [Fact]
    public async Task Blade18UniformHostFrame_EffectTransitionReactivatesNewOwnershipOnce()
    {
        var session = new Session();
        await using var port = Create(session, Enabled());
        LightingSettings ambient = LightingSettingsFactory.AmbientAwareness(
            AmbientAwarenessRegion.Full,
            50);

        await port.SetLightingAsync(ambient, CancellationToken.None);
        await port.PushUniformLightingFrameAsync(
            new UniformLightingFrame(new RgbColor(1, 2, 3)),
            CancellationToken.None);
        await port.SetLightingAsync(
            LightingSettingsFactory.AudioMeter(1, 50),
            CancellationToken.None);
        await port.PushUniformLightingFrameAsync(
            new UniformLightingFrame(new RgbColor(4, 5, 6)),
            CancellationToken.None);

        Assert.Equal(LightingRendererOwnershipState.AudioMeter, port.OwnershipState);
        Assert.Equal(2, session.Commands.Count(command =>
            command.ClassId == 0x03
            && command.CommandId == 0x0A
            && command.Arguments.Span.SequenceEqual(new byte[] { 5, 0 })));
        await port.RelinquishRendererAsync(CancellationToken.None);
        Assert.Equal(3, session.Effect);
    }

    [Fact]
    public async Task Blade18UniformHostFrame_PartialBatchPausesAndNeverReplays()
    {
        var session = new Session { FailAnimationBatch = 1 };
        var policy = Enabled();
        await using var port = Create(session, policy);
        await port.SetLightingAsync(
            LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50),
            CancellationToken.None);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.PushUniformLightingFrameAsync(
                new UniformLightingFrame(new RgbColor(1, 2, 3)),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.AnimationBatches);
        Assert.DoesNotContain(session.Commands, command =>
            command.ClassId == 0x03 && command.CommandId == 0x0A);
        policy.CompleteRecovery();
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.PushUniformLightingFrameAsync(
                new UniformLightingFrame(new RgbColor(4, 5, 6)),
                CancellationToken.None));
        Assert.Equal(1, session.AnimationBatches);
    }

    [Fact]
    public async Task Blade18Starlight_StreamsAndRetiresWithoutLaterFrames()
    {
        var clock = new AnimationClock();
        var session = new Session();
        await using var port = AnimatedPort(session, clock);
        await port.SetLightingAsync(
            LightingSettingsFactory.StarlightSingle(
                new RgbColor(9, 8, 7),
                50,
                StarlightLightingDuration.Medium),
            CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);

        Assert.Equal(LightingRendererOwnershipState.Starlight, port.OwnershipState);
        Assert.Equal(1, session.AnimationBatches);
        Assert.Single(session.Commands, command =>
            command.ClassId == 0x03 && command.CommandId == 0x0A);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        Assert.Equal(2, session.AnimationBatches);

        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50),
            CancellationToken.None);
        int retiredBatches = session.AnimationBatches;
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(retiredBatches, session.AnimationBatches);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task Blade18UniformHostFrame_RespectsTransportBudgetAndCanceledWaitDoesNotWrite()
    {
        var clock = new AnimationClock();
        var session = new Session();
        var policy = Enabled();
        await using var port = new ExperimentalBladePort(
            session, new(), Model, policy, () => true,
            () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        await port.SetLightingAsync(
            LightingSettingsFactory.AudioMeter(1, 50), CancellationToken.None);
        var frame = new UniformLightingFrame(new RgbColor(1, 2, 3));
        await port.PushUniformLightingFrameAsync(frame, CancellationToken.None);
        Task next = port.PushUniformLightingFrameAsync(frame, CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(49));
        Assert.False(next.IsCompleted);
        Assert.Equal(1, session.AnimationBatches);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await next;
        Assert.Equal(2, session.AnimationBatches);
        using var cancellation = new CancellationTokenSource();
        Task canceled = port.PushUniformLightingFrameAsync(frame, cancellation.Token);
        await clock.WaitForTimerCountAsync(2);
        cancellation.Cancel();
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => canceled);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(2, session.AnimationBatches);
        Assert.True(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.AudioMeter, port.OwnershipState);
    }

    private static bool IsBlade18FrameCommand(RazerCommandSpec command) =>
        command.ClassId == 0x03 && command.CommandId is 0x0A or 0x0B;

    private static void AssertBlade18UniformRows(
        IReadOnlyList<RazerCommandSpec> rows,
        RgbColor expected)
    {
        Assert.Equal(Blade18LightingFrame.Rows, rows.Count);
        for (int row = 0; row < rows.Count; row++)
        {
            byte[] payload = rows[row].Arguments.ToArray();
            Assert.Equal(ExperimentalBladeLightingFrame.RowPayloadLength, payload.Length);
            Assert.Equal(new byte[] { 0xFF, (byte)row, 0, 15 }, payload[..4]);
            Assert.All(payload[4..].Chunk(3), rgb =>
                Assert.Equal(new byte[] { expected.Red, expected.Green, expected.Blue }, rgb));
        }
    }
}
