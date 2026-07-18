namespace OpenBlade.Protocol;

/// <summary>
/// Pure host-rendered matrix effect. The caller owns wall-clock scheduling and
/// passes elapsed time explicitly, which keeps rendering deterministic and
/// independent from HID I/O.
/// </summary>
internal interface IPid02E0LightingEffect
{
    TimeSpan? GetNextFrameDelay(TimeSpan elapsed);

    Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed);
}

internal static class Pid02E0LightingTiming
{
    internal static readonly TimeSpan AnimatedFrameInterval = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan IdleKeepaliveInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan BreathingPlateauStreamDuration = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan SpectrumCycle = TimeSpan.FromSeconds(40);
    internal static readonly TimeSpan BreathingCycle = TimeSpan.FromSeconds(7);
}

internal sealed class Pid02E0StaticEffect(MatrixRgb color) : IPid02E0LightingEffect
{
    internal MatrixRgb Color { get; } = color;

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0LightingTiming.IdleKeepaliveInterval;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0MatrixRenderers.RenderStatic(Color);
    }
}

internal sealed class Pid02E0SpectrumEffect : IPid02E0LightingEffect
{
    private readonly TimeSpan cycleDuration;

    internal Pid02E0SpectrumEffect(TimeSpan? cycleDuration = null)
    {
        this.cycleDuration = cycleDuration ?? Pid02E0LightingTiming.SpectrumCycle;
        Pid02E0LightingMath.ThrowIfNotPositive(this.cycleDuration, nameof(cycleDuration));
    }

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0LightingTiming.AnimatedFrameInterval;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        double phase = Pid02E0LightingMath.CyclePhase(elapsed, cycleDuration);
        return Pid02E0MatrixRenderers.RenderSpectrumSample(
            Pid02E0LightingMath.HueToRgb(phase));
    }
}

internal enum Pid02E0BreathingColorMode
{
    Single,
    Dual,
    Random,
}

/// <summary>
/// Supplies one stable color for a breathing cycle. Addressing colors by cycle
/// instead of consuming a mutable random stream makes rendering repeatable
/// even when frames are skipped or requested again.
/// </summary>
internal interface IPid02E0RandomColorSource
{
    MatrixRgb ColorForCycle(long cycleIndex);
}

/// <summary>
/// Deterministic full-brightness hue source. A production caller can seed one
/// instance randomly; tests can use a fixed seed or a small fake.
/// </summary>
internal sealed class Pid02E0SeededRandomColorSource(ulong seed) : IPid02E0RandomColorSource
{
    public MatrixRgb ColorForCycle(long cycleIndex)
    {
        if (cycleIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cycleIndex));
        }

        ulong value = unchecked(seed + ((ulong)cycleIndex * 0x9E3779B97F4A7C15UL));
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        value ^= value >> 31;

        double hue = (value >> 11) * (1.0 / (1UL << 53));
        return Pid02E0LightingMath.HueToRgb(hue);
    }
}

internal sealed class Pid02E0BreathingEffect : IPid02E0LightingEffect
{
    private readonly Pid02E0BreathingColorMode mode;
    private readonly MatrixRgb primaryColor;
    private readonly MatrixRgb secondaryColor;
    private readonly IPid02E0RandomColorSource? randomColorSource;
    private readonly TimeSpan cycleDuration;

    private Pid02E0BreathingEffect(
        Pid02E0BreathingColorMode mode,
        MatrixRgb primaryColor,
        MatrixRgb secondaryColor,
        IPid02E0RandomColorSource? randomColorSource,
        TimeSpan? cycleDuration)
    {
        this.mode = mode;
        this.primaryColor = primaryColor;
        this.secondaryColor = secondaryColor;
        this.randomColorSource = randomColorSource;
        this.cycleDuration = cycleDuration ?? Pid02E0LightingTiming.BreathingCycle;
        Pid02E0LightingMath.ThrowIfNotPositive(this.cycleDuration, nameof(cycleDuration));
    }

