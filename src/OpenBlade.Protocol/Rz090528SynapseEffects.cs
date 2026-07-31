using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal static class Rz090528LightingMath
{
    internal static void ThrowIfNegative(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    internal static byte ToByte(double value) =>
        checked((byte)Math.Clamp((int)Math.Round(value), 0, 255));

    internal static RgbColor Scale(RgbColor color, byte intensity) => new(
        checked((byte)((color.Red * intensity + 127) / 255)),
        checked((byte)((color.Green * intensity + 127) / 255)),
        checked((byte)((color.Blue * intensity + 127) / 255)));

    internal static RgbColor Interpolate(
        RgbColor first,
        RgbColor second,
        double fraction) => new(
        ToByte(first.Red + ((second.Red - first.Red) * fraction)),
        ToByte(first.Green + ((second.Green - first.Green) * fraction)),
        ToByte(first.Blue + ((second.Blue - first.Blue) * fraction)));
}

internal static class Rz090528CapturedPalette
{
    internal static IReadOnlyList<RgbColor> Colors { get; } =
    [
        new(0xFF, 0x00, 0x00), new(0xFF, 0x40, 0x00),
        new(0xFF, 0x80, 0x00), new(0xFF, 0xBF, 0x00),
        new(0xFF, 0xFF, 0x00), new(0xBF, 0xFF, 0x00),
        new(0x80, 0xFF, 0x00), new(0x40, 0xFF, 0x00),
        new(0x00, 0xFF, 0x00), new(0x00, 0xFF, 0x40),
        new(0x00, 0xFF, 0x80), new(0x00, 0xFF, 0xBF),
        new(0x00, 0xFF, 0xFF), new(0x00, 0xBF, 0xFF),
        new(0x00, 0x80, 0xFF), new(0x00, 0x40, 0xFF),
        new(0x00, 0x00, 0xFF), new(0x40, 0x00, 0xFF),
        new(0x80, 0x00, 0xFF), new(0xBF, 0x00, 0xFF),
        new(0xFF, 0x00, 0xFF), new(0xFF, 0x00, 0xBF),
        new(0xFF, 0x00, 0x80), new(0xFF, 0x00, 0x40),
    ];
}

internal sealed class Rz090528RandomColorPairSource
{
    private readonly byte[] seed;

    internal Rz090528RandomColorPairSource()
        : this(RandomNumberGenerator.GetBytes(32))
    {
    }

    internal Rz090528RandomColorPairSource(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Length < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(seed));
        }

        this.seed = (byte[])seed.Clone();
    }

    internal (RgbColor Primary, RgbColor Secondary) ColorsForCycle(long cycleIndex)
    {
        if (cycleIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cycleIndex));
        }

        Span<byte> input = stackalloc byte[seed.Length + sizeof(long)];
        seed.CopyTo(input);
        BitConverter.TryWriteBytes(input[seed.Length..], cycleIndex);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        IReadOnlyList<RgbColor> palette = Rz090528CapturedPalette.Colors;
        int first = (int)(BitConverter.ToUInt32(digest) % (uint)palette.Count);
        int second = (int)(BitConverter.ToUInt32(digest[4..]) % (uint)(palette.Count - 1));
        if (second >= first)
        {
            second++;
        }

        return (palette[first], palette[second]);
    }
}

internal sealed class Rz090528StarlightEffect : IRz090528LightingEffect
{
    private const int MinimumPeriodUnits = 8;
    private const int PeriodUnitVariants = 5;
    private readonly StarlightColorMode colorMode;
    private readonly RgbColor primaryColor;
    private readonly RgbColor secondaryColor;
    private readonly TimeSpan twinkleDuration;

    internal Rz090528StarlightEffect(
        StarlightColorMode colorMode,
        RgbColor primaryColor,
        RgbColor secondaryColor,
        TimeSpan twinkleDuration)
    {
        if (!Enum.IsDefined(colorMode) || twinkleDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException();
        }

        this.colorMode = colorMode;
        this.primaryColor = primaryColor;
        this.secondaryColor = secondaryColor;
        this.twinkleDuration = twinkleDuration;
    }

