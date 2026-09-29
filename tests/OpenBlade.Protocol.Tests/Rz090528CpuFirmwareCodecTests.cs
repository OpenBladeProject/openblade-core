using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuFirmwareCodecTests
{
    [Fact]
    public void SavedSlotsRetainPhysicalIdsBeyondTheActiveCoreCount()
    {
        byte[] bytes = new byte[800];
        bytes[0xC0] = 2;
        bytes[0xC4 + 15] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x104 + 15 * 2), 7);
        var baseline = Rz090528CpuFirmwareCodec.DecodeSaved(bytes);
        Assert.Equal(-7, baseline.GetCoreCurveOptimizer(15));
        Assert.Throws<ArgumentOutOfRangeException>(() => baseline.GetCoreCurveOptimizer(16));
    }

    [Fact]
    public void PowerLimitUsesFullLittleEndianMilliwattsArgument()
    {
        Assert.Equal(new byte[] { 1, 0, 5, 0, 0x18, 0x73, 1, 0 }, Rz090528CpuFirmwareCodec.EncodePowerLimit(95));
    }

    [Fact]
    public void DisabledCurveOptimizerUsesSupportedAllCoreModeAndZeroMagnitude()
    {
        Assert.Equal(new byte[] { 10, 0, 5, 0, 0, 0, 0xFF, 0x11 }, Rz090528CpuFirmwareCodec.EncodeCurveOptimizer(false, -30));
        Assert.Equal(new byte[] { 10, 0, 5, 0, 30, 0, 0xFF, 0x11 }, Rz090528CpuFirmwareCodec.EncodeCurveOptimizer(true, -30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(44)]
    [InlineData(96)]
    public void PowerLimitRejectsValuesOutsideEditorRange(int watts) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Rz090528CpuFirmwareCodec.EncodePowerLimit(watts));

    [Theory]
    [InlineData(0)]
    [InlineData(-31)]
    [InlineData(1)]
    public void DisabledDoesNotMakeInvalidRememberedValueValid(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Rz090528CpuFirmwareCodec.EncodeCurveOptimizer(false, value));

    [Fact]
    public void BaselinePreservesEveryByteAndDoesNotAliasInputOrOutput()
    {
        byte[] bytes = Enumerable.Range(0, 800).Select(value => (byte)value).ToArray();
        byte[] original = (byte[])bytes.Clone();
        var baseline = Rz090528CpuFirmwareCodec.DecodeSaved(bytes);
        Array.Clear(bytes);
        byte[] exported = baseline.CopyBytes();
        Assert.Equal(original, exported);
        Array.Clear(exported);
        Assert.Equal(original, baseline.CopyBytes());
    }

    [Fact]
    public void DecoderKeepsHeterogeneousAndOutsideEditorRangeBaseline()
    {
        byte[] bytes = new byte[800];
        bytes[0xC0] = 2;
        bytes[0xC4] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x104), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x106), 8);
        var baseline = Rz090528CpuFirmwareCodec.DecodeSaved(bytes);
        Assert.Null(baseline.AllCoreCurveOptimizer);
        Assert.Equal(-45, baseline.GetCoreCurveOptimizer(0));
        Assert.Equal(8, baseline.GetCoreCurveOptimizer(1));
    }

    [Fact]
    public void UnsupportedModeOrSignRemainsUnknownAndLosslesslyRestorable()
    {
        byte[] bytes = new byte[800];
        var baseline = Rz090528CpuFirmwareCodec.DecodeSaved(bytes);
        Assert.Null(baseline.GetCoreCurveOptimizer(0));
        bytes[0xC0] = 1;
        bytes[0xC1] = 2;
        baseline = Rz090528CpuFirmwareCodec.DecodeSaved(bytes);
        Assert.Null(baseline.AllCoreCurveOptimizer);
        Assert.Equal(bytes, baseline.CopyBytes());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(799)]
    [InlineData(801)]
    public void DecoderRejectsUnobservedLayouts(int length) =>
        Assert.Throws<ArgumentException>(() => Rz090528CpuFirmwareCodec.DecodeSaved(new byte[length]));
}
