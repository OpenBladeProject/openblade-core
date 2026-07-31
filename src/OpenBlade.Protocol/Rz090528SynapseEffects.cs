using System.Numerics;
using System.Security.Cryptography;
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

        return Rz090528MatrixFrame.FromOwnedSlots(slots);
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
        frame = Rz090528MatrixFrame.FromOwnedSlots(slots);
    }
}

internal sealed class Rz090528AudioMeterEffect(double colorBoost)
    : IRz090528LightingEffect
{
    private Rz090528MatrixFrame frame = Rz090528MatrixFrame.Uniform(default);

    internal double ColorBoost { get; } =
        double.IsFinite(colorBoost)
        && colorBoost is >= 1 and <= 4
        && colorBoost * 4 == Math.Truncate(colorBoost * 4)
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
        frame = Rz090528MatrixFrame.FromOwnedSlots(slots);
    }
}

internal sealed class Rz090528BreathingEffect : IRz090528LightingEffect
{
    internal static readonly TimeSpan CycleDuration = TimeSpan.FromSeconds(7);
    internal static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan FullDuration = TimeSpan.FromSeconds(1.5);
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
        TimeSpan phase =
            TimeSpan.FromTicks(elapsed.Ticks % CycleDuration.Ticks);
        byte intensity = phase switch
        {
            { } value when value < FadeDuration =>
                Rz090528LightingMath.ToByte(
                    value.TotalMilliseconds /
                    FadeDuration.TotalMilliseconds * 255),
            { } value when value < FadeDuration + FullDuration => 255,
            { } value when value < (FadeDuration * 2) + FullDuration =>
                Rz090528LightingMath.ToByte(
                    (1 - ((value - FadeDuration - FullDuration).TotalMilliseconds /
                        FadeDuration.TotalMilliseconds)) * 255),
            _ => 0,
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
        return Rz090528MatrixFrame.FromOwnedSlots(slots);
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
        return Rz090528FireOracle.Render(elapsed);
    }
}

internal static class Rz090528FireOracle
{
    internal const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_FireOracle.json";
    internal const string ApprovedFixtureSha256 =
        "75C4E4EF09B11DF94840A79B44A6B3778CEF8F0CD2064030E28EF1A71F0378C7";
    internal const int FrameCount = 171;
    internal static readonly TimeSpan StartupFadeDuration =
        TimeSpan.FromMilliseconds(3500);
    internal static readonly TimeSpan CycleDuration =
        TimeSpan.FromMilliseconds(8550);
    private static readonly TimeSpan FrameInterval =
        TimeSpan.FromMilliseconds(50);
    private static readonly Lazy<RgbColor[]> Template = new(ReadTemplate);

    internal static Rz090528MatrixFrame Render(TimeSpan elapsed)
    {
        Rz090528LightingMath.ThrowIfNegative(elapsed);
        double position = (elapsed.Ticks % CycleDuration.Ticks) /
            (double)FrameInterval.Ticks;
        int lower = (int)Math.Floor(position);
        int upper = (lower + 1) % FrameCount;
        double fraction = position - lower;
        double startupScale = Math.Min(
            1,
            elapsed.TotalMilliseconds / StartupFadeDuration.TotalMilliseconds);
        byte startupIntensity =
            Rz090528LightingMath.ToByte(startupScale * 255);
        RgbColor[] slots = new RgbColor[Rz090528MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            RgbColor color = Rz090528LightingMath.Interpolate(
                Template.Value[(lower * slots.Length) + slot],
                Template.Value[(upper * slots.Length) + slot],
                fraction);
            slots[slot] = Rz090528LightingMath.Scale(color, startupIntensity);
        }
        return Rz090528MatrixFrame.FromOwnedSlots(slots);
    }

