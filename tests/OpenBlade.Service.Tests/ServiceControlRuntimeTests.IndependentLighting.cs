using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task IndependentLighting_RuntimeMergesPersistedIntentButDispatchesOnlyRequestedPatch()
    {
        string path = Path.Combine(directory, "independent-overrides.json");
        TestContext context = await CreateIndependentLightingContextAsync(path);
        LightingSettings wrapper = context.Runtime.GetStatus().DesiredState.Lighting.Value;
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetIndependentLightingOverrideAsync(
            new(new(70, null), OverrideLifetime.Persistent), default)).Status);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { IndependentLighting = new(55, LogoLightingMode.Static) };
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetIndependentLightingOverrideAsync(
            new(new(null, LogoLightingMode.Breathing), OverrideLifetime.Persistent), default)).Status);
        Assert.Equal(new IndependentLightingSettings(null, LogoLightingMode.Breathing), context.Device.IndependentLightingWrites.Last());
        Assert.Equal<byte?>(70, context.Runtime.GetStatus().ConfirmedState!.IndependentLighting!.Brightness);
        Assert.Null(context.Runtime.GetStatus().ConfirmedState!.Lighting);
        Assert.Equal(0, context.Device.LightingSetCalls);
        LightingSettings saved = (await new PersistentOverrideStore(path).LoadAsync(default)).Overrides.Lighting!;
        Assert.Equal(wrapper.Effect, saved.Effect);
        Assert.Equal(wrapper.Brightness, saved.Brightness);
        Assert.Equal(new IndependentLightingSettings(70, LogoLightingMode.Breathing), saved.IndependentControls);
        // A third patch must merge the authoritative saved intent, not the prior
        // partial hardware target published for independent confirmation.
        await context.Runtime.SetIndependentLightingOverrideAsync(new(new(null, LogoLightingMode.Off), OverrideLifetime.Persistent), default);
        saved = (await new PersistentOverrideStore(path).LoadAsync(default)).Overrides.Lighting!;
        Assert.Equal(new IndependentLightingSettings(70, LogoLightingMode.Off), saved.IndependentControls);
    }

    [Fact]
    public async Task IndependentLighting_RuntimeRejectsExplicitEffectsAndNativeSteps()
    {
        TestContext context = await CreateIndependentLightingContextAsync();
        int before = context.Device.LightingSetCalls;
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.SetLightingOverrideAsync(
            new(LightingSettingsFactory.SpectrumCycling(70), OverrideLifetime.Persistent), default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection.Increase, OverrideLifetime.Persistent, default)).Status);
        Assert.Equal(before, context.Device.LightingSetCalls);
        Assert.Empty(context.Device.IndependentLightingWrites);
    }

    [Fact]
    public async Task IndependentLighting_PowerPolicyRequiresBrightnessOptInAndPreservesLogo()
    {
        TestContext context = await CreateIndependentLightingContextAsync();
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        await context.Runtime.UpdateProfilesAsync(profiles with
        {
            PluggedIn = profiles.PluggedIn with { LightingPowerPolicy = new(true, null) },
        }, default);
        context.Device.IndependentLightingWrites.Clear();
        Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.SetLightingSuppressionAsync(
            LightingSuppressionReason.DisplayOff, true, default)).Status);
        Assert.Empty(context.Device.IndependentLightingWrites);
        await context.Runtime.SetLightingSuppressionAsync(LightingSuppressionReason.DisplayOff, false, default);
        await context.Runtime.SetIndependentLightingOverrideAsync(new(new(null, LogoLightingMode.Breathing), OverrideLifetime.Persistent), default);
        context.Device.IndependentLightingWrites.Clear();
        await context.Runtime.SetLightingSuppressionAsync(LightingSuppressionReason.DisplayOff, true, default);
        Assert.Empty(context.Device.IndependentLightingWrites);
        Assert.Equal<byte?>(50, context.Device.ConfirmedState.IndependentLighting!.Brightness);
        await context.Runtime.SetLightingSuppressionAsync(LightingSuppressionReason.DisplayOff, false, default);
        await context.Runtime.SetIndependentLightingOverrideAsync(new(new(70, null), OverrideLifetime.Persistent), default);
        context.Device.IndependentLightingWrites.Clear();
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetLightingSuppressionAsync(
            LightingSuppressionReason.DisplayOff, true, default)).Status);
        Assert.Equal(new IndependentLightingSettings(0, LogoLightingMode.Breathing), context.Device.IndependentLightingWrites.Last());
        Assert.Equal(LogoLightingMode.Breathing, context.Device.ConfirmedState.IndependentLighting!.LogoMode);
        Assert.Null(context.Device.ConfirmedState.Lighting);
        await context.Runtime.SetLightingSuppressionAsync(LightingSuppressionReason.DisplayOff, false, default);
        Assert.Equal<byte?>(70, context.Device.ConfirmedState.IndependentLighting!.Brightness);
        Assert.Equal(0, context.Device.LightingSetCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentLighting_UnconfirmedRuntimeWriteBlocksReplayAndReconcilesRequestedFieldsOnly(bool hybrid)
    {
        TestContext context = await CreateIndependentLightingContextAsync(hybrid: hybrid);
        context.Device.SuppressConfirmationFor = DeviceSubsystem.Lighting;
        ControlOperationResult failed = await context.Runtime.SetIndependentLightingOverrideAsync(
            new(new(70, null), OverrideLifetime.Persistent), default);
        Assert.NotEqual(ControlOperationStatus.Applied, failed.Status);
        Assert.Contains(DeviceSubsystem.Lighting, context.Runtime.GetStatus().UnconfirmedWrites);
        int writes = context.Device.IndependentLightingWrites.Count;
        await context.Runtime.SetIndependentLightingOverrideAsync(new(new(70, null), OverrideLifetime.Persistent), default);
        Assert.Equal(writes, context.Device.IndependentLightingWrites.Count);
        context.Device.SuppressConfirmationFor = null;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            IndependentLighting = new(70, null),
            LogoLighting = null,
        };
        await context.Runtime.ObserveAsync(default);
        Assert.DoesNotContain(DeviceSubsystem.Lighting, context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(writes, context.Device.IndependentLightingWrites.Count);
        Assert.Null(context.Runtime.GetStatus().ConfirmedState!.Lighting);
    }

    [Fact]
    public async Task IndependentLighting_ScopedReadbackPreservesUnrequestedButClearsUnavailableRequestedField()
    {
        TestContext context = await CreateIndependentLightingContextAsync();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Lighting)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with { IndependentLighting = null, LogoLighting = null };
            }
        };
        await context.Runtime.SetIndependentLightingOverrideAsync(new(new(70, null), OverrideLifetime.Persistent), default);
        ConfirmedDeviceState confirmed = context.Runtime.GetStatus().ConfirmedState!;
        Assert.Null(confirmed.IndependentLighting!.Brightness);
        Assert.Equal(LogoLightingMode.Static, confirmed.IndependentLighting.LogoMode);
        Assert.Equal(LogoLightingMode.Static, confirmed.LogoLighting);
        Assert.Null(confirmed.Lighting);
        Assert.Contains(DeviceSubsystem.Lighting, context.Runtime.GetStatus().UnconfirmedWrites);
    }

    [Fact]
    public async Task IndependentLighting_ReadOnlyResumeBlocksSuppressionReplayUntilExplicitRequest()
    {
        TestContext context = await CreateIndependentLightingContextAsync();
        context.Provider.AvailableWhenExperimentalAllowed = true;
        await context.Runtime.SetExternalLightingOverridePolicyAsync(true, default);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        await context.Runtime.UpdateProfilesAsync(profiles with
        {
            PluggedIn = profiles.PluggedIn with { LightingPowerPolicy = new(true, null), Lighting = profiles.PluggedIn.Lighting with { IndependentControls = new(70, null) } },
        }, default);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        var pending = Assert.IsType<PendingWriteConfirmationTracker>(typeof(ServiceControlRuntime)
            .GetField("pendingWriteConfirmations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(context.Runtime));
        pending.Set(DeviceSubsystem.Lighting, new PendingWriteConfirmation(before.ActivePowerSource,
            before.DesiredState with
            {
                Lighting = before.DesiredState.Lighting with { Value = before.Profiles.PluggedIn.Lighting },
            }, before.ConfirmedState!));
        context.Provider.IsExperimentalWritesPaused = true;
        int writes = context.Device.IndependentLightingWrites.Count;
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.ResumeExperimentalWritesAsync(static () => true, default)).Status);
        await context.Runtime.SetLightingSuppressionAsync(LightingSuppressionReason.DisplayOff, true, default);
        await context.Runtime.SetLightingSuppressionAsync(LightingSuppressionReason.DisplayOff, false, default);
        Assert.Equal(writes, context.Device.IndependentLightingWrites.Count);
        profiles = context.Runtime.GetStatus().Profiles;
        await context.Runtime.UpdateProfilesAsync(profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                Lighting = LightingSettingsFactory.SpectrumCycling(22) with { IndependentControls = profiles.PluggedIn.Lighting.IndependentControls },
            },
        }, default);
        await context.Runtime.SetLightingSuppressionAsync(LightingSuppressionReason.DisplayOff, true, default);
        await context.Runtime.SetLightingSuppressionAsync(LightingSuppressionReason.DisplayOff, false, default);
        Assert.Equal(writes, context.Device.IndependentLightingWrites.Count);
        await context.Runtime.SetIndependentLightingOverrideAsync(new(new(75, null), OverrideLifetime.Persistent), default);
        Assert.Equal(writes + 1, context.Device.IndependentLightingWrites.Count);
    }

    [Fact]
    public async Task HybridLighting_DispatchesExplicitEffectAndIndependentPatchSeparately()
    {
        TestContext context = await CreateIndependentLightingContextAsync(hybrid: true);
        LightingSettings effect = LightingSettingsFactory.SpectrumCycling(70) with { ControlsKeyboardEffect = true };
        int before = context.Device.LightingSetCalls;
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Lighting && context.Device.ConfirmedState.Lighting is { } observed)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Lighting = observed with { ControlsKeyboardEffect = false },
                };
            }
        };
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetLightingOverrideAsync(
            new(effect, OverrideLifetime.Persistent), default)).Status);
        Assert.Equal(before + 1, context.Device.LightingSetCalls);
        Assert.Empty(context.Device.IndependentLightingWrites);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Lighting = null };
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetIndependentLightingOverrideAsync(
            new(new(42, null), OverrideLifetime.Persistent), default)).Status);
        Assert.Equal(before + 1, context.Device.LightingSetCalls);
        Assert.Equal(new IndependentLightingSettings(42, null), Assert.Single(context.Device.IndependentLightingWrites));
        Assert.Equal(effect.Effect, context.Runtime.GetStatus().LightingOverride!.Value.Effect);
        Assert.Equal(new IndependentLightingSettings(42, null), context.Runtime.GetStatus().LightingOverride!.Value.IndependentControls);
        Assert.Null(context.Runtime.GetStatus().ConfirmedState!.Lighting);
    }

    [Fact]
    public async Task HybridLighting_FullIntentPowerPolicyUsesEffectOffAndPreservesOptIn()
    {
        TestContext context = await CreateIndependentLightingContextAsync(hybrid: true);
        await context.Runtime.SetLightingOverrideAsync(new(
            LightingSettingsFactory.SpectrumCycling(70) with { ControlsKeyboardEffect = true }, OverrideLifetime.Persistent), default);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        await context.Runtime.UpdateProfilesAsync(profiles with
        {
            PluggedIn = profiles.PluggedIn with { LightingPowerPolicy = new(true, null) },
        }, default);
        int before = context.Device.LightingSetCalls;
        ControlOperationResult result = await context.Runtime.SetLightingSuppressionAsync(LightingSuppressionReason.DisplayOff, true, default);
        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(before + 1, context.Device.LightingSetCalls);
        Assert.Equal(LightingEffect.Off, context.Device.ConfirmedState.Lighting!.Effect);
        Assert.True(context.Runtime.GetStatus().LightingOverride!.Value.ControlsKeyboardEffect);
        Assert.Empty(context.Device.IndependentLightingWrites);
    }

    [Fact]
    public async Task HybridLighting_LegacyShapeOffRetainsOptInAndActuallyDispatches()
    {
        TestContext context = await CreateIndependentLightingContextAsync(hybrid: true);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = LightingSettingsFactory.SpectrumCycling(70),
        };
        var legacy = new LightingSettings(LightingEffect.Off, default, 0)
        {
            ControlsKeyboardEffect = true,
            LogoMode = LogoLightingMode.Static,
        };
        int before = context.Device.LightingSetCalls;
        ControlOperationResult result = await context.Runtime.SetLightingOverrideAsync(
            new(legacy, OverrideLifetime.Persistent), default);
        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(before + 1, context.Device.LightingSetCalls);
        Assert.Empty(context.Device.IndependentLightingWrites);
        LightingSettings saved = context.Runtime.GetStatus().LightingOverride!.Value;
        Assert.True(saved.ControlsKeyboardEffect);
        Assert.Equal(LightingSettingsFactory.Off().Parameters, saved.Parameters);
        Assert.Equal(LogoLightingMode.Static, saved.LogoMode);
        Assert.Equal(LightingEffect.Off, context.Device.ConfirmedState.Lighting!.Effect);
    }

    private async Task<TestContext> CreateIndependentLightingContextAsync(string? overridePath = null, bool hybrid = false)
    {
        TestContext context = CreateContext(true, persistentOverridePath: overridePath);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            SupportsIndependentLightingControls = true,
            SupportsKeyboardBrightness = true,
            LightingEffects = hybrid
                ? new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Static, LightingEffect.Spectrum }
                : new HashSet<LightingEffect>(),
            LogoLightingModes = Enum.GetValues<LogoLightingMode>().ToHashSet(),
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = null,
            IndependentLighting = new(50, LogoLightingMode.Static),
            LogoLighting = LogoLightingMode.Static,
        };
        await context.Runtime.ObserveAsync(default);
        Assert.True(context.Runtime.GetStatus().Capabilities!.SupportsIndependentLightingControls);
        return context;
    }
}
