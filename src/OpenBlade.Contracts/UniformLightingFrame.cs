namespace OpenBlade.Contracts;

/// <summary>
/// One host-rendered color applied uniformly to every addressable keyboard slot.
/// This shape has no physical key-coordinate or layout-mask dependency.
/// </summary>
public sealed record UniformLightingFrame(RgbColor Color)
{
    public static UniformLightingFrame FromSpatialFrame(AmbientLightingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.IsWellFormed())
        {
            throw new ArgumentException("The source lighting frame is malformed.", nameof(frame));
        }

        long red = 0;
        long green = 0;
        long blue = 0;
        int count = 0;
        for (int slot = 0; slot < AmbientLightingFrame.SlotCount; slot++)
        {
            if (AmbientLightingFrame.IsHole(slot))
            {
                continue;
            }
            RgbColor color = frame.Slots[slot];
            red += color.Red;
            green += color.Green;
            blue += color.Blue;
            count++;
        }

        return new(new RgbColor(
            checked((byte)((red + count / 2) / count)),
            checked((byte)((green + count / 2) / count)),
            checked((byte)((blue + count / 2) / count))));
    }
}
