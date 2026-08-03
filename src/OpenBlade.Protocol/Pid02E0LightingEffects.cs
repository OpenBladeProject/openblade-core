using OpenBlade.Contracts;

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

internal interface IPid02E0KeyTriggeredLightingEffect : IPid02E0LightingEffect
{
    void Pulse(int slot, TimeSpan at);
}

internal static class Pid02E0LightingTiming
{
    // Thirty frames per second removes the visible stepping in fades while
    // retaining one serialized feature-report writer for the keyboard matrix.
    internal static readonly TimeSpan AnimatedFrameInterval = TimeSpan.FromTicks(
        TimeSpan.TicksPerSecond / 30);
    internal static readonly TimeSpan IdleKeepaliveInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan BreathingPlateauStreamDuration = TimeSpan.FromSeconds(1);
    // Mean of three complete Synapse cycles captured on RZ09-0581/PID 02E0:
    // 37,716.720 ms, 37,685.435 ms, and 37,724.251 ms.
    internal static readonly TimeSpan SpectrumCycle = TimeSpan.FromMilliseconds(37_708.802);
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
            Pid02E0LightingMath.SpectrumColor(phase));
    }
}

/// <summary>
/// Holds the latest keyboard-sized color sample supplied by the signed-in
/// session for host-rendered effects such as Ambient Awareness and Audio
/// Meter. The service owns the HID stream; this renderer never captures the
/// desktop, audio, or interprets screen coordinates.
/// </summary>
internal sealed class Pid02E0SessionFrameEffect : IPid02E0LightingEffect
{
    private Pid02E0MatrixFrame frame =
        Pid02E0MatrixLayout.RenderUniform(MatrixRgb.Black);

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return null;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return new Pid02E0MatrixFrame(frame.ToArray());
    }

    internal void Update(AmbientLightingFrame value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsWellFormed())
        {
            throw new ArgumentException(
                "The session-rendered lighting frame is invalid.",
                nameof(value));
        }

        MatrixRgb[] slots = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            RgbColor color = value.Slots[slot];
            slots[slot] = new MatrixRgb(
                color.Red,
                color.Green,
                color.Blue);
        }

        frame = new Pid02E0MatrixFrame(slots);
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
/// Selects independently from the 24-color palette observed across 256
/// bounded Synapse Random Breathing cycles. A selected color remains stable
/// for its cycle, including when a frame is retried.
/// </summary>
internal sealed class Pid02E0RandomPaletteColorSource : IPid02E0RandomColorSource
{
    private static readonly MatrixRgb[] Palette =
    [
        new(0x00, 0x00, 0x80),
        new(0x00, 0x00, 0xFF),
        new(0x00, 0x80, 0x00),
        new(0x00, 0x80, 0x80),
        new(0x00, 0x80, 0xFF),
        new(0x00, 0xFF, 0x00),
        new(0x00, 0xFF, 0x80),
        new(0x00, 0xFF, 0xFF),
        new(0x80, 0x00, 0x00),
        new(0x80, 0x00, 0x80),
        new(0x80, 0x00, 0xFF),
        new(0x80, 0x80, 0x00),
        new(0x80, 0x80, 0xFF),
        new(0x80, 0xFF, 0x00),
        new(0x80, 0xFF, 0x80),
        new(0x80, 0xFF, 0xFF),
        new(0xFF, 0x00, 0x00),
        new(0xFF, 0x00, 0x80),
        new(0xFF, 0x00, 0xFF),
        new(0xFF, 0x80, 0x00),
        new(0xFF, 0x80, 0x80),
        new(0xFF, 0x80, 0xFF),
        new(0xFF, 0xFF, 0x00),
        new(0xFF, 0xFF, 0x80),
    ];

    private readonly Func<int, int> selectIndex;
    private long? selectedCycle;
    private MatrixRgb selectedColor;

    internal Pid02E0RandomPaletteColorSource()
        : this(System.Security.Cryptography.RandomNumberGenerator.GetInt32)
    {
    }

    internal Pid02E0RandomPaletteColorSource(Func<int, int> selectIndex)
    {
        this.selectIndex = selectIndex ?? throw new ArgumentNullException(nameof(selectIndex));
    }

    internal static IReadOnlyList<MatrixRgb> CapturedPalette => Palette;

