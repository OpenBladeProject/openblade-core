using System.Reflection;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task LaptopReview_PendingDiagnosticReachesExplicitControl()
    {
        TestContext context = await CreateLaptopBehaviorTransitionContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.AfterOneShotSet = _ => context.Device.FunctionKeyPrimary = null;
        await runtime.ApplyOneShotLaptopControlAsync(FunctionKeysRequest, default);
        int writes = context.Device.TotalSetterCalls;

        ControlOperationResult refused = await runtime.ApplyOneShotLaptopControlAsync(FunctionKeysRequest, default);

        Assert.Equal(ControlOperationStatus.Blocked, refused.Status);
        Assert.Contains("must read the pending laptop-control state", refused.Message, StringComparison.Ordinal);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaptopReview_PowerDestinationRemainsUnconsumedUntilAfterReadOnlyRecovery(bool enableFullCharge)
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl> { OneShotLaptopControl.FunctionKeyPrimary },
        };
        context.Device.FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys;
        await context.Runtime.ObserveAsync(default);
        using ServiceControlRuntime runtime = context.Runtime;
        await runtime.ReconcileStartupProfileAsync(default);
        ProfileSet profiles = runtime.GetStatus().Profiles;
        LightingSettings destination = LightingSettingsFactory.Static(new RgbColor(17, 31, 47), 77);
        await runtime.UpdateProfilesAsync(profiles with
        {
            Battery = profiles.Battery with { Lighting = destination },
        }, default);
        Assert.Equal(destination, runtime.GetStatus().Profiles.Battery.Lighting);
        context.Device.AfterOneShotSet = _ => context.Device.FunctionKeyPrimary = null;
        ControlOperationResult uncertain = await runtime.ApplyOneShotLaptopControlAsync(FunctionKeysRequest, default);
        Assert.True(runtime.GetStatus().WritesBlocked, uncertain.Message);
        Assert.Equal(1, context.Device.OneShotSetCalls);
        int writes = context.Device.TotalSetterCalls;
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await runtime.SetFullChargeOverrideAsync(enableFullCharge, default);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
        Assert.Equal(PowerSource.PluggedIn, Assert.IsType<ProfileAutomationState>(PrivateField("automation").GetValue(runtime)).PowerSource);
        await runtime.ObserveAsync(default);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
        Assert.Equal(PowerSource.Battery, runtime.GetStatus().ActivePowerSource);
        Assert.Contains("must read the pending laptop-control state", runtime.GetStatus().Message, StringComparison.Ordinal);
        Assert.Contains("controls still show the previously active targets", runtime.GetStatus().Message, StringComparison.Ordinal);
        context.Device.AfterOneShotSet = null;
        context.Device.FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys;

        await runtime.ObserveAsync(default);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
        Assert.Contains("reconciled without writes", runtime.GetStatus().Message, StringComparison.Ordinal);
        Assert.Contains("controls still show the previously active targets", runtime.GetStatus().Message, StringComparison.Ordinal);
        Assert.False(context.Provider.RecoveryWriteAdmission!());
        await runtime.ObserveAsync(default);
        Assert.True(context.Provider.RecoveryWriteAdmission!());
        Assert.DoesNotContain("previously active targets", runtime.GetStatus().Message ?? string.Empty, StringComparison.Ordinal);

        Assert.True(destination == context.Device.ConfirmedState.Lighting, $"{runtime.GetStatus().Message} actual={context.Device.ConfirmedState.Lighting} desired={runtime.GetStatus().DesiredState.Lighting}");
        Assert.Equal(1, context.Device.OneShotSetCalls);
    }

    [Fact]
    public async Task LaptopReview_RecoveryPreservesOtherPendingConfirmationUntilOrdinaryObservation()
    {
        TestContext context = await CreateLaptopBehaviorTransitionContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 2;
        await runtime.SetBatteryPolicyAsync(BatteryPolicy.Create(75), default);
        Assert.Contains(DeviceSubsystem.Battery, runtime.GetStatus().UnconfirmedWrites);
        await SeedLaptopReviewPendingAsync(context);
        int writes = context.Device.TotalSetterCalls;

        await runtime.ObserveAsync(default);

        Assert.True(runtime.GetStatus().WritesBlocked);
        Assert.Contains(DeviceSubsystem.Battery, runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
        await runtime.ObserveAsync(default);
        Assert.Empty(runtime.GetStatus().UnconfirmedWrites);
        Assert.False(runtime.GetStatus().WritesBlocked);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task LaptopReview_ReadOnlyRecoveryDoesNotRetireOwnedHyperBoostProvenance()
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = await CreateOwnedHyperBoostGpuContextAsync(journal);
        using ServiceControlRuntime runtime = context.Runtime;
        object? ownedIntent = PrivateField("ownedHyperBoostRestoreIntent").GetValue(runtime);
        Assert.NotNull(ownedIntent);
        await SeedLaptopReviewPendingAsync(context);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        int writes = context.Device.TotalSetterCalls;

        await runtime.ObserveAsync(default);

        Assert.Same(ownedIntent, PrivateField("ownedHyperBoostRestoreIntent").GetValue(runtime));
        Assert.True(Assert.IsType<bool>(PrivateField("ownsHyperBoost").GetValue(runtime)));
        Assert.NotNull(journal.Journal);
        Assert.True(runtime.GetStatus().WritesBlocked);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
        await runtime.ObserveAsync(default);
        Assert.Null(journal.Journal);
        Assert.Null(PrivateField("ownedHyperBoostRestoreIntent").GetValue(runtime));
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
    }

    [Fact]
    public async Task LaptopReview_ExternalHyperBoostDuringRecoveryInvalidatesOldExactFanAuthority()
    {
        (TestContext context, FixedFanTemperatureSource temperatures, _) = await CreateOwnedManualFanCurveContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        int establishedWrites = context.Device.FanTargets.Count;
        await SeedLaptopReviewPendingAsync(context);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = true };
        await runtime.ObserveAsync(default);
        await runtime.MaintainFanCurveAsync(default);
        int reads = temperatures.ReadCount;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await runtime.ObserveAsync(default);
        await runtime.MaintainFanCurveAsync(default);
        Assert.Equal(establishedWrites, context.Device.FanTargets.Count);
        Assert.Equal(reads, temperatures.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaptopReview_DeferredUsbCSourcePausesOldCurveAndRetainedFrames(bool revertBeforeOrdinaryObservation)
    {
        (TestContext context, FixedFanTemperatureSource temperatures, _) = await CreateOwnedManualFanCurveContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.Capabilities = context.Device.Capabilities with { SupportsManualFansOnLimitedPower = true };
        await runtime.ReconcileStartupProfileAsync(default);
        await SeedLaptopReviewPendingAsync(context);
        context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);

        await runtime.ObserveAsync(default);

        Assert.Equal(PowerSource.UsbC, runtime.GetStatus().ActivePowerSource);
        Assert.Equal(PowerSource.PluggedIn, Assert.IsType<ProfileAutomationState>(PrivateField("automation").GetValue(runtime)).PowerSource);
        Assert.False(context.Provider.RecoveryWriteAdmission!());
        int reads = temperatures.ReadCount;
        int writes = context.Device.FanTargets.Count;
        await runtime.MaintainFanCurveAsync(default);
        Assert.Equal(reads, temperatures.ReadCount);
        Assert.Equal(writes, context.Device.FanTargets.Count);
        if (revertBeforeOrdinaryObservation)
        {
            context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        }
        await runtime.ObserveAsync(default);
        Assert.True(context.Provider.RecoveryWriteAdmission!());
        Assert.Equal(revertBeforeOrdinaryObservation ? PowerSource.PluggedIn : PowerSource.UsbC,
            Assert.IsType<ProfileAutomationState>(PrivateField("automation").GetValue(runtime)).PowerSource);
    }

    private async Task SeedLaptopReviewPendingAsync(TestContext context)
    {
        // Model a retained durable intent alongside another recovery owner; no
        // second hardware submission is needed to establish this invariant.
        var store = new LaptopControlPendingStore(Path.Combine(directory, "laptop-control-pending.json"));
        LaptopControlPendingLoad loaded = store.Load();
        Assert.True(await store.TrySaveAsync(loaded.Sha256, new(1, Environment.MachineName,
            LaptopIdentity, [new(FunctionKeysRequest, true)]), default));
        context.Device.FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys;
    }

    private static FieldInfo PrivateField(string name) => typeof(ServiceControlRuntime).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!;
}