    private static RgbColor[] ReadTemplate()
    {
        FireDocument document =
            Rz090528LightingOracleResources.ReadDocument<FireDocument>(
                ResourceName,
                ApprovedFixtureSha256);
        if (document.SchemaVersion != 1
            || document.EvidenceRole != "SanitizedRz090528FireMatrixOracle"
            || document.SourceEvidenceId !=
                "RZ09-0528-SYNAPSE-FULL-MENU-UPPER-ORACLE-20260730"
            || !Rz090528LightingOracleResources.IsExactTarget(document.Target)
            || document.SourceActionId != "fire-select"
            || document.StartupFadeMilliseconds != 3500
            || document.CycleMilliseconds != 8550
            || document.FrameIntervalMilliseconds != 50
            || document.FrameCount != FrameCount
            || document.SlotCount != Rz090528MatrixFrame.SlotCount
            || document.Encoding != "deflate-base64-rgb24"
            || document.UncompressedBytes !=
                FrameCount * Rz090528MatrixFrame.SlotCount * 3)
        {
            throw new InvalidOperationException("The RZ09-0528 Fire oracle is invalid.");
        }

        byte[] raw = Rz090528LightingOracleResources.Inflate(
            document.Data,
            document.UncompressedBytes);
        RgbColor[] result = new RgbColor[raw.Length / 3];
        for (int index = 0; index < result.Length; index++)
        {
            int offset = index * 3;
            result[index] = new(raw[offset], raw[offset + 1], raw[offset + 2]);
            int slot = index % Rz090528MatrixFrame.SlotCount;
            if (Rz090528MatrixFrame.IsHole(slot) != (result[index] == default)
                || result[index].Blue != 0)
            {
                throw new InvalidOperationException(
                    "An RZ09-0528 Fire oracle slot is invalid.");
            }
        }
        return result;
    }

    private sealed record FireDocument(
        int SchemaVersion,
        string EvidenceRole,
        string SourceEvidenceId,
        Rz090528LightingOracleResources.Target Target,
        string SourceActionId,
        int StartupFadeMilliseconds,
        int CycleMilliseconds,
        int FrameIntervalMilliseconds,
        int FrameCount,
        int SlotCount,
        int FirstSourceFrame,
        int LastSourceFrame,
        string Encoding,
        int UncompressedBytes,
        string Data);
}

internal sealed class Rz090528RippleEffect : IRz090528KeyTriggeredLightingEffect
{
    private const double DistanceTolerance = 1e-9;
    internal static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan BandInterval = TimeSpan.FromMilliseconds(50);
    private static readonly (double Inner, double Outer)[] Bands =
    [
        (0.00, 0.00), (0.00, 1.00), (1.00, 2.24), (1.41, 3.61),
        (2.83, 4.24), (3.61, 5.39), (4.47, 6.32), (5.83, 7.28),
        (6.71, 8.25), (7.28, 9.22), (8.25, 10.44), (9.22, 11.40),
        (10.20, 13.15), (11.18, 13.34), (12.17, 14.32), (14.00, 14.32),
    ];
    private static readonly (
        double MinimumSquared,
        double MaximumSquared)[] SquaredBandBounds = CreateSquaredBandBounds();
    private static readonly RippleSlotMask[] BandMasks = CreateBandMasks();
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
        ulong lowerSlots = 0;
        ulong upperSlots = 0;
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

