using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

// Recovery, read isolation and panel caching for the RZ09-0510/0509 local
// dimming Clear. Failure kinds: 0 status 5, 1 bad checksum, 2 native IO error,
// 3 timeout.
public sealed partial class ExperimentalBladeBatteryIntegrationTests
{
    private static async Task<(WindowsVerifiedBladeDeviceProvider Provider, IBladeDevice Device)> PauseClearAsync(
        Io io, int productId)
    {
        var native = new Native { ProductId = (ushort)productId };
        WindowsVerifiedBladeDeviceProvider provider = CreateProvider(
            io, native: native, productId: productId, power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.True((await device.ReadCapabilitiesAsync(CancellationToken.None)).LocalDimming?.SupportsClear);

        // The pre-write 07/8F read passes; the readback after the 07/0F write times out.
        io.RejectedGetter = (0x07, 0x8F);
        io.GetterFailureKind = 3;
        io.GetterFailureAfter = 1;
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.ClearLocalDimmingAsync(CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal([0x00], io.PowerModeControlWrites);
        Assert.True(provider.IsExperimentalWritesPaused);
        return (provider, device);
    }

    [Theory]
    [InlineData(0x02B7, 0)]
    [InlineData(0x02B7, 4)]
    [InlineData(0x02B8, 0)]
    public async Task LocalDimming_RecoveryAfterAmbiguousClearReadsTheRealBit(int productId, byte mode)
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x02, PowerModeControl = 0x08, Mode = mode };
        (WindowsVerifiedBladeDeviceProvider provider, _) = await PauseClearAsync(io, productId);
        using (provider)
        {
            io.RejectedGetter = null;
            int before = io.Commands.Count;
            ConfirmedDeviceState? accepted = null;

            await provider.ResumeExperimentalWritesAsync(state =>
            {
                accepted = state;
                return true;
            }, () => true, CancellationToken.None);

            Assert.Contains(io.Commands.Skip(before), command => command is (0x07, 0x8F));
            Assert.False(accepted!.LocalDimmingActive);
            Assert.False(provider.IsExperimentalWritesPaused);
            Assert.Equal([0x00], io.PowerModeControlWrites);
        }
    }

