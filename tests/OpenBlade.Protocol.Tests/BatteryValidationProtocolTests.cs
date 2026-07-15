using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class BatteryValidationProtocolTests
{
    public static TheoryData<BatteryValidationSetting, byte> ClosedSettings => new()
    {
        { BatteryValidationSetting.ProtectionOff, 0x50 },
        { BatteryValidationSetting.Limit50, 0xB2 },
        { BatteryValidationSetting.Limit55, 0xB7 },
        { BatteryValidationSetting.Limit60, 0xBC },
        { BatteryValidationSetting.Limit65, 0xC1 },
        { BatteryValidationSetting.Limit70, 0xC6 },
        { BatteryValidationSetting.Limit75, 0xCB },
        { BatteryValidationSetting.Limit80, 0xD0 },
    };

    [Fact]
    public void CreateQuery_UsesExactCapturedCommand()
    {
        byte[] report = BatteryValidationProtocol.CreateQuery(0x20).Encode();

        Assert.Equal(0x20, report[1]);
        Assert.Equal(1, report[5]);
        Assert.Equal(0x07, report[6]);
        Assert.Equal(0x92, report[7]);
        Assert.Equal(0x00, report[8]);
    }

    [Theory]
    [MemberData(nameof(ClosedSettings))]
    public void CreateSet_UsesExactClosedPayload(
        BatteryValidationSetting setting,
        byte expectedPayload)
    {
        byte[] report = BatteryValidationProtocol.CreateSet(0x21, setting).Encode();

        Assert.Equal(0x21, report[1]);
        Assert.Equal(1, report[5]);
        Assert.Equal(0x07, report[6]);
        Assert.Equal(0x12, report[7]);
        Assert.Equal(expectedPayload, report[8]);
    }

    [Theory]
    [MemberData(nameof(ClosedSettings))]
    public void TryDecodeQuery_RoundTripsEveryClosedValue(
        BatteryValidationSetting expected,
        byte payload)
    {
        bool decoded = BatteryValidationProtocol.TryDecodeQuery([payload], out BatteryValidationSetting actual);

        Assert.True(decoded);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CreateSet_RejectsUndefinedSetting()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BatteryValidationProtocol.CreateSet(0x21, (BatteryValidationSetting)999));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xB2, 0x00 })]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xB3 })]
    [InlineData(new byte[] { 0xFF })]
    public void TryDecodeQuery_RejectsInvalidResponse(byte[] payload)
    {
        bool decoded = BatteryValidationProtocol.TryDecodeQuery(payload, out BatteryValidationSetting setting);

        Assert.False(decoded);
        Assert.Equal(default, setting);
    }
}