    public MatrixRgb ColorForCycle(long cycleIndex)
    {
        if (cycleIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cycleIndex));
        }

        if (selectedCycle == cycleIndex)
        {
            return selectedColor;
        }

        int index = selectIndex(Palette.Length);
        if ((uint)index >= Palette.Length)
        {
            throw new InvalidOperationException(
                $"The random palette selector returned invalid index {index}.");
        }

        selectedCycle = cycleIndex;
        selectedColor = Palette[index];
        return selectedColor;
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
internal sealed class Pid02E0ReactiveEffect : IPid02E0KeyTriggeredLightingEffect
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

    public void Pulse(int slot, TimeSpan at)
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
    // RGB anchors sampled every 2% of one complete cycle and averaged across
    // three independent Synapse oracle runs. The repeated blue anchors retain
    // the measured #1858FF hold that a conventional HSV wheel cannot model.
    private static readonly MatrixRgb[] SpectrumAnchors =
    [
        new(0xFF, 0x10, 0x10),
        new(0xFF, 0x25, 0x0C),
        new(0xFF, 0x3B, 0x09),
        new(0xFF, 0x52, 0x06),
        new(0xFF, 0x68, 0x03),
        new(0xFF, 0x7E, 0x00),
        new(0xFA, 0xA1, 0x00),
        new(0xF6, 0xC7, 0x00),
        new(0xF1, 0xEC, 0x00),
        new(0xED, 0xFF, 0x00),
        new(0xE8, 0xFF, 0x00),
        new(0xE3, 0xFF, 0x00),
        new(0xDF, 0xFF, 0x00),
        new(0xA4, 0xFF, 0x00),
        new(0x63, 0xFF, 0x00),
        new(0x21, 0xFF, 0x00),
        new(0x00, 0xFF, 0x4E),
        new(0x00, 0xFF, 0xFF),
        new(0x03, 0xE4, 0xFF),
        new(0x07, 0xC8, 0xFF),
        new(0x0B, 0xAC, 0xFF),
        new(0x0F, 0x90, 0xFF),
        new(0x13, 0x74, 0xFF),
        new(0x18, 0x58, 0xFF),
        new(0x18, 0x58, 0xFF),
        new(0x18, 0x58, 0xFF),
        new(0x18, 0x58, 0xFF),
        new(0x18, 0x58, 0xFF),
        new(0x19, 0x57, 0xFF),
        new(0x19, 0x57, 0xFF),
        new(0x1F, 0x55, 0xFF),
        new(0x34, 0x50, 0xFF),
        new(0x49, 0x4A, 0xFF),
        new(0x5C, 0x45, 0xFF),
        new(0x70, 0x3F, 0xFF),
        new(0x85, 0x39, 0xFF),
        new(0x99, 0x34, 0xFF),
        new(0xAC, 0x2E, 0xFF),
        new(0xC1, 0x29, 0xFF),
        new(0xD6, 0x23, 0xFF),
        new(0xEA, 0x1D, 0xFF),
        new(0xFD, 0x18, 0xFF),
        new(0xFF, 0x17, 0xE7),
        new(0xFF, 0x16, 0xCB),
        new(0xFF, 0x15, 0xB1),
        new(0xFF, 0x14, 0x95),
        new(0xFF, 0x13, 0x7B),
        new(0xFF, 0x12, 0x60),
        new(0xFF, 0x11, 0x46),
        new(0xFF, 0x10, 0x2B),
        new(0xFF, 0x10, 0x10),
    ];

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

    internal static MatrixRgb SpectrumColor(double phase)
    {
        phase -= Math.Floor(phase);
        double position = phase * (SpectrumAnchors.Length - 1);
        int lowerIndex = (int)Math.Floor(position);
        double fraction = position - lowerIndex;
        MatrixRgb lower = SpectrumAnchors[lowerIndex];
        MatrixRgb upper = SpectrumAnchors[lowerIndex + 1];

        return InterpolateColor(lower, upper, fraction);
    }

    internal static MatrixRgb InterpolateColor(
        MatrixRgb from,
        MatrixRgb to,
        double fraction)
    {
        if (fraction is < 0 or > 1 || double.IsNaN(fraction))
        {
            throw new ArgumentOutOfRangeException(nameof(fraction));
        }

        return new MatrixRgb(
            Interpolate(from.Red, to.Red, fraction),
            Interpolate(from.Green, to.Green, fraction),
            Interpolate(from.Blue, to.Blue, fraction));
    }

    private static byte Interpolate(byte from, byte to, double fraction) =>
        ToByte(from + ((to - from) * fraction));

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
