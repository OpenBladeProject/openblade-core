using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ExperimentalBladeBatteryIntegrationTests
{
    public static TheoryData<int, int> ExperimentalGetterFailures
    {
        get
        {
            var data = new TheoryData<int, int>();
            foreach (int product in new[] { 0x029F, 0x029D, 0x02C5, 0x0255, 0x02A0 })
            {
                foreach (int kind in Enumerable.Range(0, 4))
                {
                    data.Add(product, kind);
                }
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ExperimentalGetterFailures))]
    public async Task GetterExchangeFailure_IsScopedAcrossExperimentalModelsAndRecoversOnNextRefresh(
        int productId, int kind)
    {
        bool thermal = productId == 0x0255;
        var io = new Io
        {
            Raw = 0xD0,
            RejectedGetter = thermal ? ((byte)0x0D, (byte)0x82) : ((byte)0x07, (byte)0x92),
            GetterFailureKind = kind,
            // The two correlated interface selectors remain mandatory admission;
            // inject only the subsequent state getter, never those selectors.
            GetterFailureAfter = thermal ? 2 : 0,
        };
        var native = new Native { ProductId = (ushort)productId };
        io.Events = native.Events;
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io, native: native, productId: productId);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => device.ReadStateAsync(default));
        PartialDeviceStateRead partial = Assert.IsType<PartialDeviceStateRead>(DeviceStateReadIsolation.PartialRead(failure));
        if (thermal)
        {
            Assert.Null(partial.State.Performance);
            Assert.Null(partial.State.Fans);
            Assert.Null(partial.State.HyperBoostActive);
        }
        else
        {
            Assert.Null(partial.State.Battery);
            Assert.Equal(PerformancePreset.Balanced, partial.State.Performance);
            Assert.Equal(FanMode.Automatic, partial.State.Fans);
        }
        Assert.Empty(io.Setters);
        Assert.All(io.Commands, command => Assert.True(command.Id >= 0x80));
        Assert.Contains("close:501", native.Events);
        int close = native.Events.IndexOf("close:501");
        Assert.DoesNotContain(native.Events.Skip(close + 1), item => item.StartsWith("query:501:", StringComparison.Ordinal));

        io.RejectedGetter = null;
        ConfirmedDeviceState recovered = await device.ReadStateAsync(default);
        Assert.Equal(PerformancePreset.Balanced, recovered.Performance);
        Assert.Equal(FanMode.Automatic, recovered.Fans);
        if (!thermal)
        {
            Assert.Equal(80, recovered.Battery!.ChargeLimitPercent);
        }
        Assert.Empty(io.Setters);
    }

    [Fact]
    public async Task InterfaceSelectorFailure_RemainsGlobalAndDoesNotBecomePartialState()
    {
        var io = new Io { Raw = 0xD0, RejectedGetter = (0x0D, 0x82) };
        using WindowsVerifiedBladeDeviceProvider provider = CreateProvider(io);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => device.ReadStateAsync(default));

        Assert.Null(DeviceStateReadIsolation.PartialRead(failure));
        Assert.DoesNotContain(io.Commands, command => command is (0x07, 0x92));
        Assert.Empty(io.Setters);
    }
}
