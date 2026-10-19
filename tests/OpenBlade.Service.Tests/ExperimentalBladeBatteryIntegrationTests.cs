using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

public sealed class ExperimentalBladeBatteryIntegrationTests
{
    [Fact]
    public async Task OptionalCapabilities_ArePromotedOnlyAfterSuccessfulExplicitReads()
    {
        var io = new Io { Raw = 0xD0 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        DeviceCapabilities initial = await device.ReadCapabilitiesAsync(CancellationToken.None);
        Assert.False(initial.SupportsTemporaryFullCharge);
        Assert.DoesNotContain(OneShotLaptopControl.StartupAnimation, initial.OneShotLaptopControls);
        Assert.DoesNotContain(io.Commands, command => command is (0x0F, 0x98) or (0x07, 0x8F));

        ConfirmedDeviceState state = await device.ReadStateAsync(CancellationToken.None);
        Assert.False(state.Battery!.TemporaryFullCharge);
        Assert.True(await device.ReadStartupAnimationAsync(CancellationToken.None));
        DeviceCapabilities refreshed = await device.ReadCapabilitiesAsync(CancellationToken.None);

        Assert.True(refreshed.SupportsTemporaryFullCharge);
        Assert.Contains(OneShotLaptopControl.StartupAnimation, refreshed.OneShotLaptopControls);
        Assert.Empty(io.Setters);
        Assert.All(io.Commands, command => Assert.True(command.Id >= 0x80));
    }

    [Fact]
    public async Task StartupGetterStatusFailure_DoesNotBlockIndependentControls()
    {
        var io = new Io { Raw = 0xD0, Mode = 4, Cpu = 3, Gpu = 2, RejectedGetter = (0x0F, 0x98) };
        var native = new Native();
        io.Events = native.Events;
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, native: native);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        Assert.Null(await device.ReadStartupAnimationAsync(CancellationToken.None));
        ConfirmedDeviceState state = await device.ReadStateAsync(CancellationToken.None);

        Assert.Equal(80, state.Battery!.ChargeLimitPercent);
        Assert.False(state.Battery.TemporaryFullCharge);
        Assert.Equal(PerformancePreset.Custom, state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Equal(new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High),
            state.CustomPerformance);
        Assert.Equal(LightingEffect.Spectrum, state.Lighting!.Effect);
        Assert.Equal(LogoLightingMode.Off, state.LogoLighting);
        Assert.Empty(io.Setters);
        Assert.All(io.Commands, command => Assert.True(command.Id >= 0x80));
        Assert.True(native.Events.IndexOf("close:501") < native.Events.IndexOf("open:502"));
        Assert.DoesNotContain(native.Events.Skip(native.Events.IndexOf("close:501") + 1),
            item => item.StartsWith("query:501:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FullChargeStatusFailure_RetainsQueryDiagnosticWithoutInventingBatteryState()
    {
        var io = new Io { Raw = 0xD0, RejectedGetter = (0x07, 0x8F) };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        IOException failure = await Assert.ThrowsAsync<IOException>(() => device.ReadStateAsync(CancellationToken.None));
        NativeDeviceReadException cause = Assert.IsType<NativeDeviceReadException>(failure.InnerException);
        Assert.Equal(DeviceStateQuery.TemporaryFullCharge, cause.StateQuery);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, cause.Failure);
        Assert.Equal(HidProtocolValidationFailure.Status, cause.Diagnostic?.ProtocolValidationFailure);
        Assert.Contains("query=TemporaryFullCharge", failure.Message);
        Assert.Contains("failure=ProtocolFailure", failure.Message);
        Assert.Contains("phase=ProtocolValidation", failure.Message);
        Assert.DoesNotContain("Battery state query failed.", failure.Message);
        Assert.Empty(io.Setters);
        Assert.All(io.Commands, command => Assert.True(command.Id >= 0x80));
    }

    [Theory]
    [InlineData("battery", 0x02A0)]
    [InlineData("battery", 0x029F)]
    [InlineData("performance", 0x02A0)]
    [InlineData("agreement", 0x02A0)]
    [InlineData("custom", 0x02A0)]
    public async Task ContentRejection_PreservesIndependentStateAfterDrainedReopen(string rejected, int productId)
    {
        var io = new Io { Raw = rejected == "battery" ? (byte)0x81 : (byte)0xD0 };
        io.Mode = rejected == "performance" ? (byte)99 : rejected == "custom" ? (byte)4 : (byte)0;
        io.DisagreeModes = rejected == "agreement";
        io.InvalidCustom = rejected == "custom";
        var native = new Native { ProductId = (ushort)productId };
        io.Events = native.Events;
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, native: native, productId: productId);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        RazerProtocolException failure = await Assert.ThrowsAsync<RazerProtocolException>(() =>
            device.ReadStateAsync(CancellationToken.None));
        PartialDeviceStateRead partial = Assert.IsType<PartialDeviceStateRead>(failure.PartialState);
        ConfirmedDeviceState state = partial.State;
        if (rejected == "battery")
        {
            Assert.Null(state.Battery);
            Assert.Equal(PerformancePreset.Balanced, state.Performance);
            Assert.Equal(FanMode.Automatic, state.Fans);
            Assert.Equal(new[] { DeviceSubsystem.Battery }, partial.UnavailableSubsystems);
        }
        else
        {
            Assert.Equal(80, state.Battery!.ChargeLimitPercent);
            Assert.Null(state.Performance);
            Assert.Null(state.Fans);
            Assert.Null(state.HyperBoostActive);
            Assert.Contains(DeviceSubsystem.Performance, partial.UnavailableSubsystems);
            Assert.Contains(DeviceSubsystem.Fans, partial.UnavailableSubsystems);
        }
        if (productId == 0x02A0)
        {
            Assert.Equal(LightingEffect.Spectrum, state.Lighting!.Effect);
            Assert.Equal(LogoLightingMode.Off, state.LogoLighting);
        }
        Assert.Empty(io.Setters);
        Assert.All(io.Commands, command => Assert.True(command.Id >= 0x80));
        Assert.True(native.Events.IndexOf("close:501") < native.Events.IndexOf("open:502"));
        Assert.Contains("close:501", native.Events);
        Assert.Contains("open:502", native.Events);
        Assert.DoesNotContain(native.Events.Skip(native.Events.IndexOf("close:501") + 1),
            item => item.StartsWith("query:501:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x02A0, 2)]
    [InlineData(0x02A0, 0)]
    [InlineData(0x029F, 2)]
    public async Task CustomBoost_PreservesAllIndependentStateThroughProductionProvider(int productId, byte gpu)
    {
        var io = new Io { Mode = 4, Cpu = 3, Gpu = gpu, Raw = 0xD0 };
        var native = new Native { ProductId = (ushort)productId };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, native: native, productId: productId);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        ConfirmedDeviceState state = await device.ReadStateAsync(CancellationToken.None);
        Assert.Equal(new CustomPerformanceSettings(CustomPerformanceLevel.Boost, (CustomPerformanceLevel)gpu), state.CustomPerformance);
        Assert.NotEmpty(io.CustomReadProfiles);
        Assert.All(io.CustomReadProfiles, profile => Assert.Equal(productId == 0x02A0 ? (byte)1 : (byte)0, profile));
        Assert.Equal(PerformancePreset.Custom, state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Equal(80, state.Battery!.ChargeLimitPercent);
        Assert.Equal(LightingEffect.Spectrum, state.Lighting!.Effect);
        Assert.Equal(LogoLightingMode.Off, state.LogoLighting);
        Assert.All(io.Commands, command => Assert.True(command.Id >= 0x80));
    }

    [Theory]
    [InlineData(LightingEffect.Fire, LightingRotationDirection.Clockwise,
        LightingRendererOwnershipState.Fire)]
    [InlineData(LightingEffect.Wheel, LightingRotationDirection.Clockwise,
        LightingRendererOwnershipState.Wheel)]
    [InlineData(LightingEffect.Wheel, LightingRotationDirection.Counterclockwise,
        LightingRendererOwnershipState.Wheel)]
    public async Task Blade18AnimatedLighting_UsesExactSetOnlyRowsAndRetainsOwnership(
        LightingEffect effect,
        LightingRotationDirection direction,
        LightingRendererOwnershipState expectedOwnership)
    {
        var io = new Io();
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        DeviceCapabilities capabilities =
            await device.ReadCapabilitiesAsync(CancellationToken.None);
        Assert.Contains(effect, capabilities.LightingEffects);

        LightingSettings requested = effect == LightingEffect.Fire
            ? LightingSettingsFactory.Fire(50)
            : LightingSettingsFactory.Wheel(direction, 50);
        await device.SetLightingAsync(requested, CancellationToken.None);

        LightingTraffic[] firstFrame = io.LightingTraffic.ToArray()[..7];
        Assert.Equal(6, firstFrame.Count(item => item is { Class: 0x03, Id: 0x0B }));
        for (int row = 0; row < 6; row++)
        {
            LightingTraffic command = firstFrame[row];
            Assert.Equal((byte)0x03, command.Class);
            Assert.Equal((byte)0x0B, command.Id);
            Assert.Equal(52, command.Payload.Length);
            Assert.Equal(new byte[] { 0xFF, (byte)row, 0, 15 }, command.Payload[..4]);
        }
        Assert.Equal((byte)0x03, firstFrame[6].Class);
        Assert.Equal((byte)0x0A, firstFrame[6].Id);
        Assert.Equal(new byte[] { 5, 0 }, firstFrame[6].Payload);
        Assert.Equal(0, io.LightingFrameGetRequests);
        ConfirmedDeviceState state =
            await device.ReadLightingStateAsync(CancellationToken.None);
        Assert.Null(state.Lighting);
        Assert.Equal(
            expectedOwnership,
            await device.ReadLightingRendererOwnershipAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Blade18Fire_SwitchToBuiltinWaitsForInFlightFrameAndStopsOwnership()
    {
        var io = new Io { BlockLightingRowAfter = 7 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Fire(50),
            CancellationToken.None);
        await io.LightingRowBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task change = device.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50),
            CancellationToken.None);
        try
        {
            Assert.False(change.IsCompleted);
        }
        finally
        {
            io.ReleaseLightingRow.TrySetResult();
        }
        await change.WaitAsync(TimeSpan.FromSeconds(2));

        LightingTraffic[] traffic = io.LightingTraffic.ToArray();
        Assert.Equal((byte)0x0F, traffic[^1].Class);
        Assert.Equal((byte)0x02, traffic[^1].Id);
        Assert.Equal(new byte[] { 1, 5, 3 }, traffic[^1].Payload);
        Assert.Equal((byte)3, io.Effect);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            await device.ReadLightingRendererOwnershipAsync(CancellationToken.None));
        Assert.Equal(0, io.LightingFrameGetRequests);
    }

    [Fact]
    public async Task Blade18Wheel_RetirementWaitsForInFlightFrameAndRestoresBaseline()
    {
        var io = new Io { BlockLightingRowAfter = 7 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Wheel(
                LightingRotationDirection.Counterclockwise,
                50),
            CancellationToken.None);
        await io.LightingRowBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<LightingRendererInvalidationResult> retirement =
            provider.InvalidateLightingRendererAsync(CancellationToken.None);
        try
        {
            Assert.False(retirement.IsCompleted);
        }
        finally
        {
            io.ReleaseLightingRow.TrySetResult();
        }
        LightingRendererInvalidationResult result =
            await retirement.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(LightingRendererOwnershipState.Wheel, result.Ownership);
        LightingTraffic[] traffic = io.LightingTraffic.ToArray();
        Assert.Equal((byte)0x0F, traffic[^1].Class);
        Assert.Equal((byte)0x02, traffic[^1].Id);
        Assert.Equal(new byte[] { 1, 5, 3 }, traffic[^1].Payload);
        Assert.Equal((byte)3, io.Effect);
        Assert.Equal(0, io.LightingFrameGetRequests);
    }

    [Fact]
    public async Task Provider_AdmitsRangeChangesFromOffAndConfirmsExactLimit()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var io = new Io { Raw = 0x50 };
        var factory = new ExperimentalBladeFactory(model, new Identity(), new Native(), new Guard(), io,
            () => PowerSource.ExternalUnknown);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready, BatteryValidationFirmwareCompatibility.ModelScoped),
            factory, UnavailableFanRpmFactory());
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        ConfirmedDeviceState baseline = await device.ReadStateAsync(CancellationToken.None);
        Assert.False(baseline.Battery!.ProtectionEnabled);
        Assert.False(baseline.Battery.TemporaryFullCharge);
        var coordinator = new ControlCoordinator(device);
        var desired = new DesiredBatteryState(BatteryPolicy.Create(99), true, false);
        ScopedApplyOutcome changed = await coordinator.ApplyBatteryOutcomeAsync(desired, CancellationToken.None);
        Assert.Equal(ControlResultStatus.Applied, changed.Result.Status);
        Assert.Equal(99, changed.Readback!.Battery!.ChargeLimitPercent);
        Assert.False(changed.Readback.Battery.TemporaryFullCharge);
        Assert.Equal(0xE3, io.Raw);
        Assert.Single(io.Setters);
        Assert.Equal((byte)0xE3, io.Setters[0]);
        ScopedApplyOutcome disabled = await coordinator.ApplyBatteryOutcomeAsync(
            new DesiredBatteryState(BatteryPolicy.Create(80), false, false), CancellationToken.None);
        Assert.Equal(ControlResultStatus.Applied, disabled.Result.Status);
        Assert.False(disabled.Readback!.Battery!.ProtectionEnabled);
        Assert.Null(disabled.Readback.Battery.ChargeLimitPercent);
        Assert.Equal(new byte[] { 0xE3, 0x50 }, io.Setters);

    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task Recovery_UnknownUniformLightingDoesNotBlockUnrelatedControls(int productId)
    {
        var io = new Io { Raw = 0x50 };
        var native = new Native { ProductId = (ushort)productId };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, native: native,
            productId: productId);
        IBladeDevice device = await PauseAsync(provider, io);
        int setterCount = io.Setters.Count;
        int commandCount = io.Commands.Count;
        bool accepted = false;
        ConfirmedDeviceState observed = await provider.ResumeExperimentalWritesAsync(state =>
        {
            Assert.True(provider.IsExperimentalWritesPaused);
            Assert.True(state.Battery!.ProtectionEnabled);
            Assert.Equal(80, state.Battery.ChargeLimitPercent);
            Assert.False(state.Battery.TemporaryFullCharge);
            Assert.Equal(PerformancePreset.Balanced, state.Performance);
            Assert.Equal(FanMode.Automatic, state.Fans);
            Assert.Null(state.Lighting);
            Assert.Equal(LogoLightingMode.Off, state.LogoLighting);
            accepted = true;
            return true;
        }, () => true, CancellationToken.None);
        Assert.True(accepted);
        Assert.False(provider.IsExperimentalWritesPaused);
        Assert.False(provider.GetExperimentalSupportDiagnostics().WritesPaused);
        Assert.Equal(setterCount, io.Setters.Count);
        Assert.All(io.Commands.Skip(commandCount), command => Assert.True(command.Id >= 0x80));
        Assert.Contains(provider.GetExperimentalSupportDiagnostics().Observations,
            item => item.Operation == "WriteRecovery" && item.Outcome == "Resumed" && !item.Submitted);
        Assert.Equal(80, observed.Battery!.ChargeLimitPercent);
        await device.SetChargeLimitAsync(BatteryPolicy.Create(90), CancellationToken.None);
        Assert.Equal(setterCount + 1, io.Setters.Count);
        ConfirmedDeviceState confirmed = await device.ReadStateAsync(CancellationToken.None);
        Assert.Equal(90, confirmed.Battery!.ChargeLimitPercent);
    }

