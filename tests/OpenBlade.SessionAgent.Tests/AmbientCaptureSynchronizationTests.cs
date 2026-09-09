using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent.Tests;

public sealed class AmbientCaptureSynchronizationTests
{
    [Fact]
    public void ReadCompletedFrame_FlushesDrawingBeforeReadingPixels()
    {
        byte[] buffer = new byte[AmbientLightingFrame.SlotCount * 4];
        nint bits = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, bits, buffer.Length);
            AmbientLightingFrame frame = WindowsAmbientScreenSampler.ReadCompletedFrame(bits, buffer, () =>
            {
                // Model a queued GDI draw completing only when the batch is flushed.
                Marshal.WriteByte(bits, 4, 30);
                Marshal.WriteByte(bits, 5, 20);
                Marshal.WriteByte(bits, 6, 10);
                return true;
            });
            Assert.Equal(new RgbColor(10, 20, 30), frame.Slots[1]);
        }
        finally
        {
            Marshal.FreeHGlobal(bits);
        }
    }

    [Fact]
    public void ReadCompletedFrame_FailedFlushDoesNotReadOrPublishPixels()
    {
        byte[] buffer = new byte[AmbientLightingFrame.SlotCount * 4];
        // An invalid source proves no pixel access occurs after the failed batch.
        Assert.Throws<InvalidOperationException>(() =>
            WindowsAmbientScreenSampler.ReadCompletedFrame(nint.Zero, buffer, () => false));
        Assert.All(buffer, value => Assert.Equal((byte)0, value));
    }
}