    public bool RequiresContinuousRendering => true;

    public TimeSpan GetNextFrameDelay(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return Rz090528LightingTiming.AnimatedFrameInterval;
    }

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        double normalizedTime = elapsed.TotalSeconds / twinkleDuration.TotalSeconds;
        RgbColor[] slots = new RgbColor[Rz090528MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (Rz090528MatrixFrame.IsHole(slot))
            {
                continue;
            }

            uint slotHash = Mix((uint)slot + 1u);
            int periodUnits = MinimumPeriodUnits + (int)(slotHash % PeriodUnitVariants);
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

            double intensity = eventAge <= 0.5
                ? eventAge * 2.0
                : (1.0 - eventAge) * 2.0;
            slots[slot] = Rz090528LightingMath.Scale(
                ColorForEvent(slotHash, eventIndex),
                Rz090528LightingMath.ToByte(intensity * 255.0));
        }

        return new(slots);
    }

    private RgbColor ColorForEvent(uint slotHash, long eventIndex)
    {
        uint eventHash = Mix(slotHash ^ unchecked((uint)eventIndex * 0x85EBCA6Bu));
        return colorMode switch
        {
            StarlightColorMode.Single => primaryColor,
            StarlightColorMode.Dual => (eventHash & 1u) == 0u
                ? primaryColor
                : secondaryColor,
            StarlightColorMode.Random =>
                Rz090528CapturedPalette.Colors[
                    (int)(eventHash % (uint)Rz090528CapturedPalette.Colors.Count)],
            _ => throw new InvalidOperationException(),
        };
    }

    private static double Fraction(uint value) =>
        value / ((double)uint.MaxValue + 1.0);

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        return value ^ (value >> 16);
    }
}

internal sealed class Rz090528AmbientAwarenessEffect : IRz090528LightingEffect
{
    private Rz090528MatrixFrame frame = Rz090528MatrixFrame.Uniform(default);

    public bool RequiresContinuousRendering => false;

    public TimeSpan GetNextFrameDelay(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return Timeout.InfiniteTimeSpan;
    }

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return frame;
    }

    internal void Update(AmbientLightingFrame value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsWellFormed())
        {
            throw new ArgumentException("The Ambient Awareness frame is invalid.", nameof(value));
        }

        RgbColor[] slots = value.Slots.ToArray();
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (Rz090528MatrixFrame.IsHole(slot))
            {
                slots[slot] = default;
            }
        }
        frame = new(slots);
    }
}

internal sealed class Rz090528AudioMeterEffect(byte colorBoost)
    : IRz090528LightingEffect
{
    private Rz090528MatrixFrame frame = Rz090528MatrixFrame.Uniform(default);

    internal byte ColorBoost { get; } = colorBoost is >= 1 and <= 10
        ? colorBoost
        : throw new ArgumentOutOfRangeException(nameof(colorBoost));

    public bool RequiresContinuousRendering => false;

    public TimeSpan GetNextFrameDelay(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return Timeout.InfiniteTimeSpan;
    }

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return frame;
    }

    internal void Update(AmbientLightingFrame value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsWellFormed())
        {
            throw new ArgumentException("The Audio Meter frame is invalid.", nameof(value));
        }
        RgbColor[] slots = value.Slots.ToArray();
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (Rz090528MatrixFrame.IsHole(slot))
            {
                slots[slot] = default;
            }
        }
        frame = new(slots);
    }
}

internal sealed class Rz090528BreathingEffect : IRz090528LightingEffect
{
    internal static readonly TimeSpan CycleDuration = TimeSpan.FromSeconds(7);
    private readonly BreathingColorMode mode;
    private readonly RgbColor primary;
    private readonly RgbColor secondary;
    private readonly Rz090528RandomColorPairSource randomSource = new();

