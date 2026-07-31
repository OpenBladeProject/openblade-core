using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528MatrixLightingTests
{
    private static readonly int[] ExpectedHoleSlots =
        [0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93];

    [Fact]
    public void UniformFrame_UsesOnlyTheIndependentlyCapturedPid02C6HoleMask()
    {
        var color = new RgbColor(0x47, 0xE1, 0x0C);

        Rz090528MatrixFrame frame = Rz090528MatrixFrame.Uniform(color);

        Assert.Equal(ExpectedHoleSlots, Rz090528MatrixFrame.CapturedHoleSlots);
        for (int slot = 0; slot < Rz090528MatrixFrame.SlotCount; slot++)
        {
            bool hole = ExpectedHoleSlots.Contains(slot);
            Assert.Equal(hole, Rz090528MatrixFrame.IsHole(slot));
            Assert.Equal(hole ? default : color, frame[slot]);
        }
    }

    [Fact]
    public void RowCodec_EmitsCapturedFfRowZeroToSixteenHeaderAndRgbOrder()
    {
        var color = new RgbColor(0x47, 0xE1, 0x0C);
        Rz090528MatrixFrame frame = Rz090528MatrixFrame.Uniform(color);

        for (int row = 0; row < Rz090528MatrixFrame.RowCount; row++)
        {
            byte[] payload = Rz090528MatrixRowProtocol.EncodeRow(row, frame);

            Assert.Equal(Rz090528MatrixRowProtocol.RowPayloadLength, payload.Length);
            Assert.Equal(0xFF, payload[0]);
            Assert.Equal(row, payload[1]);
            Assert.Equal(0x00, payload[2]);
            Assert.Equal(0x10, payload[3]);
            for (int column = 0; column < Rz090528MatrixFrame.ColumnCount; column++)
            {
                int slot = (row * Rz090528MatrixFrame.ColumnCount) + column;
                int offset = 4 + (column * 3);
                RgbColor expected = Rz090528MatrixFrame.IsHole(slot)
                    ? default
                    : color;
                Assert.Equal(expected.Red, payload[offset]);
                Assert.Equal(expected.Green, payload[offset + 1]);
                Assert.Equal(expected.Blue, payload[offset + 2]);
            }
        }
    }

    [Fact]
    public void Spectrum_TraversesRgbWheelAcrossExactMeasuredCycle()
    {
        var effect = new Rz090528SpectrumEffect();

        Assert.Equal(
            new RgbColor(0xFF, 0x00, 0x00),
            effect.RenderFrame(TimeSpan.Zero)[1]);
        Assert.Equal(
            new RgbColor(0x80, 0xFF, 0x00),
            effect.RenderFrame(Rz090528SpectrumEffect.CycleDuration / 4)[1]);
        Assert.Equal(
            new RgbColor(0x00, 0xFF, 0xFF),
            effect.RenderFrame(Rz090528SpectrumEffect.CycleDuration / 2)[1]);
        Assert.Equal(
            new RgbColor(0x80, 0x00, 0xFF),
            effect.RenderFrame(Rz090528SpectrumEffect.CycleDuration * 3 / 4)[1]);
        Assert.Equal(
            new RgbColor(0xFF, 0x00, 0x00),
            effect.RenderFrame(Rz090528SpectrumEffect.CycleDuration)[1]);
    }
}
