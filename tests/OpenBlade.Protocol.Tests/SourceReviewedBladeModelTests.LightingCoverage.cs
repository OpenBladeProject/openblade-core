using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    public static TheoryData<int, bool> IndependentLightingProducts => new()
    {
        { 0x0234, true },
        { 0x0245, true },
        { 0x0246, false },
        { 0x024C, true },
        { 0x0253, true },
        { 0x0268, false },
        { 0x0270, true },
        { 0x0276, true },
        { 0x02B6, true },
    };

    [Theory]
    [MemberData(nameof(IndependentLightingProducts))]
    public async Task IndependentLighting_ExposesOnlyReviewedFieldsAndNeverQueriesAnEffect(int product, bool hasLogo)
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(Model(product), true);
        Assert.True(capabilities.SupportsIndependentLightingControls);
        Assert.True(capabilities.SupportsKeyboardBrightness);
        Assert.Equal(hasLogo ? 3 : 0, capabilities.LogoLightingModes.Count);
        Assert.Empty(capabilities.LightingEffects);
        Assert.NotEmpty(capabilities.ObservedLightingEffects);
        Assert.False(capabilities.SupportsUniformLightingFrames);
        Assert.Null(capabilities.KeyboardLightingLayoutId);
        var session = new Session();
        await using var port = Create(product, session);
        Assert.Null(await port.ReadStateAsync(default));
        Assert.Empty(session.Commands);
        Assert.Equal(new IndependentLightingSettings(50, hasLogo ? LogoLightingMode.Static : null),
            await port.ReadIndependentLightingAsync(default));
        Assert.All(session.Commands, command => Assert.True(command.ClassId == 3
            && command.CommandId is 0x83 or 0x80 or 0x82));
        if (!hasLogo)
        {
            Assert.Single(session.Commands);
        }
    }

    [Theory]
    [MemberData(nameof(IndependentLightingProducts))]
    public async Task IndependentLighting_ConfirmsOnlyRequestedBrightnessAndRetainsUnknownEffects(int product, bool hasLogo)
    {
        var session = new Session();
        await using var port = Create(product, session);
        await port.SetIndependentLightingAsync(new(73, null), default);
        Assert.Equal(187, session.Brightness);
        Assert.Equal(new IndependentLightingSettings(73, null),
            await port.ReadIndependentLightingAsync(new(0, null), default));
        Assert.All(session.Commands, command => Assert.True(command.ClassId == 3
            && command.CommandId is 3 or 0x83));
        Assert.Single(session.Commands, command => command.CommandId == 3);
        Assert.Null(await port.ReadStateAsync(default));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(73), default));
        Assert.Equal(hasLogo ? 3 : 0, port.Capabilities.LogoLightingModes.Count);
    }

    [Theory]
    [MemberData(nameof(IndependentLightingProducts))]
    public async Task IndependentLighting_LogoUsesOwnReadbackOrIsRejectedBeforeAnyWrite(int product, bool hasLogo)
    {
        var session = new Session();
        await using var port = Create(product, session);
        if (!hasLogo)
        {
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetIndependentLightingAsync(
                new(73, LogoLightingMode.Breathing), default));
            Assert.Empty(session.Commands);
            return;
        }
        await port.SetIndependentLightingAsync(new(null, LogoLightingMode.Breathing), default);
        Assert.Equal(LogoLightingMode.Breathing, await port.ReadLogoStateAsync(default));
        Assert.Equal(128, session.Brightness);
        Assert.All(session.Commands, command =>
        {
            Assert.Equal(3, command.ClassId);
            Assert.Equal(new byte[] { 0, 4 }, command.Arguments.Span[..2].ToArray());
            Assert.Contains(command.CommandId, new byte[] { 0, 2, 0x80, 0x82 });
        });
        int effect = session.Commands.FindIndex(command => command.CommandId == 2);
        int state = session.Commands.FindIndex(command => command.CommandId == 0);
        Assert.True(effect >= 0 && state > effect);
        Assert.Contains(session.Commands.Skip(effect + 1).Take(state - effect - 1),
            command => command.CommandId == 0x82);
    }

    [Theory]
    [MemberData(nameof(IndependentLightingProducts))]
    public async Task IndependentLighting_UnconfirmedWriteStopsWithoutReplayAndRecoveryIsReadOnly(int product, bool hasLogo)
    {
        var session = new Session { IgnoreWrites = true };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetIndependentLightingAsync(new(73, null), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetIndependentLightingAsync(new(73, null), default));
        Assert.Equal(before, session.Commands.Count);
        ConfirmedDeviceState recovered = await port.ReadRecoveryStateAsync(default);
        Assert.Null(recovered.Lighting);
        Assert.Equal(new IndependentLightingSettings(50, hasLogo ? LogoLightingMode.Static : null),
            recovered.IndependentLighting);
        Assert.All(session.Commands.Skip(before), command => Assert.True(command.CommandId >= 0x80));
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0F || command.CommandId == 0x8A);
        Assert.False(policy.WritesAllowed);
    }
}
