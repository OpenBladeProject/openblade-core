using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x029D)]
    [InlineData(0x0255)]
    [InlineData(0x02C5)]
    [InlineData(0x02A0)]
    public async Task WakeFromDark_BuiltInEffectIsReadyBeforeBrightness(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 0, Effect = 0 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50),
            CancellationToken.None);

        Assert.Equal(new byte[] { 0x02, 0x03 }, MutationCommandIds(session));
        Assert.Equal(3, session.Effect);
        Assert.Equal(128, session.Raw);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task WakeFromDark_StaticFrameIsCompleteBeforeBrightness(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 0, Effect = 0 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(9, 8, 7), 50),
            CancellationToken.None);

        int brightness = session.Commands.FindIndex(IsBrightnessSetter);
        Assert.True(brightness > 0);
        Assert.Equal(ExperimentalBladeLightingFrame.RowCount,
            session.Commands.Take(brightness).Count(IsFrameRow));
        Assert.Contains(session.Commands.Take(brightness), IsFrameActivation);
        Assert.DoesNotContain(session.Commands.Skip(brightness + 1), IsFrameCommand);
        Assert.Equal(128, session.Raw);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WakeFromDark_Blade15ParameterizedEffectIsConfirmedBeforeBrightness(
        bool breathing)
    {
        ExperimentalBladeModel model = ModelFor(0x0255);
        var session = new Session { Raw = 0, Effect = 0 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
        LightingSettings requested = Blade15ParameterizedSetting(breathing);

        await port.SetLightingAsync(requested, CancellationToken.None);

        int setter = session.Commands.FindIndex(IsSingleZoneEffectSetter);
        int effectQuery = session.Commands.FindIndex(setter + 1, IsEffectQuery);
        int parameterQuery = session.Commands.FindIndex(effectQuery + 1, IsStandardEffectQuery);
        int brightness = session.Commands.FindIndex(IsBrightnessSetter);
        Assert.True(setter < effectQuery);
        Assert.True(effectQuery < parameterQuery);
        Assert.True(parameterQuery < brightness);
        Assert.Equal(breathing ? 2 : 1, session.Effect);
        Assert.Equal(128, session.Raw);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WakeFromDark_Blade15IgnoredParameterizedEffectAckStaysDarkAndPauses(
        bool breathing)
    {
        ExperimentalBladeModel model = ModelFor(0x0255);
        var session = new Session { Raw = 0, Effect = 0, IgnoreEffect = true };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);
        LightingSettings requested = Blade15ParameterizedSetting(breathing);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(requested, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(0, session.Raw);
        Assert.DoesNotContain(session.Commands, IsBrightnessSetter);
        Assert.False(policy.WritesAllowed);

        var retrySession = new Session { Raw = 0, Effect = 0 };
        await using var retry = Create(retrySession, policy, model);
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            retry.SetLightingAsync(requested, CancellationToken.None));
        Assert.Empty(retrySession.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WakeFromDark_Blade15ParameterizedEffectMismatchStaysDark(
        bool breathing)
    {
        ExperimentalBladeModel model = ModelFor(0x0255);
        var session = new Session { Raw = 0, Effect = 0 };
        session.BeforeSubmission = command =>
        {
            if (IsStandardEffectQuery(command))
            {
                session.StandardEffect = breathing
                    ? new byte[] { 3, 1, 0xAA, 0xBB, 0xCC }
                    : new byte[] { 6, 0xAA, 0xBB, 0xCC, 0 };
            }
        };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                Blade15ParameterizedSetting(breathing),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(0, session.Raw);
        Assert.DoesNotContain(session.Commands, IsBrightnessSetter);
        Assert.False(policy.WritesAllowed);
    }

    [Fact]
    public async Task WakeFromDark_Blade18AnimationFrameIsReadyBeforeBrightness()
    {
        var session = new Session { Raw = 0, Effect = 0 };
        var clock = new AnimationClock();
        await using var port = new ExperimentalBladePort(
            session, new(), Model, Enabled(), () => true,
            () => PowerSource.PluggedIn, lightingTimeProvider: clock);

        await port.SetLightingAsync(
            LightingSettingsFactory.StarlightSingle(
                new RgbColor(9, 8, 7),
                50,
                StarlightLightingDuration.Medium),
            CancellationToken.None);

        int brightness = session.Commands.FindIndex(IsBrightnessSetter);
        Assert.Equal(1, session.AnimationBatches);
        Assert.Equal(ExperimentalBladeLightingFrame.RowCount,
            session.Commands.Take(brightness).Count(IsFrameRow));
        Assert.Contains(session.Commands.Take(brightness), IsFrameActivation);
        Assert.Equal(128, session.Raw);
    }

    [Theory]
    [InlineData(0x029F, LightingEffect.AmbientAwareness)]
    [InlineData(0x029F, LightingEffect.AudioMeter)]
    [InlineData(0x02A0, LightingEffect.AmbientAwareness)]
    [InlineData(0x02A0, LightingEffect.AudioMeter)]
    public async Task WakeFromDark_UniformEffectSeedsBlackBeforeBrightnessThenAcceptsSample(
        int productId,
        LightingEffect effect)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 0, Effect = 0 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await port.SetLightingAsync(
            effect == LightingEffect.AmbientAwareness
                ? LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50)
                : LightingSettingsFactory.AudioMeter(1, 50),
            CancellationToken.None);

        int brightness = session.Commands.FindIndex(IsBrightnessSetter);
        RazerCommandSpec[] seedRows = session.Commands.Take(brightness)
            .Where(IsFrameRow)
            .ToArray();
        Assert.Equal(ExperimentalBladeLightingFrame.RowCount, seedRows.Length);
        AssertUniformRows(seedRows, new RgbColor(0, 0, 0));
        Assert.Contains(session.Commands.Take(brightness), IsFrameActivation);
        Assert.Equal(128, session.Raw);

        int beforeSample = session.Commands.Count;
        var sample = new RgbColor(9, 8, 7);
        await port.PushUniformLightingFrameAsync(
            new UniformLightingFrame(sample),
            CancellationToken.None);

        RazerCommandSpec[] sampleRows = session.Commands.Skip(beforeSample)
            .Where(IsFrameRow)
            .ToArray();
        Assert.Equal(ExperimentalBladeLightingFrame.RowCount, sampleRows.Length);
        AssertUniformRows(sampleRows, sample);
        Assert.DoesNotContain(session.Commands.Skip(beforeSample), IsBrightnessSetter);
    }

    [Fact]
    public async Task WakeFromDark_UniformFrameFailureLeavesBrightnessDarkAndPauses()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var session = new Session { Raw = 0, Effect = 0, FailFrameRow = true };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(0, session.Raw);
        Assert.DoesNotContain(session.Commands, IsBrightnessSetter);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.False(policy.WritesAllowed);
    }

    [Fact]
    public async Task WakeFromDark_DeferredBrightnessFailureFaultsAfterCompleteUniformFrame()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var session = new Session { Raw = 0, Effect = 0, IgnoreBrightness = true };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.AudioMeter(1, 50),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        int brightness = session.Commands.FindIndex(IsBrightnessSetter);
        Assert.Equal(ExperimentalBladeLightingFrame.RowCount,
            session.Commands.Take(brightness).Count(IsFrameRow));
        Assert.Contains(session.Commands.Take(brightness), IsFrameActivation);
        Assert.Equal(0, session.Raw);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.False(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task WakeFromDark_UniformRelinquishRestoresOriginalZeroBrightness(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 0, Effect = 0 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await port.SetLightingAsync(
            LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50),
            CancellationToken.None);
        Assert.Equal(128, session.Raw);

        await port.RelinquishRendererAsync(CancellationToken.None);

        Assert.Equal(0, session.Raw);
        Assert.Equal(0, session.Effect);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    private static byte[] MutationCommandIds(Session session) =>
        session.Commands
            .Where(command => command.CommandId < 0x80)
            .Select(command => command.CommandId)
            .ToArray();

    private static bool IsBrightnessSetter(RazerCommandSpec command) =>
        command.ClassId == 0x03 && command.CommandId == 0x03;

    private static LightingSettings Blade15ParameterizedSetting(bool breathing) =>
        breathing
            ? LightingSettingsFactory.BreathingSingle(new RgbColor(9, 8, 7), 50)
            : LightingSettingsFactory.Static(new RgbColor(9, 8, 7), 50);

    private static bool IsSingleZoneEffectSetter(RazerCommandSpec command) =>
        command.ClassId == 0x03 && command.CommandId == 0x0A
        && command.Arguments.Span[0] is 3 or 6;

    private static bool IsEffectQuery(RazerCommandSpec command) =>
        command.ClassId == 0x0F && command.CommandId == 0x82;

    private static bool IsStandardEffectQuery(RazerCommandSpec command) =>
        command.ClassId == 0x03 && command.CommandId == 0x8A;

    private static bool IsFrameRow(RazerCommandSpec command) =>
        command.ClassId == 0x03 && command.CommandId == 0x0B;

    private static bool IsFrameActivation(RazerCommandSpec command) =>
        command.ClassId == 0x03 && command.CommandId == 0x0A
        && command.Arguments.Span.SequenceEqual(new byte[] { 5, 0 });

    private static bool IsFrameCommand(RazerCommandSpec command) =>
        IsFrameRow(command) || IsFrameActivation(command);

    private static void AssertUniformRows(
        IReadOnlyList<RazerCommandSpec> rows,
        RgbColor expected)
    {
        for (int row = 0; row < rows.Count; row++)
        {
            byte[] payload = rows[row].Arguments.ToArray();
            Assert.Equal(new byte[] { 0xFF, (byte)row, 0, 15 }, payload[..4]);
            Assert.All(payload[4..].Chunk(3), rgb =>
                Assert.Equal(new byte[] { expected.Red, expected.Green, expected.Blue }, rgb));
        }
    }
}
