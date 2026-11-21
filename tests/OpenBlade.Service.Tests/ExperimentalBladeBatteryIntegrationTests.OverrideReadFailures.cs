using OpenBlade.Contracts;
using OpenBlade.Protocol;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

// 07/8F has not been observed on every model that admits it. A failed or
// undecodable override read must leave the limiter, its confirmation and
// recovery exactly as they were before the override was admitted.
public sealed partial class ExperimentalBladeBatteryIntegrationTests
{
    private const int UndecodableMask = -1;

    // Production reports Windows AC presence as ExternalUnknown; it admits the enable.
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task TemporaryFullCharge_WindowsExternalPresenceReachesWrite(int product)
    {
        var io = new Io { Raw = 0xD0 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = (ushort)product }, productId: product,
            power: () => WindowsPowerStateProvider.MapPowerSource(1));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome enabled = await coordinator.ApplyTemporaryFullChargeOutcomeAsync(true, CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, enabled.Result.Status);
        Assert.True(enabled.Readback!.Battery!.TemporaryFullCharge);
        Assert.Equal(new byte[] { 4 }, io.MaskWrites);
        Assert.Empty(io.Setters);
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    [Theory]
    [InlineData(0x02A0, 0)]
    [InlineData(0x02A0, 3)]
    [InlineData(0x02A0, UndecodableMask)]
    [InlineData(0x02C5, 0)]
    [InlineData(0x02C5, 3)]
    [InlineData(0x02C5, UndecodableMask)]
    [InlineData(0x02C7, 0)]
    [InlineData(0x02C7, 3)]
    [InlineData(0x02C7, UndecodableMask)]
    public async Task OverrideReadFailure_KeepsLimiterAndBlocksOnlyOverride(int product, int failure)
    {
        Io io = FailingOverrideIo(failure);
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = (ushort)product }, productId: product);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        ConfirmedDeviceState state = await device.ReadStateAsync(CancellationToken.None);
        Assert.True(state.Battery!.ProtectionEnabled);
        Assert.Equal(80, state.Battery.ChargeLimitPercent);
        Assert.Null(state.Battery.TemporaryFullCharge);
        Assert.Equal(PerformancePreset.Balanced, state.Performance);

        var coordinator = new ControlCoordinator(device);
        ScopedApplyOutcome changed = await coordinator.ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75), CancellationToken.None);
        Assert.Equal(ControlResultStatus.Applied, changed.Result.Status);
        Assert.Equal(BatteryConfirmationScope.PolicyOnly, changed.BatteryConfirmation);
        Assert.Equal(75, changed.Readback!.Battery!.ChargeLimitPercent);
        Assert.Null(changed.Readback.Battery.TemporaryFullCharge);
        Assert.Equal(new byte[] { 0xCB }, io.Setters);
        Assert.False(provider.IsExperimentalWritesPaused);

        foreach (bool enable in new[] { true, false })
        {
            ScopedApplyOutcome blocked = await coordinator.ApplyTemporaryFullChargeOutcomeAsync(
                enable, CancellationToken.None);
            Assert.Equal(ControlResultStatus.Blocked, blocked.Result.Status);
            Assert.False(blocked.WriteAttempted);
        }
        Assert.DoesNotContain(io.Commands, command => command is (0x07, 0x0F));
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    // The override read after the limiter write is optional confirmation only.
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task OverrideReadFailureAfterLimiterWrite_StillConfirmsPolicy(int product)
    {
        // The state read and the port's own pre-write read succeed; later reads fail.
        var io = new Io { Raw = 0xD0, RejectedGetter = (0x07, 0x8F), GetterFailureAfter = 2 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = (ushort)product }, productId: product);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        ScopedApplyOutcome changed = await new ControlCoordinator(device).ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75), CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, changed.Result.Status);
        Assert.Equal(75, changed.Readback!.Battery!.ChargeLimitPercent);
        Assert.Null(changed.Readback.Battery.TemporaryFullCharge);
        Assert.Equal(new byte[] { 0xCB }, io.Setters);
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    // The service plan sees Off from its state read; the port's own fresh read
    // then reports an active override and refuses before the limiter write.
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task PortOverrideRead_RefusesLimiterWriteAfterStaleOffState(int product)
    {
        var io = new Io { Raw = 0xD0, MaskReply = read => read == 1 ? (byte)0 : (byte)4 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io,
            native: new Native { ProductId = (ushort)product }, productId: product);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        ScopedApplyOutcome changed = await new ControlCoordinator(device).ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75), CancellationToken.None);

        Assert.NotEqual(ControlResultStatus.Applied, changed.Result.Status);
        Assert.Empty(io.Setters);
        Assert.DoesNotContain(io.Commands, command => command is (0x07, 0x12));
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    // A limiter pause recovers without depending on 07/8F; the flag stays unknown.
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(UndecodableMask)]
    public async Task OverrideReadFailure_DoesNotBlockLimiterPauseRecovery(int failure)
    {
        Io io = FailingOverrideIo(failure);
        io.Raw = 0x50;
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        // The service reads state before any write; that first failed read is what
        // keeps the port from repeating the override query before the limiter write.
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice initial));
        Assert.Null((await initial.ReadStateAsync(CancellationToken.None)).Battery!.TemporaryFullCharge);
        IBladeDevice device = await PauseAsync(provider, io);
        int setterCount = io.Setters.Count;

        ConfirmedDeviceState observed = await provider.ResumeExperimentalWritesAsync(state =>
        {
            Assert.Equal(80, state.Battery!.ChargeLimitPercent);
            Assert.Null(state.Battery.TemporaryFullCharge);
            return true;
        }, () => true, CancellationToken.None);

        Assert.False(provider.IsExperimentalWritesPaused);
        Assert.Null(observed.Battery!.TemporaryFullCharge);
        Assert.Equal(setterCount, io.Setters.Count);
        await device.SetChargeLimitAsync(BatteryPolicy.Create(90), CancellationToken.None);
        Assert.Equal(setterCount + 1, io.Setters.Count);
    }

    private static Io FailingOverrideIo(int failure) => failure == UndecodableMask
        ? new Io { Raw = 0xD0, MaskReply = _ => 0x10 }
        : new Io { Raw = 0xD0, RejectedGetter = (0x07, 0x8F), GetterFailureKind = failure };
}