            RippleSlotMask mask = BandMasks[(origin * Bands.Length) + step];
            lowerSlots |= mask.Lower;
            upperSlots |= mask.Upper;
        }

        ApplyMask(slots, lowerSlots, offset: 0);
        ApplyMask(slots, upperSlots, offset: 64);
        return Rz090528MatrixFrame.FromOwnedSlots(slots);
    }

    private bool HasActivePulse(TimeSpan elapsed) =>
        lastPulseAt.Any(value =>
            value is { } at && at <= elapsed && elapsed - at < Duration);

    private static (double MinimumSquared, double MaximumSquared)[]
        CreateSquaredBandBounds()
    {
        var result =
            new (double MinimumSquared, double MaximumSquared)[Bands.Length];
        for (int band = 0; band < Bands.Length; band++)
        {
            (double inner, double outer) = Bands[band];
            double minimum = Math.Max(0, inner - DistanceTolerance);
            double maximum = outer + DistanceTolerance;
            result[band] = (minimum * minimum, maximum * maximum);
        }
        return result;
    }

    private static RippleSlotMask[] CreateBandMasks()
    {
        var result =
            new RippleSlotMask[Rz090528MatrixFrame.SlotCount * Bands.Length];
        for (int origin = 0; origin < Rz090528MatrixFrame.SlotCount; origin++)
        {
            if (Rz090528MatrixFrame.IsHole(origin))
            {
                continue;
            }

            int originRow = origin / Rz090528MatrixFrame.ColumnCount;
            int originColumn = origin % Rz090528MatrixFrame.ColumnCount;
            for (int slot = 0; slot < Rz090528MatrixFrame.SlotCount; slot++)
            {
                if (Rz090528MatrixFrame.IsHole(slot))
                {
                    continue;
                }

                int rowDelta = (slot / Rz090528MatrixFrame.ColumnCount) - originRow;
                int columnDelta = (slot % Rz090528MatrixFrame.ColumnCount) - originColumn;
                int squaredDistance =
                    (rowDelta * rowDelta) + (columnDelta * columnDelta);
                for (int band = 0; band < SquaredBandBounds.Length; band++)
                {
                    (double minimumSquared, double maximumSquared) =
                        SquaredBandBounds[band];
                    if (squaredDistance < minimumSquared
                        || squaredDistance > maximumSquared)
                    {
                        continue;
                    }

                    ref RippleSlotMask mask =
                        ref result[(origin * Bands.Length) + band];
                    mask.Add(slot);
                }
            }
        }
        return result;
    }

    private void ApplyMask(RgbColor[] slots, ulong mask, int offset)
    {
        while (mask != 0)
        {
            int bit = BitOperations.TrailingZeroCount(mask);
            slots[offset + bit] = color;
            mask &= mask - 1;
        }
    }

    private struct RippleSlotMask
    {
        internal ulong Lower;
        internal ulong Upper;

        internal void Add(int slot)
        {
            if (slot < 64)
            {
                Lower |= 1UL << slot;
            }
            else
            {
                Upper |= 1UL << (slot - 64);
            }
        }
    }
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
        "B5B5B1DBB4A1292D0C43C06E32E2AAE00834C3095ACAD0C46031536255956A93";
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
        return Rz090528MatrixFrame.FromOwnedSlots(slots);
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
        TidalDocument document =
            Rz090528LightingOracleResources.ReadDocument<TidalDocument>(
            ResourceName,
            ApprovedFixtureSha256);
        if (document.SchemaVersion != 1
            || document.EvidenceRole != "SanitizedRz090528TidalMatrixOracle"
            || document.SourceEvidenceId !=
                "RZ09-0528-SYNAPSE-QUICK-EFFECTS-ORACLE-20260730"
            || !Rz090528LightingOracleResources.IsExactTarget(document.Target)
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

        byte[] raw =
            Rz090528LightingOracleResources.Inflate(template.Data, bytes);
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

    private sealed record TidalDocument(
        int SchemaVersion,
        string EvidenceRole,
        string SourceEvidenceId,
        Rz090528LightingOracleResources.Target Target,
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

}

internal static class Rz090528WaveWheelOracle
{
    internal const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_WaveWheelOracle.json";
    internal const string ApprovedFixtureSha256 =
        "4A8345CEF4DEF2706D6D2E0B66F9C2ACD49F9564CBBF7AD5A61416A594435B6D";
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
        return Rz090528MatrixFrame.FromOwnedSlots(slots);
    }

    private static IReadOnlyDictionary<string, RgbColor[]> ReadTemplates()
    {
        WaveWheelDocument document =
            Rz090528LightingOracleResources.ReadDocument<WaveWheelDocument>(
                ResourceName,
                ApprovedFixtureSha256);
        if (document.SchemaVersion != 1
            || document.EvidenceRole != "SanitizedRz090528WaveWheelMatrixOracle"
            || document.SourceEvidenceId !=
                "RZ09-0528-SYNAPSE-QUICK-EFFECTS-ORACLE-20260730"
            || !Rz090528LightingOracleResources.IsExactTarget(document.Target)
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

        byte[] raw =
            Rz090528LightingOracleResources.Inflate(template.Data, bytes);
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
        Rz090528LightingOracleResources.Target Target,
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
