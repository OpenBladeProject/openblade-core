namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadFanObservedRawProtocolTests
{
    [Fact]
    public void Query_IsExactReadOnlyPid0F43Contract()
    {
        RazerCommandSpec command = CoolingPadFanObservedRawProtocol.CreateQuery();

        Assert.Equal(0x0D, command.ClassId);
        Assert.Equal(0x81, command.CommandId);
        Assert.Equal([0x01, 0x05, 0x00], command.Arguments.ToArray());
        Assert.Equal(3, command.ExpectedPayloadLength);
        Assert.DoesNotContain(command.CommandId, new byte[] { 0x01, 0x10, 0x88, 0x90 });
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x2C)]
    [InlineData(0xFF)]
    public void Decode_PreservesThirdByteWithoutSemanticConversion(byte rawValue)
    {
        bool decoded = CoolingPadFanObservedRawProtocol.TryDecodeResponse(
            [0x01, 0x05, rawValue],
            out CoolingPadFanObservedRawByte observation);

        Assert.True(decoded);
        Assert.Equal(rawValue, observation.RawByte);
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x05 })]
    [InlineData(new byte[] { 0x01, 0x05, 0x2C, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x05, 0x2C })]
    [InlineData(new byte[] { 0x01, 0x04, 0x2C })]
    public void Decode_RejectsAnythingOutsideExactResponseShape(byte[] payload)
    {
        Assert.False(CoolingPadFanObservedRawProtocol.TryDecodeResponse(
            payload,
            out _));
    }

    [Fact]
    public void ApiNaming_ExposesOnlyAnObservedRawByte()
    {
        Type type = typeof(CoolingPadFanObservedRawByte);
        string[] propertyNames = type
            .GetProperties(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains("ObservedRawByte", type.Name, StringComparison.Ordinal);
        Assert.Equal(["RawByte"], propertyNames);
        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Rpm", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Speed", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Target", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Mode", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ownership", StringComparison.OrdinalIgnoreCase));
    }
}