    internal Rz090528BreathingEffect(
        BreathingColorMode mode,
        RgbColor primary,
        RgbColor secondary)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        this.mode = mode;
        this.primary = primary;
        this.secondary = secondary;
    }

    public bool RequiresContinuousRendering => true;

    public TimeSpan GetNextFrameDelay(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return Rz090528LightingTiming.AnimatedFrameInterval;
    }

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        long cycle = elapsed.Ticks / CycleDuration.Ticks;
        double phase = (elapsed.Ticks % CycleDuration.Ticks) /
            (double)CycleDuration.Ticks;
        byte intensity = phase switch
        {
            < 0.25 => 0,
            < 0.50 => Rz090528LightingMath.ToByte((phase - 0.25) * 4 * 255),
            < 0.75 => 255,
            _ => Rz090528LightingMath.ToByte((1 - phase) * 4 * 255),
        };
        RgbColor color = mode switch
        {
            BreathingColorMode.Single => primary,
            BreathingColorMode.Dual when (cycle & 1) == 0 => primary,
            BreathingColorMode.Dual => secondary,
            BreathingColorMode.Random =>
                randomSource.ColorsForCycle(cycle).Primary,
            _ => throw new InvalidOperationException(),
        };
        return Rz090528MatrixFrame.Uniform(
            Rz090528LightingMath.Scale(color, intensity));
    }
}

internal sealed class Rz090528ReactiveEffect : IRz090528KeyTriggeredLightingEffect
{
    private readonly RgbColor color;
    private readonly TimeSpan duration;
    private readonly TimeSpan?[] pulses =
        new TimeSpan?[Rz090528MatrixFrame.SlotCount];

    internal Rz090528ReactiveEffect(RgbColor color, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        this.color = color;
        this.duration = duration;
    }

    public bool RequiresContinuousRendering => true;

    public TimeSpan GetNextFrameDelay(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return pulses.Any(value =>
            value is { } at && at <= elapsed && elapsed - at < duration)
                ? Rz090528LightingTiming.AnimatedFrameInterval
                : Rz090528LightingTiming.IdleKeepaliveInterval;
    }

    public void Pulse(int slot, TimeSpan at)
    {
        Rz090528LightingMath.ThrowIfNegative(at);
        if ((uint)slot >= Rz090528MatrixFrame.SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
        if (Rz090528MatrixFrame.IsHole(slot))
        {
            throw new ArgumentException(
                $"Captured layout-hole slot {slot} cannot receive a Reactive pulse.",
                nameof(slot));
        }
        pulses[slot] = at;
    }

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        RgbColor[] slots = new RgbColor[Rz090528MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (pulses[slot] is not { } at || at > elapsed)
            {
                continue;
            }
            TimeSpan age = elapsed - at;
            if (age >= duration)
            {
                continue;
            }
            double remaining = 1 - (age.TotalSeconds / duration.TotalSeconds);
            slots[slot] = Rz090528LightingMath.Scale(
                color,
                Rz090528LightingMath.ToByte(remaining * 255));
        }
        return new(slots);
    }
}

internal sealed class Rz090528FireEffect : IRz090528LightingEffect
{
    public bool RequiresContinuousRendering => true;

    public TimeSpan GetNextFrameDelay(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return Rz090528LightingTiming.AnimatedFrameInterval;
    }

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        long frameIndex = elapsed.Ticks /
            Rz090528LightingTiming.AnimatedFrameInterval.Ticks;
        RgbColor[] slots = new RgbColor[Rz090528MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (Rz090528MatrixFrame.IsHole(slot))
            {
                continue;
            }
            int row = slot / Rz090528MatrixFrame.ColumnCount;
            int column = slot % Rz090528MatrixFrame.ColumnCount;
            uint noise = FireMix(unchecked(
                (uint)(frameIndex * 0x9E3779B9L)
                ^ (uint)(column * 0x85EBCA6B)
                ^ (uint)(row * 0xC2B2AE35)));
            double baseHeat = 1.0 - (row / (double)Rz090528MatrixFrame.RowCount);
            double flicker = 0.55 + ((noise & 0xFFFF) / 65535.0 * 0.45);
            double heat = Math.Clamp(baseHeat * flicker, 0, 1);
            slots[slot] = heat switch
            {
                < 0.33 => new(
                    Rz090528LightingMath.ToByte(heat * 3 * 180),
                    0,
                    0),
                < 0.72 => new(
                    255,
                    Rz090528LightingMath.ToByte((heat - 0.33) / 0.39 * 150),
                    0),
                _ => new(
                    255,
                    Rz090528LightingMath.ToByte(150 + ((heat - 0.72) / 0.28 * 105)),
                    Rz090528LightingMath.ToByte((heat - 0.72) / 0.28 * 40)),
            };
        }
        return new(slots);
    }

    private static uint FireMix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        return value ^ (value >> 16);
    }
}