    internal static Pid02E0BreathingEffect Single(
        MatrixRgb color,
        TimeSpan? cycleDuration = null) =>
        new(Pid02E0BreathingColorMode.Single, color, default, null, cycleDuration);

    internal static Pid02E0BreathingEffect Dual(
        MatrixRgb firstColor,
        MatrixRgb secondColor,
        TimeSpan? cycleDuration = null) =>
        new(Pid02E0BreathingColorMode.Dual, firstColor, secondColor, null, cycleDuration);

    internal static Pid02E0BreathingEffect Random(
        IPid02E0RandomColorSource colorSource,
        TimeSpan? cycleDuration = null) =>
        new(
            Pid02E0BreathingColorMode.Random,
            default,
            default,
            colorSource ?? throw new ArgumentNullException(nameof(colorSource)),
            cycleDuration);

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0LightingMath.BreathingFrameDelay(elapsed, cycleDuration);
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);

        long cycleIndex = Pid02E0LightingMath.CycleIndex(elapsed, cycleDuration);
        double phase = Pid02E0LightingMath.CyclePhase(elapsed, cycleDuration);
        byte intensity = Pid02E0LightingMath.BreathingIntensity(phase);

        MatrixRgb color = mode switch
        {
            Pid02E0BreathingColorMode.Single => primaryColor,
            Pid02E0BreathingColorMode.Dual when (cycleIndex & 1) == 0 => primaryColor,
            Pid02E0BreathingColorMode.Dual => secondaryColor,
            Pid02E0BreathingColorMode.Random => randomColorSource!.ColorForCycle(cycleIndex),
            _ => throw new InvalidOperationException($"Unsupported breathing color mode {mode}."),
        };

        return Pid02E0MatrixRenderers.RenderBreathingSample(color, intensity);
    }
}

/// <summary>
/// Stateful per-slot Reactive renderer. Pulse timestamps are supplied by the
/// caller, so this type observes no keyboard input and records no key data.
/// </summary>
internal sealed class Pid02E0ReactiveEffect : IPid02E0LightingEffect
{
    private readonly MatrixRgb color;
    private readonly TimeSpan fadeDuration;
    private readonly TimeSpan?[] lastPulseAt = new TimeSpan?[Pid02E0MatrixFrame.SlotCount];

    internal Pid02E0ReactiveEffect(MatrixRgb color, TimeSpan fadeDuration)
    {
        Pid02E0LightingMath.ThrowIfNotPositive(fadeDuration, nameof(fadeDuration));
        this.color = color;
        this.fadeDuration = fadeDuration;
    }

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return HasActivePulse(elapsed)
            ? Pid02E0LightingTiming.AnimatedFrameInterval
            : Pid02E0LightingTiming.IdleKeepaliveInterval;
    }

    internal void Pulse(int slot, TimeSpan at)
    {
        Pid02E0LightingMath.ThrowIfNegative(at);
        if ((uint)slot >= Pid02E0MatrixFrame.SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        if (Pid02E0MatrixLayout.IsHole(slot))
        {
            throw new ArgumentException(
                $"Captured layout-hole slot {slot} cannot receive a Reactive pulse.",
                nameof(slot));
        }

        lastPulseAt[slot] = at;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        MatrixRgb[] slots = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];

        for (int slot = 0; slot < slots.Length; slot++)
        {
            TimeSpan? pulseAt = lastPulseAt[slot];
            if (pulseAt is null || pulseAt.Value > elapsed)
            {
                continue;
            }

            TimeSpan age = elapsed - pulseAt.Value;
            if (age >= fadeDuration)
            {
                continue;
            }

            double remaining = 1.0 - (age.TotalSeconds / fadeDuration.TotalSeconds);
            slots[slot] = color.Scale(Pid02E0LightingMath.ToByte(remaining * 255));
        }

        return new Pid02E0ReactiveFrame(slots).ToMatrixFrame();
    }

    private bool HasActivePulse(TimeSpan elapsed)
    {
        foreach (TimeSpan? pulseAt in lastPulseAt)
        {
            if (pulseAt is { } value
                && value <= elapsed
                && elapsed - value < fadeDuration)
            {
                return true;
            }
        }

        return false;
    }
}

