using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Host-rendered Starlight effect reconstructed from the PID 02E0 Synapse
/// capture. Each physical slot receives an independent, repeatable twinkle;
/// the chosen duration controls the complete linear fade-in/fade-out envelope.
/// </summary>
internal sealed class Pid02E0StarlightEffect : IPid02E0LightingEffect
{
    private const int MinimumPeriodUnits = 8;
    private const int PeriodUnitVariants = 5;

    private readonly StarlightColorMode colorMode;
    private readonly MatrixRgb primaryColor;
    private readonly MatrixRgb secondaryColor;
    private readonly TimeSpan twinkleDuration;

    internal Pid02E0StarlightEffect(
        StarlightColorMode colorMode,
        MatrixRgb primaryColor,
        MatrixRgb secondaryColor,
        TimeSpan twinkleDuration)
    {
        if (!Enum.IsDefined(colorMode))
        {
            throw new ArgumentOutOfRangeException(nameof(colorMode));
        }

        Pid02E0LightingMath.ThrowIfNotPositive(twinkleDuration, nameof(twinkleDuration));
        this.colorMode = colorMode;
        this.primaryColor = primaryColor;
        this.secondaryColor = secondaryColor;
        this.twinkleDuration = twinkleDuration;
    }

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0LightingTiming.AnimatedFrameInterval;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        double normalizedTime = elapsed.TotalSeconds / twinkleDuration.TotalSeconds;
        MatrixRgb[] slots = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];

        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (Pid02E0MatrixLayout.IsHole(slot))
            {
                continue;
            }

            uint slotHash = Mix((uint)slot + 1u);
            int periodUnits = MinimumPeriodUnits
                + (int)(slotHash % PeriodUnitVariants);
            double firstStart = Fraction(Mix(slotHash ^ 0x9E3779B9u)) * periodUnits;
            double sinceFirst = normalizedTime - firstStart;
            if (sinceFirst < 0)
            {
                continue;
            }

            long eventIndex = (long)Math.Floor(sinceFirst / periodUnits);
            double eventAge = sinceFirst - (eventIndex * periodUnits);
            if (eventAge >= 1.0)
            {
                continue;
            }

            // Captured Starlight events rise and fall symmetrically, peaking
            // near the middle of their selected Short/Medium/Long duration.
            double intensity = eventAge <= 0.5
                ? eventAge * 2.0
                : (1.0 - eventAge) * 2.0;
            MatrixRgb color = ColorForEvent(slotHash, eventIndex);
            slots[slot] = color.Scale(Pid02E0LightingMath.ToByte(intensity * 255.0));
        }

        return new Pid02E0MatrixFrame(slots);
    }

    private MatrixRgb ColorForEvent(uint slotHash, long eventIndex)
    {
        uint eventHash = Mix(slotHash ^ unchecked((uint)eventIndex * 0x85EBCA6Bu));
        return colorMode switch
        {
            StarlightColorMode.Single => primaryColor,
            StarlightColorMode.Dual => (eventHash & 1u) == 0u
                ? primaryColor
                : secondaryColor,
            StarlightColorMode.Random =>
                Pid02E0RandomPaletteColorSource.CapturedPalette[
                    (int)(eventHash
                        % (uint)Pid02E0RandomPaletteColorSource.CapturedPalette.Count)],
            _ => throw new InvalidOperationException(
                $"Unsupported Starlight color mode {colorMode}."),
        };
    }

    private static double Fraction(uint value) => value / ((double)uint.MaxValue + 1.0);

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        return value ^ (value >> 16);
    }
}
