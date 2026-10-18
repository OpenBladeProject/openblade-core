using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Shared software twinkle pattern. The caller owns slot count and layout.</summary>
internal sealed class StarlightPattern
{
    private const int MinimumPeriodUnits = 8;
    private const int PeriodUnitVariants = 5;

    private readonly StarlightColorMode colorMode;
    private readonly MatrixRgb primaryColor;
    private readonly MatrixRgb secondaryColor;
    private readonly TimeSpan twinkleDuration;

    internal StarlightPattern(
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


    internal MatrixRgb RenderSlot(int slot, TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        double normalizedTime = elapsed.TotalSeconds / twinkleDuration.TotalSeconds;
        uint slotHash = Mix((uint)slot + 1u);
        int periodUnits = MinimumPeriodUnits + (int)(slotHash % PeriodUnitVariants);
        double firstStart = Fraction(Mix(slotHash ^ 0x9E3779B9u)) * periodUnits;
        double sinceFirst = normalizedTime - firstStart;
        if (sinceFirst < 0)
        {
            return default;
        }
        long eventIndex = (long)Math.Floor(sinceFirst / periodUnits);
        double eventAge = sinceFirst - (eventIndex * periodUnits);
        if (eventAge >= 1.0)
        {
            return default;
        }
        double intensity = eventAge <= 0.5 ? eventAge * 2.0 : (1.0 - eventAge) * 2.0;
        return ColorForEvent(slotHash, eventIndex)
            .Scale(Pid02E0LightingMath.ToByte(intensity * 255.0));
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