internal static class Pid02E0LightingMath
{
    internal static long CycleIndex(TimeSpan elapsed, TimeSpan cycleDuration) =>
        elapsed.Ticks / cycleDuration.Ticks;

    internal static double CyclePhase(TimeSpan elapsed, TimeSpan cycleDuration) =>
        (elapsed.Ticks % cycleDuration.Ticks) / (double)cycleDuration.Ticks;

    internal static byte BreathingIntensity(double phase)
    {
        // Three independent Synapse oracle captures establish a four-quarter
        // trapezoid: black hold, linear rise, full-color hold, linear fall.
        // For the captured seven-second cycle, each quarter lasts 1.75 s.
        phase -= Math.Floor(phase);
        return phase switch
        {
            < 0.25 => 0,
            < 0.50 => ToByte(((phase - 0.25) * 4.0) * 255.0),
            < 0.75 => 255,
            _ => ToByte(((1.0 - phase) * 4.0) * 255.0),
        };
    }

    internal static TimeSpan BreathingFrameDelay(TimeSpan elapsed, TimeSpan cycleDuration)
    {
        ThrowIfNegative(elapsed);
        ThrowIfNotPositive(cycleDuration, nameof(cycleDuration));

        long quarterTicks = cycleDuration.Ticks / 4;
        if (quarterTicks <= 0)
        {
            return Pid02E0LightingTiming.AnimatedFrameInterval;
        }

        long cycleTicks = elapsed.Ticks % cycleDuration.Ticks;
        int quarter = (int)Math.Min(3, cycleTicks / quarterTicks);
        bool isRamp = quarter is 1 or 3;
        if (isRamp)
        {
            return Pid02E0LightingTiming.AnimatedFrameInterval;
        }

        long offsetTicks = cycleTicks - (quarter * quarterTicks);
        long streamedPlateauTicks = Math.Min(
            Pid02E0LightingTiming.BreathingPlateauStreamDuration.Ticks,
            quarterTicks);
        if (offsetTicks < streamedPlateauTicks)
        {
            return Pid02E0LightingTiming.AnimatedFrameInterval;
        }

        // Synapse repeats the unchanged plateau for about one second, then
        // suppresses redundant matrix reports until the next ramp. Resume one
        // frame into the ramp, matching the first captured non-flat samples.
        long ticksToRamp = quarterTicks - offsetTicks;
        return TimeSpan.FromTicks(ticksToRamp)
            + Pid02E0LightingTiming.AnimatedFrameInterval;
    }

    internal static MatrixRgb HueToRgb(double hue)
    {
        hue -= Math.Floor(hue);
        double scaled = hue * 6.0;
        int sector = (int)Math.Floor(scaled);
        double fraction = scaled - sector;
        byte rising = ToByte(fraction * 255.0);
        byte falling = ToByte((1.0 - fraction) * 255.0);

        return sector switch
        {
            0 => new MatrixRgb(255, rising, 0),
            1 => new MatrixRgb(falling, 255, 0),
            2 => new MatrixRgb(0, 255, rising),
            3 => new MatrixRgb(0, falling, 255),
            4 => new MatrixRgb(rising, 0, 255),
            _ => new MatrixRgb(255, 0, falling),
        };
    }

    internal static byte ToByte(double value) => checked((byte)Math.Clamp(
        Math.Round(value + 1e-9, MidpointRounding.AwayFromZero),
        byte.MinValue,
        byte.MaxValue));

    internal static void ThrowIfNegative(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }
    }

    internal static void ThrowIfNotPositive(TimeSpan duration, string parameterName)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
