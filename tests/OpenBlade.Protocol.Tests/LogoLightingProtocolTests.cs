using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class LogoLightingProtocolTests
{
    [Theory]
    [InlineData("000400", "000400", LogoLightingMode.Off)]
    [InlineData("000401", "000400", LogoLightingMode.Static)]
    [InlineData("000401", "000402", LogoLightingMode.Breathing)]
    public void ExactSelectorZeroDecodesCapturedModes(
        string stateHex,
        string effectHex,
        LogoLightingMode expected)
    {
        Assert.Equal("000400", Convert.ToHexString(
            LogoLightingProtocol.QueryArguments(0x00)));
        Assert.Equal(
            effectHex,
            Convert.ToHexString(
                LogoLightingProtocol.SetEffectArguments(expected, 0x00)));
        Assert.Equal(
            stateHex,
            Convert.ToHexString(
                LogoLightingProtocol.SetStateArguments(expected, 0x00)));
        Assert.True(LogoLightingProtocol.TryDecodeMode(
            Convert.FromHexString(stateHex),
            Convert.FromHexString(effectHex),
            0x00,
            out LogoLightingMode actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(LogoLightingMode.Static, "010400", "010401")]
    [InlineData(LogoLightingMode.Breathing, "010402", "010401")]
    [InlineData(LogoLightingMode.Off, "010400", "010400")]
    public void SetterArgumentsMatchCapturedAndReferenceShapes(
        LogoLightingMode mode,
        string expectedEffect,
        string expectedState)
    {
        Assert.Equal(expectedEffect, Convert.ToHexString(
            LogoLightingProtocol.SetEffectArguments(mode)));
        Assert.Equal(expectedState, Convert.ToHexString(
            LogoLightingProtocol.SetStateArguments(mode)));
    }

    [Theory]
    [InlineData("010400", "010400", LogoLightingMode.Off)]
    [InlineData("010401", "010400", LogoLightingMode.Static)]
    [InlineData("010401", "010402", LogoLightingMode.Breathing)]
    public void QueryPairDecodesOnlyKnownModes(
        string stateHex,
        string effectHex,
        LogoLightingMode expected)
    {
        Assert.True(LogoLightingProtocol.TryDecodeMode(
            Convert.FromHexString(stateHex),
            Convert.FromHexString(effectHex),
            out LogoLightingMode actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("010401", "010403")]
    [InlineData("010402", "010400")]
    [InlineData("000401", "010400")]
    public void QueryPairRejectsUnknownOrMalformedValues(string stateHex, string effectHex)
    {
        Assert.False(LogoLightingProtocol.TryDecodeMode(
            Convert.FromHexString(stateHex),
            Convert.FromHexString(effectHex),
            out _));
    }
}