internal sealed class Rz090528RippleEffect : IRz090528KeyTriggeredLightingEffect
{
    internal static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan BandInterval = TimeSpan.FromMilliseconds(50);
    private static readonly (double Inner, double Outer)[] Bands =
    [
        (0.00, 0.00), (0.00, 1.00), (1.00, 2.24), (1.41, 3.61),
        (2.83, 4.24), (3.61, 5.39), (4.47, 6.32), (5.83, 7.28),
        (6.71, 8.25), (7.28, 9.22), (8.25, 10.44), (9.22, 11.40),
        (10.20, 13.15), (11.18, 13.34), (12.17, 14.32), (14.00, 14.32),
    ];
    private readonly RgbColor color;
    private readonly TimeSpan?[] lastPulseAt =
        new TimeSpan?[Rz090528MatrixFrame.SlotCount];

    internal Rz090528RippleEffect(RgbColor color) => this.color = color;

    public bool RequiresContinuousRendering => true;

    public TimeSpan GetNextFrameDelay(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return HasActivePulse(elapsed)
            ? Rz090528LightingTiming.AnimatedFrameInterval
            : Rz090528LightingTiming.IdleKeepaliveInterval;
    }

    public void Pulse(int slot, TimeSpan at)
    {
        Rz090528LightingMath.ThrowIfNegative(at);
        if ((uint)slot >= Rz090528MatrixFrame.SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
        if (Rz090528MatrixFrame.IsHole(slot))
        {
            throw new ArgumentException(
                $"Captured layout-hole slot {slot} cannot receive a Ripple pulse.",
                nameof(slot));
        }

        lastPulseAt[slot] = at;
    }

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        RgbColor[] slots = new RgbColor[Rz090528MatrixFrame.SlotCount];
        for (int origin = 0; origin < lastPulseAt.Length; origin++)
        {
            if (lastPulseAt[origin] is not { } pulseAt || pulseAt > elapsed)
            {
                continue;
            }

            int step = (int)((elapsed - pulseAt).Ticks / BandInterval.Ticks);
            if ((uint)step >= Bands.Length)
            {
                continue;
            }

            (double inner, double outer) = Bands[step];
            int originRow = origin / Rz090528MatrixFrame.ColumnCount;
            int originColumn = origin % Rz090528MatrixFrame.ColumnCount;
            for (int slot = 0; slot < slots.Length; slot++)
            {
                if (Rz090528MatrixFrame.IsHole(slot))
                {
                    continue;
                }

                int rowDelta = (slot / Rz090528MatrixFrame.ColumnCount) - originRow;
                int columnDelta = (slot % Rz090528MatrixFrame.ColumnCount) - originColumn;
                double distance = Math.Sqrt(
                    (rowDelta * rowDelta) + (columnDelta * columnDelta));
                if (distance + 1e-9 >= inner && distance <= outer + 1e-9)
                {
                    slots[slot] = color;
                }
            }
        }

        return new(slots);
    }

    private bool HasActivePulse(TimeSpan elapsed) =>
        lastPulseAt.Any(value =>
            value is { } at && at <= elapsed && elapsed - at < Duration);
}

