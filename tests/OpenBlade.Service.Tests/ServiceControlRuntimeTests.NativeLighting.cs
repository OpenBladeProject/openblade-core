using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(LightingEffect.Wave, false)]
    [InlineData(LightingEffect.Wave, true)]
    [InlineData(LightingEffect.Spectrum, false)]
    [InlineData(LightingEffect.Spectrum, true)]
    [InlineData(LightingEffect.Breathing, false)]
    [InlineData(LightingEffect.Breathing, true)]
    [InlineData(LightingEffect.Static, false)]
    [InlineData(LightingEffect.Static, true)]
    public async Task NativeLighting_WithoutRendererOwnershipAppliesWithoutRecoveryReplay(
        LightingEffect effect, bool profileTransition)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off, LightingEffect.Static, LightingEffect.Spectrum,
                LightingEffect.Breathing, LightingEffect.Wave,
            },
        };
        TestContext context = CreateWritableContext(
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities));
        using var runtime = context.Runtime;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LightingEffects = capabilities.LightingEffects,
        };
        context.Device.LightingRequiresRendererOwnership = false;
        context.Device.LightingOwnershipAfterSet = LightingRendererOwnershipState.None;
        await runtime.ObserveAsync(CancellationToken.None);
        int before = context.Device.LightingSetCalls;
        LightingSettings target = effect switch
        {
            LightingEffect.Wave => LightingSettingsFactory.Wave(LightingDirection.Rightward, 50),
            LightingEffect.Spectrum => LightingSettingsFactory.SpectrumCycling(50),
            LightingEffect.Breathing => LightingSettingsFactory.BreathingRandom(50),
            LightingEffect.Static => LightingSettingsFactory.Static(new RgbColor(9, 8, 7), 50),
            _ => throw new InvalidOperationException(),
        };
        if (effect == LightingEffect.Wave)
        {
            // Compact firmware readback confirms effect/brightness in the backend,
            // but cannot provide a complete setting without the direction byte.
            context.Device.AfterSetter = subsystem =>
            {
                if (subsystem == DeviceSubsystem.Lighting)
                {
                    context.Device.ConfirmedState = context.Device.ConfirmedState with { Lighting = null };
                }
            };
        }

        if (profileTransition)
        {
            ProfileSet profiles = runtime.GetStatus().Profiles;
            await runtime.UpdateProfilesAsync(profiles with
            {
                Battery = profiles.Battery with { Lighting = target },
            }, CancellationToken.None);
            context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
            await runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
            Assert.DoesNotContain("did not remain active", runtime.GetStatus().Message);
        }
        else
        {
            ControlOperationResult result = await runtime.SetLightingOverrideAsync(
                new(target, OverrideLifetime.Persistent), CancellationToken.None);
            Assert.Equal(ControlOperationStatus.Applied, result.Status);
        }

        Assert.Equal(before + 1, context.Device.LightingSetCalls);
        Assert.Equal(target, runtime.GetStatus().DesiredState.Lighting.Value);
        Assert.Equal(LightingRendererOwnershipState.None, runtime.GetStatus().LightingRendererOwnership);
        Assert.DoesNotContain(DeviceSubsystem.Lighting, runtime.GetStatus().UnconfirmedWrites);
        if (effect == LightingEffect.Wave)
        {
            Assert.Null(runtime.GetStatus().ConfirmedState!.Lighting);
        }
        else
        {
            Assert.Equal(target, runtime.GetStatus().ConfirmedState!.Lighting);
        }
        await runtime.ObserveAsync(CancellationToken.None);
        await runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(before + 1, context.Device.LightingSetCalls);
        Assert.DoesNotContain(DeviceSubsystem.Lighting, runtime.GetStatus().ExternalChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeLighting_SuccessSupersedesQueuedHostRecovery(bool profileTransition)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off, LightingEffect.Static, LightingEffect.Spectrum,
            },
        };
        TestContext context = CreateWritableContext(
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities));
        using var runtime = context.Runtime;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LightingEffects = capabilities.LightingEffects,
        };
        await runtime.ObserveAsync(CancellationToken.None);
        context.Device.LightingOwnershipAfterSet = LightingRendererOwnershipState.None;
        LightingSettings host = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50);
        ControlOperationResult failed = await runtime.SetLightingOverrideAsync(
            new(host, OverrideLifetime.UntilPowerSourceChange), CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Failed, failed.Status);
        int afterFailure = context.Device.LightingSetCalls;

        context.Device.LightingRequiresRendererOwnership = false;
        LightingSettings native = LightingSettingsFactory.SpectrumCycling(50);
        if (profileTransition)
        {
            ProfileSet profiles = runtime.GetStatus().Profiles;
            await runtime.UpdateProfilesAsync(profiles with
            {
                Battery = profiles.Battery with { Lighting = native },
            }, CancellationToken.None);
            context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
            await runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        }
        else
        {
            ControlOperationResult applied = await runtime.SetLightingOverrideAsync(
                new(native, OverrideLifetime.Persistent), CancellationToken.None);
            Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        }

        Assert.Equal(afterFailure + 1, context.Device.LightingSetCalls);
        Assert.Equal(native, runtime.GetStatus().DesiredState.Lighting.Value);
        await runtime.ObserveAsync(CancellationToken.None);
        await runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(afterFailure + 1, context.Device.LightingSetCalls);
        Assert.Equal(native, context.Device.ConfirmedState.Lighting);
        Assert.Equal(LightingRendererOwnershipState.None, runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task HostLighting_WithoutRequiredRendererStillFails()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        using var runtime = context.Runtime;
        context.Device.LightingOwnershipAfterSet = LightingRendererOwnershipState.None;

        ControlOperationResult result = await runtime.SetLightingOverrideAsync(
            new(LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50), OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Contains("did not remain active", result.Message);
    }

    [Fact]
    public async Task KeyboardBrightnessStep_DoesNotCancelMissingHostRendererRecovery()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        using var runtime = context.Runtime;
        context.Device.LightingOwnershipAfterSet = LightingRendererOwnershipState.None;
        LightingSettings host = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50);
        ControlOperationResult failed = await runtime.SetLightingOverrideAsync(
            new(host, OverrideLifetime.Persistent), CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Failed, failed.Status);
        int before = context.Device.LightingSetCalls;
        context.Device.NextKeyboardBrightness = 44;

        ControlOperationResult step = await runtime.StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection.Decrease, OverrideLifetime.Persistent, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, step.Status);
        Assert.Equal(before, context.Device.LightingSetCalls);
        Assert.Equal(LightingRendererOwnershipState.None, runtime.GetStatus().LightingRendererOwnership);
        // The step changes brightness only. The queued host effect still needs
        // its one bounded recovery once the renderer can acquire ownership.
        context.Device.LightingOwnershipAfterSet = null;
        await runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(before + 1, context.Device.LightingSetCalls);
        Assert.Equal(LightingRendererOwnershipState.Static, runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task NativeLighting_OffStillRequiresPreviousRendererToBeGone()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        using var runtime = context.Runtime;
        context.Device.LightingRequiresRendererOwnership = false;
        context.Device.LightingOwnershipAfterSet = LightingRendererOwnershipState.Static;

        ControlOperationResult result = await runtime.SetLightingOverrideAsync(
            new(LightingSettingsFactory.Off(), OverrideLifetime.Persistent), CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Contains("did not remain active", result.Message);
    }
}
