using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Shared twinkle pattern with the exact PID 02E0 physical-slot mask.</summary>
internal sealed class Pid02E0StarlightEffect : IPid02E0LightingEffect
{
    private readonly StarlightPattern pattern;

    internal Pid02E0StarlightEffect(
        StarlightColorMode colorMode,
        MatrixRgb primaryColor,
        MatrixRgb secondaryColor,
        TimeSpan twinkleDuration)
    {
        pattern = new(colorMode, primaryColor, secondaryColor, twinkleDuration);
    }

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0LightingTiming.AnimatedFrameInterval;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        MatrixRgb[] slots = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (!Pid02E0MatrixLayout.IsHole(slot))
            {
                slots[slot] = pattern.RenderSlot(slot, elapsed);
            }
        }
        return new Pid02E0MatrixFrame(slots);
    }
}
