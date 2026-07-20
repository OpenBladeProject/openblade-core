using OpenBlade.Contracts;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class AmbientFrameMapperTests
{
    [Fact]
    public void FromBgra32_ConvertsChannelsAndKeepsCapturedHolesBlack()
    {
        byte[] bgra = new byte[AmbientLightingFrame.SlotCount * 4];
        for (int slot = 0; slot < AmbientLightingFrame.SlotCount; slot++)
        {
            int offset = slot * 4;
            bgra[offset] = 30;
            bgra[offset + 1] = 20;
            bgra[offset + 2] = 10;
            bgra[offset + 3] = 255;
        }

        AmbientLightingFrame frame = AmbientFrameMapper.FromBgra32(bgra);

        Assert.True(frame.IsWellFormed());
        Assert.Equal(new RgbColor(10, 20, 30), frame.Slots[1]);
        Assert.Equal(default, frame.Slots[0]);
    }
}