internal sealed class Rz090528TidalEffect : IRz090528LightingEffect
{
    private readonly RgbColor primaryColor;
    private readonly RgbColor secondaryColor;
    private readonly bool randomColors;
    private readonly LightingDirection direction;
    private readonly Rz090528RandomColorPairSource randomColorSource;

    internal Rz090528TidalEffect(
        RgbColor primaryColor,
        RgbColor secondaryColor,
        bool randomColors,
        LightingDirection direction,
        Rz090528RandomColorPairSource? randomColorSource = null)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        this.primaryColor = primaryColor;
        this.secondaryColor = secondaryColor;
        this.randomColors = randomColors;
        this.direction = direction;
        this.randomColorSource = randomColorSource ?? new();
    }

    public bool RequiresContinuousRendering => true;

    public TimeSpan GetNextFrameDelay(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return Rz090528LightingTiming.AnimatedFrameInterval;
    }

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        long cycleIndex = elapsed.Ticks / Rz090528TidalOracle.CycleDuration.Ticks;
        RgbColor first = primaryColor;
        RgbColor second = secondaryColor;
        if (randomColors)
        {
            (first, second) = randomColorSource.ColorsForCycle(cycleIndex);
        }

        return Rz090528TidalOracle.Render(direction, elapsed, first, second);
    }
}

internal sealed class Rz090528WaveEffect(LightingDirection direction)
    : IRz090528LightingEffect
{
    public bool RequiresContinuousRendering => true;

    public TimeSpan GetNextFrameDelay(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return Rz090528LightingTiming.AnimatedFrameInterval;
    }

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed) =>
        Rz090528WaveWheelOracle.Render("Wave", direction.ToString(), elapsed);
}

internal sealed class Rz090528WheelEffect(LightingRotationDirection direction)
    : IRz090528LightingEffect
{
    public bool RequiresContinuousRendering => true;

    public TimeSpan GetNextFrameDelay(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        return Rz090528LightingTiming.AnimatedFrameInterval;
    }

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed) =>
        Rz090528WaveWheelOracle.Render("Wheel", direction.ToString(), elapsed);
}