    [Fact]
    public async Task Recovery_RetiresRetainedLightingBeforeFreshAdmissionAndSerializesReads()
    {
        var io = new Io { Raw = 0x50 };
        var native = new Native();
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, native: native);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.ReadLightingStateAsync(CancellationToken.None);
        Assert.Equal(1, native.ActiveSessions);
        await PauseAsync(provider, io);
        Assert.Equal(1, native.ActiveSessions);
        int closedBeforeRecovery = native.ClosedSessions;
        Task<ConfirmedDeviceState>? concurrentRead = null;
        await provider.ResumeExperimentalWritesAsync(_ =>
        {
            Assert.Equal(closedBeforeRecovery + 2, native.ClosedSessions);
            Assert.Equal(0, native.ActiveSessions);
            concurrentRead = device.ReadLightingStateAsync(CancellationToken.None);
            Assert.False(concurrentRead.IsCompleted);
            return true;
        }, () => true, CancellationToken.None);
        Assert.NotNull(concurrentRead);
        ConfirmedDeviceState readback = await concurrentRead;
        Assert.Equal(LightingEffect.Spectrum, readback.Lighting!.Effect);
        Assert.False(provider.IsExperimentalWritesPaused);
        Assert.Single(io.Setters);
    }

    [Fact]
    public async Task Recovery_RejectedReconciliationLeavesPauseAndPreventsLaterWrites()
    {
        var io = new Io { Raw = 0x50 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        IBladeDevice device = await PauseAsync(provider, io);
        int setterCount = io.Setters.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => provider.ResumeExperimentalWritesAsync(
            _ => false, () => true, CancellationToken.None));
        Assert.True(provider.IsExperimentalWritesPaused);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            device.SetChargeLimitAsync(BatteryPolicy.Create(90), CancellationToken.None));
        Assert.Equal(setterCount, io.Setters.Count);
    }

    [Theory]
    [InlineData("battery")]
    [InlineData("performance")]
    public async Task Recovery_UnrepresentableReadbackLeavesPause(string malformed)
    {
        var io = new Io { Raw = 0x50 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        await PauseAsync(provider, io);
        if (malformed == "battery")
        {
            io.Raw = 0x81;
        }
        else
        {
            io.Mode = 99;
        }
        bool accepted = false;
        Exception? failure = await Record.ExceptionAsync(() => provider.ResumeExperimentalWritesAsync(
            _ => { accepted = true; return true; }, () => true, CancellationToken.None));
        Assert.NotNull(failure);
        Assert.IsType<RazerProtocolException>(failure);
        Assert.False(accepted);
        Assert.True(provider.IsExperimentalWritesPaused);
        Assert.Single(io.Setters);
    }

    [Fact]
    public async Task Recovery_MalformedSingleZoneLightingLeavesPause()
    {
        var io = new Io { Raw = 0x50 };
        var native = new Native { ProductId = 0x0255 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, native: native,
            productId: 0x0255);
        await PauseLightingAsync(provider, io);
        io.Effect = 99;
        bool accepted = false;

        Exception? failure = await Record.ExceptionAsync(() => provider.ResumeExperimentalWritesAsync(
            _ => { accepted = true; return true; }, () => true, CancellationToken.None));

        Assert.IsType<DeviceStateContentRejectedException>(failure);
        Assert.False(accepted);
        Assert.True(provider.IsExperimentalWritesPaused);
        Assert.Empty(io.Setters);
        Assert.Single(io.Commands, command => command is { Class: 0x03, Id: 0x0A });
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("authorization")]
    [InlineData("identity")]
    [InlineData("controller")]
    public async Task Recovery_FinalAdmissionFailureLeavesPause(string failure)
    {
        var io = new Io { Raw = 0x50 };
        var identity = new Identity();
        var guard = new Guard();
        using var cancellation = new CancellationTokenSource();
        bool authorized = true;
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, identity, guard);
        await PauseAsync(provider, io);
        bool accepted = false;
        io.AfterLogoRead = () =>
        {
            switch (failure)
            {
                case "cancel": cancellation.Cancel(); break;
                case "authorization": authorized = false; break;
                case "identity": identity.Present = false; break;
                case "controller": guard.Fail = true; break;
            }
        };
        Exception? exception = await Record.ExceptionAsync(() => provider.ResumeExperimentalWritesAsync(
            _ => { accepted = true; return true; }, () => authorized, cancellation.Token));
        Assert.False(accepted);
        Assert.NotNull(exception);
        if (failure == "cancel")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
        else
        {
            Assert.IsType<CapabilityUnavailableException>(exception);
        }
        Assert.True(provider.IsExperimentalWritesPaused);
        Assert.Single(io.Setters);
    }

    [Fact]
    public async Task Recovery_UnpausedProviderDoesNotSubmitCommands()
    {
        var io = new Io { Raw = 0x50 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => provider.ResumeExperimentalWritesAsync(
            _ => true, () => true, CancellationToken.None));
        Assert.Empty(io.Commands);
    }

    [Fact]
    public async Task Blade18FixedFans_UseClassOneGettersThroughRealHidProviderAndRestore()
    {
        var io = new Io { Raw = 0xD0 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetFanControlAsync(new(FanMode.Fixed, 3000, ManualFanCurve.Default), CancellationToken.None);
        ConfirmedDeviceState state = await device.ReadStateAsync(CancellationToken.None);
        Assert.Equal(PerformancePreset.Balanced, state.Performance);
        Assert.Equal(FanMode.Fixed, state.Fans);
        Assert.Equal(new (byte, byte)[] { (1, 30), (2, 30) }, io.FanTargetWrites);
        Assert.NotEmpty(io.FanTargetReadClasses);
        Assert.All(io.FanTargetReadClasses, value => Assert.Equal(1, value));

        await device.SetFanControlAsync(new(FanMode.Fixed, 3800, ManualFanCurve.Default), CancellationToken.None);
        await device.SetFanModeAsync(FanMode.Automatic, CancellationToken.None);
        Assert.Equal(38, io.FirstTarget);
        Assert.Equal(38, io.SecondTarget);
        Assert.Equal(FanMode.Automatic, (await device.ReadStateAsync(CancellationToken.None)).Fans);
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    [Fact]
    public async Task Blade18FixedFans_RealProviderPausesAfterIgnoredFirstTarget()
    {
        var io = new Io { Raw = 0xD0, Fan = 1, IgnoreFanTargets = true };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            device.SetFanControlAsync(new(FanMode.Fixed, 3000, ManualFanCurve.Default), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(io.FanTargetWrites);
        Assert.True(provider.IsExperimentalWritesPaused);
        await device.ReadStateAsync(CancellationToken.None);
        Assert.Single(io.FanTargetWrites);
    }
    private static WindowsVerifiedBladeDeviceProvider CreateProvider(Io io, Identity? identity = null, Guard? guard = null, Native? native = null,
        int productId = 0x02A0, Func<PowerSource>? power = null)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        var factory = new ExperimentalBladeFactory(model, identity ?? new Identity(model.ProductName), native ?? new Native(), guard ?? new Guard(), io,
            power ?? (() => PowerSource.ExternalUnknown));
        return new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready, BatteryValidationFirmwareCompatibility.ModelScoped),
            factory, UnavailableFanRpmFactory(), factory.CreateLightingFactory());
    }

    private static async Task<IBladeDevice> PauseAsync(WindowsVerifiedBladeDeviceProvider provider, Io io)
    {
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        io.FailNextSetterResponse = true;
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            device.SetChargeLimitAsync(BatteryPolicy.Create(80), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.True(provider.IsExperimentalWritesPaused);
        Assert.True(provider.GetExperimentalSupportDiagnostics().WritesPaused);
        Assert.Single(io.Setters);
        return device;
    }

    private static async Task<IBladeDevice> PauseLightingAsync(
        WindowsVerifiedBladeDeviceProvider provider,
        Io io)
    {
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        io.FailNextLightingSetterResponse = true;
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            device.SetLightingAsync(
                LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
                CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.True(provider.IsExperimentalWritesPaused);
        Assert.True(provider.GetExperimentalSupportDiagnostics().WritesPaused);
        Assert.Empty(io.Setters);
        return device;
    }

    private sealed class Identity(string productName = "Blade 18 - RZ09-0484") : IHostIdentitySource
    {
        internal bool Present { get; set; } = true;
        public HostIdentityReadResult ReadExactIdentity() => Present
            ? new(new HostIdentityEvidence("Razer", productName, null, "test"), null)
            : new(null, HidSessionFailureKind.IdentityBlocked);
    }
    private sealed class Guard : IHardwareAccessGuard
    {
        internal bool Fail { get; set; }
        public HardwareAccessDecision Check() => new([], Fail ? "Unavailable" : null);
    }
    private sealed class Native : IHidCandidateNative
    {
        private int nextHandle = 500;
        internal ushort ProductId { get; init; } = 0x02A0;
        internal List<string> Events { get; } = [];
        internal int ActiveSessions { get; private set; }
        internal int ClosedSessions { get; private set; }
        public IReadOnlyList<string> EnumerateDevicePaths() => ["test"];
        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters) =>
            new(new OpenedCandidateHandle(new SafeFileHandle(new IntPtr(++nextHandle), ownsHandle: false)), null);
        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle) => new(0x1532, ProductId, 1, 2, 91);
        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
        {
            SafeFileHandle file = handle.TransferOwnership();
            var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
            ActiveSessions++;
            long id = file.DangerousGetHandle().ToInt64();
            Events.Add($"open:{id}");
            return new(file, bound, () => { }, () =>
            {
                ActiveSessions--;
                ClosedSessions++;
                Events.Add($"close:{id}");
                file.Dispose();
            });
        }
    }
    private sealed class Io : IHidFeatureIo
    {
        private byte[] request = [];
        internal byte Raw { get; set; }
        internal (byte Class, byte Id)? RejectedGetter { get; init; }
        internal byte Mode { get; set; }
        internal byte Fan { get; set; }
        internal byte FirstTarget { get; set; } = 38;
        internal byte SecondTarget { get; set; } = 38;
        internal bool IgnoreFanTargets { get; init; }
        internal List<byte> FanTargetReadClasses { get; } = [];
        internal List<(byte Zone, byte Target)> FanTargetWrites { get; } = [];
        internal byte Effect { get; set; } = 3;
        internal bool DisagreeModes { get; set; }
        internal bool InvalidCustom { get; set; }
        internal byte Cpu { get; set; }
        internal byte Gpu { get; set; }
        internal List<string>? Events { get; set; }
        internal bool FailNextSetterResponse { get; set; }
        internal bool FailNextLightingSetterResponse { get; set; }
        internal Action? AfterLogoRead { get; set; }
        internal List<(byte Class, byte Id)> Commands { get; } = [];
        internal ConcurrentQueue<LightingTraffic> LightingTraffic { get; } = new();
        internal int LightingFrameGetRequests { get; private set; }
        internal int BlockLightingRowAfter { get; init; } = int.MaxValue;
        internal TaskCompletionSource LightingRowBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseLightingRow { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<byte> Setters { get; } = [];
        internal List<byte> CustomReadProfiles { get; } = [];
        private int lightingRows;
        public Task<HidFeatureIoResult> ExecuteAsync(OpenedHidHandle handle, uint code,
            byte[] buffer, CancellationToken caller, CancellationToken deadline)
        {
            if (code == 0x000B0191)
            {
                request = buffer.ToArray();
                Commands.Add((request[7], request[8]));
                if (request[7] == 0x0D && request[8] == 0x87)
                {
                    CustomReadProfiles.Add(request[9]);
                }
                if (request[7] == 0x0D && request[8] == 0x81)
                {
                    FanTargetReadClasses.Add(request[9]);
                }
                if (request[7] == 0x0D && request[8] == 0x02)
                {
                    Mode = request[11];
                    Fan = request[12];
                }
                if (request[7] == 0x0D && request[8] == 0x01)
                {
                    FanTargetWrites.Add((request[10], request[11]));
                    if (!IgnoreFanTargets)
                    {
                        if (request[10] == 1) { FirstTarget = request[11]; }
                        else { SecondTarget = request[11]; }
                    }
                }
                if (IsAnimatedLightingSetCommand(request))
                {
                    byte[] payload = request.AsSpan(9, request[6]).ToArray();
                    LightingTraffic.Enqueue(new(request[7], request[8], payload));
                    if (request[8] == 0x0B
                        && Interlocked.Increment(ref lightingRows) == BlockLightingRowAfter)
                    {
                        LightingRowBlocked.TrySetResult();
                        return CompleteLightingRowAfterReleaseAsync(buffer);
                    }
                }
                if (request[7] == 0x0F && request[8] == 0x02)
                {
                    Effect = request[11];
                    LightingTraffic.Enqueue(new(
                        request[7],
                        request[8],
                        request.AsSpan(9, request[6]).ToArray()));
                }
                Events?.Add($"query:{handle.FileHandle.DangerousGetHandle().ToInt64()}:{request[7]:X2}/{request[8]:X2}");
                if (request[7] == 0x07 && request[8] == 0x12)
                {
                    Raw = request[9];
                    Setters.Add(Raw);
                }
            }
            else
            {
                if (buffer[7] == 0x03 && buffer[8] == 0x8B)
                {
                    LightingFrameGetRequests++;
                    return Task.FromResult(new HidFeatureIoResult(
                        new(false, 0, 31),
                        false,
                        HidFeatureTermination.Completed));
                }
                request.CopyTo(buffer, 0);
                buffer[1] = RejectedGetter is { } rejected
                    && buffer[7] == rejected.Class && buffer[8] == rejected.Id ? (byte)5 : (byte)2;
                if (buffer[7] == 0x07 && buffer[8] == 0x92)
                {
                    buffer[9] = Raw;
                }
                else if (buffer[7] == 0x07 && buffer[8] == 0x12 && FailNextSetterResponse)
                {
                    buffer[9] ^= 1;
                    FailNextSetterResponse = false;
                }
                else if (buffer[7] == 0x03 && buffer[8] == 0x0A
                    && FailNextLightingSetterResponse)
                {
                    buffer[8] ^= 1;
                    FailNextLightingSetterResponse = false;
                }
                else if (buffer[7] == 0x0D && buffer[8] == 0x82)
                {
                    buffer[11] = DisagreeModes && buffer[10] == 2 ? (byte)5 : Mode;
                    buffer[12] = Fan;
                }
                else if (buffer[7] == 0x0D && buffer[8] == 0x81)
                {
                    buffer[11] = buffer[10] == 1 ? FirstTarget : SecondTarget;
                }
                else if (buffer[7] == 0x07 && buffer[8] == 0x8C)
                {
                    buffer[9] = 0x11;
                    buffer[10] = 0x11;
                }
                else if (buffer[7] == 0x0D && buffer[8] == 0x87)
                {
                    buffer[11] = InvalidCustom ? (byte)99 : buffer[10] == 1 ? Cpu : Gpu;
                }
                else if (buffer[7] == 0x03 && buffer[8] == 0x83)
                {
                    buffer[11] = 128;
                }
                else if (buffer[7] == 0x0F && buffer[8] == 0x82)
                {
                    buffer[11] = Effect;
                }
                else if (buffer[7] == 0x03 && buffer[8] == 0x80)
                {
                    AfterLogoRead?.Invoke();
                }
                buffer[89] = 0;
                for (int index = 3; index < 89; index++)
                {
                    buffer[89] ^= buffer[index];
                }
            }
            return Task.FromResult(new HidFeatureIoResult(new(true, buffer.Length, 0), true,
                HidFeatureTermination.Completed));
        }

        private async Task<HidFeatureIoResult> CompleteLightingRowAfterReleaseAsync(
            byte[] buffer)
        {
            await ReleaseLightingRow.Task.ConfigureAwait(false);
            return new HidFeatureIoResult(new(true, buffer.Length, 0), true,
                HidFeatureTermination.Completed);
        }

        private static bool IsAnimatedLightingSetCommand(byte[] report) =>
            report[7] == 0x03
            && (report[8] == 0x0B
                || report[8] == 0x0A
                    && report[6] == 2
                    && report[9] == 5
                    && report[10] == 0);
    }

    private sealed record LightingTraffic(byte Class, byte Id, byte[] Payload);
}
