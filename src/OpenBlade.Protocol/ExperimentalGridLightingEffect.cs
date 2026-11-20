using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Software patterns over an independently admitted six-row address grid.
/// This renderer establishes neither physical key positions nor device admission.
/// </summary>
internal sealed class ExperimentalGridLightingEffect
{
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);
    private const int Rows = 6;
    private readonly int columns;
    private readonly bool rz090529;
    private readonly LightingSettings settings;
    private readonly StarlightPattern? starlight;
    private readonly ImportedSequenceTimeline? sequence;
    private readonly TimeSpan finalFrameStarts;
    private readonly List<(int Slot, TimeSpan At)> pulses = [];
    internal bool IsKeyTriggered => settings.Effect is LightingEffect.Reactive or LightingEffect.Ripple;
    internal bool IsSequence => sequence is not null;
    // Reactive pulses and per-key sequences address keyboard slots only; every
    // other RZ09-0529 pattern also has a defined value on the LED38 row.
    internal bool IncludesVaporChamber => rz090529 && sequence is null
        && settings.Effect != LightingEffect.Reactive;

    internal ExperimentalGridLightingEffect(LightingSettings settings, bool rz090529 = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsSupported(settings, rz090529))
        {
            throw new ArgumentException("The settings do not describe a supported typed grid effect.", nameof(settings));
        }
        this.settings = settings;
        this.rz090529 = rz090529;
        columns = rz090529 ? Rz090529KeyboardLightingLayout.Columns : 16;
        if (settings.Parameters is SequenceLightingParameters playback)
        {
            if (playback.LayoutId is not (KeyboardLightingLayoutIds.Rz09_0530
                or KeyboardLightingLayoutIds.Rz09_0529))
            {
                throw new ArgumentException("The sequence must target this model's reviewed keyboard layout.", nameof(settings));
            }
            KeyboardLightingSequence decoded = KeyboardLightingSequenceCodec.Decode(
                playback.TimelineBase64, playback.LayoutId);
            sequence = new(playback);
            finalFrameStarts = TimeSpan.FromMilliseconds(decoded.Frames
                .Take(decoded.Frames.Count - 1).Sum(frame => (long)frame.DurationMilliseconds));
        }
        if (settings.Parameters is StarlightLightingParameters stars)
        {
            // Deliberate software timing, independent of any firmware waveform.
            double seconds = stars.Duration switch
            {
                StarlightLightingDuration.Short => 1,
                StarlightLightingDuration.Long => 3,
                _ => 2,
            };
            starlight = new(stars.ColorMode, ToMatrix(stars.PrimaryColor),
                ToMatrix(stars.SecondaryColor), TimeSpan.FromSeconds(seconds));
        }
    }

    internal static bool IsSupported(LightingSettings? settings, bool rz090529 = false) =>
        LightingSettingsValidation.IsWellFormed(settings)
        && (rz090529 && settings!.Parameters is SpectrumCyclingLightingParameters
            || !rz090529 && settings!.Parameters is
                BreathingLightingParameters { ColorMode: BreathingColorMode.Single or BreathingColorMode.Dual }
                or WaveLightingParameters { Direction: LightingDirection.Leftward }
            || settings!.Parameters is SequenceLightingParameters sequence
                && (rz090529
                    ? sequence.LayoutId == KeyboardLightingLayoutIds.Rz09_0529
                    : sequence.LayoutId == KeyboardLightingLayoutIds.Rz09_0530)
            || settings!.Parameters is FireLightingParameters or WheelLightingParameters
                or StarlightLightingParameters or TidalLightingParameters
                or ReactiveLightingParameters or RippleLightingParameters);

    internal TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        return sequence is null ? FrameInterval
            : settings.Parameters is SequenceLightingParameters { Loop: false }
                && elapsed >= finalFrameStarts ? null : sequence.GetNextFrameDelay(elapsed);
    }

    internal void Pulse(int slot, TimeSpan at)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(at, TimeSpan.Zero);
        if (!IsKeyTriggered || !(rz090529
            ? Rz090529KeyboardLightingLayout.IsKeySlot(slot)
            : Blade14KeyboardLightingLayout.IsKeySlot(slot)))
        {
            throw new CapabilityUnavailableException("The key is outside the reviewed keyboard map.");
        }
        if (pulses.Count == 64) { pulses.RemoveAt(0); }
        pulses.Add((slot, at));
    }

    internal IReadOnlyList<RgbColor> RenderFrame(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        if (sequence is not null)
        {
            return sequence.FrameAt(elapsed).Slots.Take(Rows * columns).ToArray();
        }
        if (IsKeyTriggered)
        {
            TimeSpan duration = PulseDuration();
            pulses.RemoveAll(pulse => elapsed - pulse.At >= duration);
        }
        var colors = new RgbColor[Rows * columns];
        for (int slot = 0; slot < colors.Length; slot++)
        {
            int row = slot / columns;
            int column = slot % columns;
            colors[slot] = settings.Parameters switch
            {
                BreathingLightingParameters breathing => Breathing(breathing, elapsed),
                WaveLightingParameters => Hue(column / (double)columns + Phase(elapsed, 3)),
                SpectrumCyclingLightingParameters => Hue(Phase(elapsed, 12)),
                FireLightingParameters => Fire(row, column, elapsed),
                WheelLightingParameters wheel => Wheel(row, column, wheel, elapsed),
                TidalLightingParameters tidal => Tidal(row, column, tidal, elapsed),
                ReactiveLightingParameters or RippleLightingParameters => RenderPulses(slot, elapsed),
                StarlightLightingParameters => FromMatrix(starlight!.RenderSlot(slot, elapsed)),
                _ => throw new InvalidOperationException("Unsupported grid effect."),
            };
        }
        // Brightness is applied separately by the device port, not baked into RGB.
        return colors;
    }

    // LED38 occupies logical canvas row 0, columns 0..17. Grid patterns use
    // that same row/column; Ripple uses the logical key-position space, and
    // Starlight gives each LED its own slot after the 114 keyboard slots.
    internal IReadOnlyList<RgbColor> RenderVaporChamber(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        if (!IncludesVaporChamber)
        {
            throw new InvalidOperationException("This grid effect has no Vapor Chamber output.");
        }
        if (IsKeyTriggered)
        {
            TimeSpan duration = PulseDuration();
            pulses.RemoveAll(pulse => elapsed - pulse.At >= duration);
        }
        var colors = new RgbColor[Rz090529KeyboardLightingFrame.VaporChamberColumns];
        for (int column = 0; column < colors.Length; column++)
        {
            colors[column] = settings.Parameters switch
            {
                SpectrumCyclingLightingParameters => Hue(Phase(elapsed, 12)),
                FireLightingParameters => Fire(0, column, elapsed),
                WheelLightingParameters wheel => Wheel(0, column, wheel, elapsed),
                TidalLightingParameters tidal => Tidal(0, column, tidal, elapsed),
                RippleLightingParameters ripple => Scale(ripple.Color, RippleIntensity(column, 0, elapsed)),
                StarlightLightingParameters => FromMatrix(starlight!.RenderSlot(Rows * columns + column, elapsed)),
                _ => throw new InvalidOperationException("Unsupported Vapor Chamber effect."),
            };
        }
        return colors;
    }

    // All periods, spatial functions and palettes below are explicit host-software
    // choices. They do not claim to reproduce a captured or vendor-native effect.
    private static RgbColor Breathing(BreathingLightingParameters value, TimeSpan elapsed)
    {
        const long cycleTicks = 4 * TimeSpan.TicksPerSecond;
        long cycle = elapsed.Ticks / cycleTicks;
        double phase = elapsed.Ticks % cycleTicks / (double)cycleTicks;
        RgbColor color = value.ColorMode == BreathingColorMode.Dual && (cycle & 1) != 0
            ? value.SecondaryColor!.Value : value.PrimaryColor!.Value;
        return Scale(color, 0.5 - 0.5 * Math.Cos(phase * 2 * Math.PI));
    }

    private static RgbColor Fire(int row, int column, TimeSpan elapsed) =>
        FromMatrix(SoftwareFirePattern.Render(row, column, elapsed));

    private RgbColor Wheel(int row, int column, WheelLightingParameters value, TimeSpan elapsed)
    {
        double angle = Math.Atan2(row - 2.5, column - (columns - 1) / 2.0) / (2 * Math.PI);
        double rotation = Phase(elapsed, 6);
        return Hue(angle + (value.Direction == LightingRotationDirection.Clockwise ? -rotation : rotation));
    }

    private RgbColor Tidal(int row, int column, TidalLightingParameters value, TimeSpan elapsed)
    {
        double center = (columns - 1) / 2.0;
        double x = (column - center) / center;
        double y = (row - 2.5) / 2.5;
        double radius = Math.Sqrt((x * x + y * y) / 2);
        double time = Phase(elapsed, 5);
        double phase = Fraction(value.Direction == LightingDirection.Leftward ? time - radius : time + radius);
        // Two separated smooth washes, with dark space between their crests.
        double first = Math.Pow(Math.Max(0, Math.Cos(phase * 2 * Math.PI)), 4);
        double second = Math.Pow(Math.Max(0, -Math.Cos(phase * 2 * Math.PI)), 4);
        RgbColor primary = value.RandomColors ? Hue(Phase(elapsed, 35)) : value.PrimaryColor;
        RgbColor secondary = value.RandomColors ? Hue(Phase(elapsed, 35) + 0.5) : value.SecondaryColor;
        return new(ToByte(primary.Red * first + secondary.Red * second),
            ToByte(primary.Green * first + secondary.Green * second),
            ToByte(primary.Blue * first + secondary.Blue * second));
    }

    private TimeSpan PulseDuration() => settings.Parameters is ReactiveLightingParameters reactive
        ? reactive.Duration switch
        {
            ReactiveLightingDuration.Short => TimeSpan.FromSeconds(0.5),
            ReactiveLightingDuration.Long => TimeSpan.FromSeconds(2),
            _ => TimeSpan.FromSeconds(1),
        } : TimeSpan.FromSeconds(1.5);

    private RgbColor RenderPulses(int slot, TimeSpan elapsed)
    {
        if (settings.Parameters is RippleLightingParameters ripple)
        {
            return TryGetKeyPosition(slot, out double x, out double y)
                ? Scale(ripple.Color, RippleIntensity(x, y, elapsed)) : default;
        }
        double strongest = 0;
        double duration = PulseDuration().TotalSeconds;
        foreach ((int origin, TimeSpan at) in pulses)
        {
            double age = (elapsed - at).TotalSeconds;
            if (age < 0 || age >= duration) { continue; }
            strongest = Math.Max(strongest, slot == origin ? 1 - age / duration : 0);
        }
        return Scale(((ReactiveLightingParameters)settings.Parameters!).Color, strongest);
    }

    private double RippleIntensity(double x, double y, TimeSpan elapsed)
    {
        double strongest = 0;
        double duration = PulseDuration().TotalSeconds;
        foreach ((int origin, TimeSpan at) in pulses)
        {
            double age = (elapsed - at).TotalSeconds;
            if (age < 0 || age >= duration || !TryGetKeyPosition(origin, out double ox, out double oy))
            {
                continue;
            }
            double distance = Math.Sqrt((x - ox) * (x - ox) + (y - oy) * (y - oy));
            double band = Math.Max(0, 1 - Math.Abs(distance - age / duration * 18) / 1.25);
            strongest = Math.Max(strongest, band * (1 - age / duration));
        }
        return strongest;
    }

    private static double Phase(TimeSpan elapsed, int seconds) =>
        elapsed.Ticks % (seconds * TimeSpan.TicksPerSecond) / (double)(seconds * TimeSpan.TicksPerSecond);

    private bool TryGetKeyPosition(int slot, out double x, out double y) => rz090529
        ? Rz090529KeyboardLightingLayout.TryGetKeyPosition(slot, out x, out y)
        : Blade14KeyboardLightingLayout.TryGetKeyPosition(slot, out x, out y);

    private static double Fraction(double value) => value - Math.Floor(value);

    private static RgbColor Hue(double hue)
    {
        double sector = Fraction(hue) * 6;
        byte ascending = ToByte((sector - Math.Floor(sector)) * 255);
        byte descending = (byte)(255 - ascending);
        return (int)sector switch
        {
            0 => new(255, ascending, 0),
            1 => new(descending, 255, 0),
            2 => new(0, 255, ascending),
            3 => new(0, descending, 255),
            4 => new(ascending, 0, 255),
            _ => new(255, 0, descending),
        };
    }

    private static RgbColor Scale(RgbColor color, double amount) =>
        new(ToByte(color.Red * amount), ToByte(color.Green * amount), ToByte(color.Blue * amount));

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
    private static MatrixRgb ToMatrix(RgbColor? color) => color is { } value
        ? new(value.Red, value.Green, value.Blue) : default;
    private static RgbColor FromMatrix(MatrixRgb color) => new(color.Red, color.Green, color.Blue);
}