internal static class Rz090528TidalOracle
{
    internal const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_TidalOracle.json";
    internal const string ApprovedFixtureSha256 =
        "492994A0A037AF449F8D834BF5E4D536498291499731BE6117F8DCE9BA2AD452";
    internal const int FrameCount = 100;
    internal static readonly TimeSpan CycleDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);
    private static readonly Lazy<(short[] Left, short[] Right)> Templates =
        new(ReadTemplates);

    internal static Rz090528MatrixFrame Render(
        LightingDirection direction,
        TimeSpan elapsed,
        RgbColor primary,
        RgbColor secondary)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        short[] template = direction switch
        {
            LightingDirection.Leftward => Templates.Value.Left,
            LightingDirection.Rightward => Templates.Value.Right,
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
        double position = (elapsed.Ticks % CycleDuration.Ticks) /
            (double)FrameInterval.Ticks;
        int lower = (int)Math.Floor(position);
        int upper = (lower + 1) % FrameCount;
        double fraction = position - lower;
        RgbColor[] slots = new RgbColor[Rz090528MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            slots[slot] = Rz090528LightingMath.Interpolate(
                Color(template[(lower * slots.Length) + slot], primary, secondary),
                Color(template[(upper * slots.Length) + slot], primary, secondary),
                fraction);
        }
        return new(slots);
    }

    private static RgbColor Color(short value, RgbColor primary, RgbColor secondary) =>
        value switch
        {
            > 0 => Rz090528LightingMath.Scale(primary, (byte)value),
            < 0 => Rz090528LightingMath.Scale(secondary, (byte)-value),
            _ => default,
        };

    private static (short[] Left, short[] Right) ReadTemplates()
    {
        TidalDocument document = ReadDocument<TidalDocument>(
            ResourceName,
            ApprovedFixtureSha256);
        if (document.SchemaVersion != 1
            || document.EvidenceRole != "SanitizedRz090528TidalMatrixOracle"
            || document.SourceEvidenceId !=
                "RZ09-0528-SYNAPSE-QUICK-EFFECTS-ORACLE-20260730"
            || !ValidTarget(document.Target)
            || document.CycleMilliseconds != 5000
            || document.FrameIntervalMilliseconds != 50
            || document.FrameCount != FrameCount
            || document.SlotCount != Rz090528MatrixFrame.SlotCount
            || document.CapturedColor1RgbHex != "00FF00"
            || document.CapturedColor2RgbHex != "0000FF"
            || document.Templates.Length != 2)
        {
            throw new InvalidOperationException("The RZ09-0528 Tidal oracle is invalid.");
        }

        return (
            DecodeSigned(document.Templates.Single(value =>
                value.Direction == nameof(LightingDirection.Leftward))),
            DecodeSigned(document.Templates.Single(value =>
                value.Direction == nameof(LightingDirection.Rightward))));
    }

    private static short[] DecodeSigned(TidalTemplate template)
    {
        int bytes = FrameCount * Rz090528MatrixFrame.SlotCount * 2;
        if (template.Encoding !=
                "deflate-base64-signed-int16-color1-positive-color2-negative"
            || template.UncompressedBytes != bytes)
        {
            throw new InvalidOperationException("A Tidal oracle template is invalid.");
        }

        byte[] raw = Inflate(template.Data, bytes);
        short[] values = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
        for (int index = 0; index < values.Length; index++)
        {
            int slot = index % Rz090528MatrixFrame.SlotCount;
            if (values[index] is < -255 or > 255
                || Rz090528MatrixFrame.IsHole(slot) && values[index] != 0)
            {
                throw new InvalidOperationException("A Tidal oracle slot is invalid.");
            }
        }
        return values;
    }

    internal static T ReadDocument<T>(string resource, string approvedHash)
    {
        using Stream stream = typeof(Rz090528TidalOracle).Assembly
            .GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("An RZ09-0528 oracle is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != approvedHash)
        {
            throw new InvalidOperationException("An RZ09-0528 oracle hash is not approved.");
        }

        return JsonSerializer.Deserialize<T>(
            bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling =
                    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            }) ?? throw new InvalidOperationException("An RZ09-0528 oracle is malformed.");
    }

    internal static byte[] Inflate(string data, int expectedBytes)
    {
        using var input = new MemoryStream(Convert.FromBase64String(data), false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        byte[] raw = new byte[expectedBytes];
        deflate.ReadExactly(raw);
        if (deflate.ReadByte() != -1)
        {
            throw new InvalidOperationException("An oracle template has trailing data.");
        }
        return raw;
    }

    private static bool ValidTarget(OracleTarget target) =>
        target == new OracleTarget("RZ09-0528", "1532", "02C6", "2.02");

    private sealed record TidalDocument(
        int SchemaVersion,
        string EvidenceRole,
        string SourceEvidenceId,
        OracleTarget Target,
        int CycleMilliseconds,
        int FrameIntervalMilliseconds,
        int FrameCount,
        int SlotCount,
        string CapturedColor1RgbHex,
        string CapturedColor2RgbHex,
        TidalTemplate[] Templates);

    private sealed record TidalTemplate(
        string Direction,
        string SourceActionId,
        double StartSeconds,
        int FirstSourceFrame,
        int LastSourceFrame,
        string Encoding,
        int UncompressedBytes,
        string Data);

    internal sealed record OracleTarget(
        string ModelNumber,
        string VendorIdHex,
        string ProductIdHex,
        string Bios);
}

internal static class Rz090528WaveWheelOracle
{
    internal const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_WaveWheelOracle.json";
    internal const string ApprovedFixtureSha256 =
        "1193D6CC36BFF24706E19A2E5584A04760C68990D3CD194970F83C92988145BD";
    internal const int FrameCount = 25;
    internal static readonly TimeSpan CycleDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(40);
    private static readonly Lazy<IReadOnlyDictionary<string, RgbColor[]>> Templates =
        new(ReadTemplates);