    [Theory]
    [InlineData(0x02B7, 0)]
    [InlineData(0x02B7, 4)]
    [InlineData(0x02B8, 0)]
    public async Task LocalDimming_RecoveryKeepsThePauseWhenPowerModeControlIsUnreadable(int productId, byte mode)
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x02, PowerModeControl = 0x08, Mode = mode };
        (WindowsVerifiedBladeDeviceProvider provider, _) = await PauseClearAsync(io, productId);
        using (provider)
        {
            io.GetterFailureAfter = 0;
            int before = io.Commands.Count;
            bool accepted = false;

            await Assert.ThrowsAnyAsync<Exception>(() => provider.ResumeExperimentalWritesAsync(_ =>
            {
                accepted = true;
                return true;
            }, () => true, CancellationToken.None));

            Assert.Contains(io.Commands.Skip(before), command => command is (0x07, 0x8F));
            Assert.False(accepted);
            Assert.True(provider.IsExperimentalWritesPaused);
            Assert.Equal([0x00], io.PowerModeControlWrites);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LocalDimming_StateReadFailureLeavesOnlyLocalDimmingUnknown(int failureKind)
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x02, PowerModeControl = 0x08 };
        var native = new Native { ProductId = 0x02B7 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(
            io, native: native, productId: 0x02B7, power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        ConfirmedDeviceState healthy = await device.ReadStateAsync(CancellationToken.None);
        Assert.True(healthy.LocalDimmingActive);

        io.RejectedGetter = (0x07, 0x8F);
        io.GetterFailureKind = failureKind;
        int before = io.Commands.Count;
        ConfirmedDeviceState failed = await device.ReadStateAsync(CancellationToken.None);

        Assert.Equal(1, io.Commands.Skip(before).Count(command => command is (0x07, 0x8F)));
        Assert.Null(failed.LocalDimmingActive);
        Assert.Equal(healthy.Performance, failed.Performance);
        Assert.Equal(healthy.Fans, failed.Fans);
        Assert.Equal(healthy.Battery, failed.Battery);
        Assert.Equal(healthy.Lighting, failed.Lighting);

        // The failure is remembered: later state reads skip 07/8F.
        before = io.Commands.Count;
        ConfirmedDeviceState skipped = await device.ReadStateAsync(CancellationToken.None);
        Assert.DoesNotContain(io.Commands.Skip(before), command => command is (0x07, 0x8F));
        Assert.Null(skipped.LocalDimmingActive);
        Assert.Equal(healthy.Battery, skipped.Battery);

        // A Clear reads Power Mode Control afresh and lifts the skip.
        io.RejectedGetter = null;
        await device.ClearLocalDimmingAsync(CancellationToken.None);
        Assert.False((await device.ReadStateAsync(CancellationToken.None)).LocalDimmingActive);
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    [Fact]
    public async Task LocalDimming_PanelIsReadOncePerDevice()
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x02, PowerModeControl = 0x08 };
        var native = new Native { ProductId = 0x02B7 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(
            io, native: native, productId: 0x02B7, power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        await device.ReadStateAsync(CancellationToken.None);
        await device.ReadStateAsync(CancellationToken.None);
        await device.SetChargeLimitAsync(BatteryPolicy.Create(80), CancellationToken.None);

        Assert.True(native.Events.Count(item => item.StartsWith("open:", StringComparison.Ordinal)) > 1);
        Assert.Equal(1, io.Commands.Count(command => command is (0x0D, 0x8F)));
        Assert.Equal(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true),
            (await device.ReadCapabilitiesAsync(CancellationToken.None)).LocalDimming);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LocalDimming_FailedPanelReadIsRememberedAndNeverBlocksOtherControls(int failureKind)
    {
        var io = new Io
        {
            Raw = 0xD0,
            SkuHardwareConfiguration = 0x02,
            PowerModeControl = 0x08,
            RejectedGetter = (0x0D, 0x8F),
            GetterFailureKind = failureKind,
        };
        var native = new Native { ProductId = 0x02B7 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(
            io, native: native, productId: 0x02B7, power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        ConfirmedDeviceState state = await device.ReadStateAsync(CancellationToken.None);
        await device.ReadStateAsync(CancellationToken.None);
        await device.SetChargeLimitAsync(BatteryPolicy.Create(80), CancellationToken.None);

        Assert.NotNull(state.Battery);
        Assert.NotNull(state.Performance);
        Assert.Null(state.LocalDimmingActive);
        Assert.Equal(1, io.Commands.Count(command => command is (0x0D, 0x8F)));
        Assert.DoesNotContain(io.Commands, command => command is (0x07, 0x8F));
        Assert.Null((await device.ReadCapabilitiesAsync(CancellationToken.None)).LocalDimming);
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task LocalDimming_LaterPanelFailureKeepsTheOfferAndClearStillReadsThePanel(int failureKind)
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x02, PowerModeControl = 0x08 };
        var native = new Native { ProductId = 0x02B7 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(
            io, native: native, productId: 0x02B7, power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.True((await device.ReadStateAsync(CancellationToken.None)).LocalDimmingActive);

        io.RejectedGetter = (0x0D, 0x8F);
        io.GetterFailureKind = failureKind;
        Assert.True((await device.ReadStateAsync(CancellationToken.None)).LocalDimmingActive);
        Assert.True((await device.ReadCapabilitiesAsync(CancellationToken.None)).LocalDimming?.SupportsClear);

        // Clear still makes its own fresh panel read; a failed one writes nothing.
        int before = io.Commands.Count;
        BladeWriteException refused = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.ClearLocalDimmingAsync(CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, refused.SubmissionState);
        Assert.Contains(io.Commands.Skip(before), command => command is (0x0D, 0x8F));
        Assert.Empty(io.PowerModeControlWrites);
        Assert.False(provider.IsExperimentalWritesPaused);

        io.RejectedGetter = null;
        await device.ClearLocalDimmingAsync(CancellationToken.None);
        Assert.Equal([0x00], io.PowerModeControlWrites);
    }

    [Fact]
    public async Task LocalDimming_ClearAdmitsAgainAfterItsReads()
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x02, PowerModeControl = 0x08 };
        var native = new Native { ProductId = 0x02B7 };
        var identity = new Identity(ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02B7).ProductName);
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(
            io, identity: identity, native: native, productId: 0x02B7, power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.True((await device.ReadCapabilitiesAsync(CancellationToken.None)).LocalDimming?.SupportsClear);

        // The laptop identity is lost after the pre-write Power Mode Control read.
        io.AfterPowerModeControlRead = () => identity.Present = false;
        BladeWriteException refused = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, refused.SubmissionState);
        Assert.DoesNotContain(io.Commands, command => command is (0x07, 0x0F));
        Assert.Empty(io.PowerModeControlWrites);
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    [Fact]
    public async Task LocalDimming_ProviderRejectsClearWithoutAdmissionBeforeOpeningThePort()
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x00, PowerModeControl = 0x08 };
        var native = new Native { ProductId = 0x02B7 };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(
            io, native: native, productId: 0x02B7, power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.Null((await device.ReadCapabilitiesAsync(CancellationToken.None)).LocalDimming);
        int before = io.Commands.Count;
        int opened = native.Events.Count;

        await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => device.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(before, io.Commands.Count);
        Assert.Equal(opened, native.Events.Count);
        Assert.Empty(io.PowerModeControlWrites);
    }

    // Maximum Fans and local dimming both on (0x0A): the Clear guard and the
    // Maximum Fans guard refuse each other, so both must be changed in Synapse.
    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    public async Task LocalDimming_MaximumFansAndLocalDimmingRefuseEachOtherWithoutWriting(int productId)
    {
        var io = new Io { Raw = 0xD0, SkuHardwareConfiguration = 0x02, PowerModeControl = 0x0A, Mode = 4, Cpu = 1, Gpu = 1 };
        var native = new Native { ProductId = (ushort)productId };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(
            io, native: native, productId: productId, power: () => PowerSource.PluggedIn);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        ConfirmedDeviceState state = await device.ReadStateAsync(CancellationToken.None);
        Assert.True(state.LocalDimmingActive);
        Assert.True(state.CustomPerformance!.MaximumFans);

        BladeWriteException clear = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.ClearLocalDimmingAsync(CancellationToken.None));
        BladeWriteException fansOff = await Assert.ThrowsAsync<BladeWriteException>(() => device.SetPerformanceAsync(
            PerformancePreset.Custom, state.CustomPerformance with { MaximumFans = false }, CancellationToken.None));
        BladeWriteException balanced = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetPerformanceAsync(PerformancePreset.Balanced, CancellationToken.None));

        Assert.All([clear, fansOff, balanced],
            failure => Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState));
        Assert.Contains("Turn off Maximum Fans in Synapse", clear.Message, StringComparison.Ordinal);
        Assert.Empty(io.PowerModeControlWrites);
        Assert.Equal(0x0A, io.PowerModeControl);
        Assert.False(provider.IsExperimentalWritesPaused);
    }
}
