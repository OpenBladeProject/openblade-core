using OpenBlade.Contracts;
using OpenBlade.Protocol;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

// Synapse clears bit 04 whenever it turns the limiter off, so protection off
// with the override set is a state OpenBlade must neither create nor fail on.
public sealed partial class ExperimentalBladeBatteryIntegrationTests
{
    // The override was decoded as active, then 07/8F stopped answering. The
    // limiter stays untouched until the override reads Off again.
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task LastSeenActiveOverride_BlocksLimiterWritesWhileUnreadable(int product)
    {
        var io = new Io { Raw = 0xD0, MaskReply = _ => 0x04, RejectedGetter = (0x07, 0x8F), GetterFailureAfter = 1 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = (ushort)product }, productId: product);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.True((await device.ReadStateAsync(CancellationToken.None)).Battery!.TemporaryFullCharge);
        ConfirmedDeviceState unknown = await device.ReadStateAsync(CancellationToken.None);
        Assert.True(unknown.Battery!.ProtectionEnabled);
        Assert.Null(unknown.Battery.TemporaryFullCharge);

        var coordinator = new ControlCoordinator(device);
        ScopedApplyOutcome off = await coordinator.ApplyBatteryProtectionOutcomeAsync(
            false, BatteryPolicy.Create(80), CancellationToken.None);
        ScopedApplyOutcome limit = await coordinator.ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75), CancellationToken.None);

        foreach (ScopedApplyOutcome refused in new[] { off, limit })
        {
            Assert.NotEqual(ControlResultStatus.Applied, refused.Result.Status);
            Assert.False(refused.WriteAttempted);
            Assert.Equal(WriteSubmissionState.KnownNotApplied, refused.WriteSubmissionState);
        }
        Assert.Empty(io.Setters);
        Assert.DoesNotContain(io.Commands, command => command is (0x07, 0x12));
        Assert.False(provider.IsExperimentalWritesPaused);

        io.RejectedGetter = null;
        io.MaskReply = _ => 0x00;
        Assert.False((await device.ReadStateAsync(CancellationToken.None)).Battery!.TemporaryFullCharge);
        ScopedApplyOutcome changed = await coordinator.ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75), CancellationToken.None);
        Assert.Equal(ControlResultStatus.Applied, changed.Result.Status);
        Assert.Equal(new byte[] { 0xCB }, io.Setters);
    }

    // Firmware that has never answered 07/8F keeps the limiter-only write it
    // had before the override was admitted. If the override later reads as set
    // under protection off, the read still succeeds: the override is unknown
    // and every other subsystem stays usable.
    [Theory]
    [InlineData(0x02A0, 0)]
    [InlineData(0x02A0, 3)]
    [InlineData(0x02C5, 0)]
    [InlineData(0x02C5, 3)]
    [InlineData(0x02C7, 0)]
    [InlineData(0x02C7, 3)]
    public async Task NeverReadOverride_KeepsLimiterWriteAndLaterConflictStaysReadable(int product, int failure)
    {
        var io = new Io { Raw = 0xD0, RejectedGetter = (0x07, 0x8F), GetterFailureKind = failure, MaskReply = _ => 0x04 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = (ushort)product }, productId: product);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.Null((await device.ReadStateAsync(CancellationToken.None)).Battery!.TemporaryFullCharge);
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome off = await coordinator.ApplyBatteryProtectionOutcomeAsync(
            false, BatteryPolicy.Create(80), CancellationToken.None);
        Assert.Equal(ControlResultStatus.Applied, off.Result.Status);
        Assert.Equal(new byte[] { 0x50 }, io.Setters);

        io.RejectedGetter = null;
        ConfirmedDeviceState later = await device.ReadStateAsync(CancellationToken.None);
        Assert.False(later.Battery!.ProtectionEnabled);
        Assert.Null(later.Battery.TemporaryFullCharge);
        Assert.Equal(PerformancePreset.Balanced, later.Performance);
        Assert.Equal(ControlResultStatus.Applied,
            (await coordinator.ApplyPerformanceAsync(PerformancePreset.Silent, CancellationToken.None)).Status);

        // The override now reads as set, so the limiter stays off until it is cancelled.
        ControlResult on = await coordinator.ApplyBatteryProtectionAsync(true, BatteryPolicy.Create(80), CancellationToken.None);
        Assert.NotEqual(ControlResultStatus.Applied, on.Status);
        Assert.Equal(new byte[] { 0x50 }, io.Setters);
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    // A laptop can already be in that state before OpenBlade writes anything.
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task ExistingProtectionOffWithOverrideBit_ReadsOverrideAsUnknown(int product)
    {
        var io = new Io { Raw = 0x50, MaskReply = _ => 0x04 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = (ushort)product }, productId: product);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        ConfirmedDeviceState state = await device.ReadStateAsync(CancellationToken.None);

        Assert.False(state.Battery!.ProtectionEnabled);
        Assert.Null(state.Battery.TemporaryFullCharge);
        Assert.Equal(PerformancePreset.Balanced, state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Empty(io.Setters);
    }

    // A profile transition confirms the limiter on these backends while the
    // override is unreadable, exactly as its plan admitted it.
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task FullTransition_UnreadableOverrideConfirmsIndependentLimiter(int product)
    {
        var io = new Io { Raw = 0xD0, MaskReply = _ => 0x10 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = (ushort)product }, productId: product);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        ConfirmedDeviceState state = await device.ReadStateAsync(CancellationToken.None);
        Assert.Null(state.Battery!.TemporaryFullCharge);
        var desired = new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(state.Performance!.Value, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(state.Fans!.Value, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(state.Lighting ?? LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
                SettingValueSource.PowerProfile),
            new DesiredBatteryState(BatteryPolicy.Create(75), true, false));

        FullTransitionApplyOutcome outcome = await new ControlCoordinator(device)
            .ApplyDesiredStateOutcomeAsync(desired, CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Battery.Status);
        Assert.Contains(DeviceSubsystem.Battery, outcome.ReadbackMatched);
        Assert.DoesNotContain(DeviceSubsystem.Battery, outcome.ConfirmationUnavailable ?? new HashSet<DeviceSubsystem>());
        Assert.Equal(75, outcome.Readback!.Battery!.ChargeLimitPercent);
        Assert.Null(outcome.Readback.Battery.TemporaryFullCharge);
        Assert.Equal(new byte[] { 0xCB }, io.Setters);
        Assert.False(provider.IsExperimentalWritesPaused);
    }
}