    internal static Rz090528MatrixFrame Render(
        string effect,
        string direction,
        TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        if (!Templates.Value.TryGetValue($"{effect}:{direction}", out RgbColor[]? template))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        double position = (elapsed.Ticks % CycleDuration.Ticks) /
            (double)FrameInterval.Ticks;
        int lower = (int)Math.Floor(position);
        int upper = (lower + 1) % FrameCount;
        double fraction = position - lower;
        RgbColor[] slots = new RgbColor[Rz090528MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            slots[slot] = Rz090528LightingMath.Interpolate(
                template[(lower * slots.Length) + slot],
                template[(upper * slots.Length) + slot],
                fraction);
        }
        return new(slots);
    }

    private static IReadOnlyDictionary<string, RgbColor[]> ReadTemplates()
    {
        WaveWheelDocument document =
            Rz090528TidalOracle.ReadDocument<WaveWheelDocument>(
                ResourceName,
                ApprovedFixtureSha256);
        if (document.SchemaVersion != 1
            || document.EvidenceRole != "SanitizedRz090528WaveWheelMatrixOracle"
            || document.SourceEvidenceId !=
                "RZ09-0528-SYNAPSE-QUICK-EFFECTS-ORACLE-20260730"
            || document.Target != new Rz090528TidalOracle.OracleTarget(
                "RZ09-0528", "1532", "02C6", "2.02")
            || document.CycleMilliseconds != 1000
            || document.FrameIntervalMilliseconds != 40
            || document.FrameCount != FrameCount
            || document.SlotCount != Rz090528MatrixFrame.SlotCount
            || document.Templates.Length != 4)
        {
            throw new InvalidOperationException("The RZ09-0528 Wave/Wheel oracle is invalid.");
        }

        string[] expected =
        [
            "Wave:Leftward", "Wave:Rightward",
            "Wheel:Clockwise", "Wheel:Counterclockwise",
        ];
        var result = new Dictionary<string, RgbColor[]>(StringComparer.Ordinal);
        foreach (WaveWheelTemplate value in document.Templates)
        {
            string key = $"{value.Effect}:{value.Direction}";
            if (!expected.Contains(key, StringComparer.Ordinal)
                || !result.TryAdd(key, Decode(value)))
            {
                throw new InvalidOperationException("A Wave/Wheel template is unexpected.");
            }
        }
        if (!expected.All(result.ContainsKey))
        {
            throw new InvalidOperationException("A Wave/Wheel template is missing.");
        }
        return result;
    }

    private static RgbColor[] Decode(WaveWheelTemplate template)
    {
        int bytes = FrameCount * Rz090528MatrixFrame.SlotCount * 3;
        if (template.Encoding != "deflate-base64-rgb24"
            || template.UncompressedBytes != bytes)
        {
            throw new InvalidOperationException("A Wave/Wheel template is invalid.");
        }

        byte[] raw = Rz090528TidalOracle.Inflate(template.Data, bytes);
        RgbColor[] values = new RgbColor[raw.Length / 3];
        for (int index = 0; index < values.Length; index++)
        {
            int offset = index * 3;
            values[index] = new(raw[offset], raw[offset + 1], raw[offset + 2]);
            int slot = index % Rz090528MatrixFrame.SlotCount;
            if (Rz090528MatrixFrame.IsHole(slot) != (values[index] == default))
            {
                throw new InvalidOperationException(
                    "A Wave/Wheel oracle slot mask is invalid.");
            }
        }
        return values;
    }

    private sealed record WaveWheelDocument(
        int SchemaVersion,
        string EvidenceRole,
        string SourceEvidenceId,
        Rz090528TidalOracle.OracleTarget Target,
        int CycleMilliseconds,
        int FrameIntervalMilliseconds,
        int FrameCount,
        int SlotCount,
        WaveWheelTemplate[] Templates);

    private sealed record WaveWheelTemplate(
        string Effect,
        string Direction,
        string SourceActionId,
        double StartSeconds,
        int FirstSourceFrame,
        int LastSourceFrame,
        string Encoding,
        int UncompressedBytes,
        string Data);
}
