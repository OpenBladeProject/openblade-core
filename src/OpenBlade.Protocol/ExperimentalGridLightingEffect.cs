using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Software patterns over an independently admitted 6-by-16 address grid.
/// This renderer establishes neither physical key positions nor device admission.
/// </summary>
internal sealed class ExperimentalGridLightingEffect
{
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);
    private const int Rows = 6;
    private const int Columns = 16;
    private readonly LightingSettings settings;
    private readonly StarlightPattern? starlight;
    private readonly ImportedSequenceTimeline? sequence;
    private readonly TimeSpan finalFrameStarts;
    private readonly List<(int Slot, TimeSpan At)> pulses = [];
    internal bool IsKeyTriggered => settings.Effect is LightingEffect.Reactive or LightingEffect.Ripple;
    internal bool IsSequence => sequence is not null;

    internal ExperimentalGridLightingEffect(LightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsSupported(settings))
        {
            throw new ArgumentException("The settings do not describe a supported typed grid effect.", nameof(settings));
        }
        this.settings = settings;
        if (settings.Parameters is SequenceLightingParameters playback)
        {
            if (playback.LayoutId != KeyboardLightingLayoutIds.Rz09_0530)
            {
                throw new ArgumentException("The sequence must target this Blade 14 layout.", nameof(settings));
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

    internal static bool IsSupported(LightingSettings? settings) =>
        LightingSettingsValidation.IsWellFormed(settings)
        && settings!.Parameters is
            BreathingLightingParameters { ColorMode: BreathingColorMode.Single or BreathingColorMode.Dual }
            or WaveLightingParameters { Direction: LightingDirection.Leftward }
            or FireLightingParameters or WheelLightingParameters
            or StarlightLightingParameters or TidalLightingParameters
            or ReactiveLightingParameters or RippleLightingParameters
            or SequenceLightingParameters { LayoutId: KeyboardLightingLayoutIds.Rz09_0530 };

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
        if (!IsKeyTriggered || !Blade14KeyboardLightingLayout.IsKeySlot(slot))
        {
            throw new CapabilityUnavailableException("The key is outside the reviewed Blade 14 map.");
        }
        if (pulses.Count == 64) { pulses.RemoveAt(0); }
        pulses.Add((slot, at));
    }

    internal IReadOnlyList<RgbColor> RenderFrame(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        if (sequence is not null)
        {
            return sequence.FrameAt(elapsed).Slots.Take(Rows * Columns).ToArray();
        }
        if (IsKeyTriggered)
        {
            TimeSpan duration = PulseDuration();
            pulses.RemoveAll(pulse => elapsed - pulse.At >= duration);
        }
        var colors = new RgbColor[Rows * Columns];
        for (int slot = 0; slot < colors.Length; slot++)
        {
            int row = slot / Columns;
            int column = slot % Columns;
            colors[slot] = settings.Parameters switch
            {
                BreathingLightingParameters breathing => Breathing(breathing, elapsed),
                WaveLightingParameters => Hue(column / (double)Columns + Phase(elapsed, 3)),
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

    private static RgbColor Fire(int row, int column, TimeSpan elapsed)
    {
        double time = Phase(elapsed, 12) * 2 * Math.PI;
        double flicker = 0.5 + 0.25 * Math.Sin(column * 1.7 + row * 0.9 - time * 3)
            + 0.25 * Math.Sin(column * 0.6 - row * 1.3 + time * 5);
        double heat = Math.Clamp(0.2 + row * 0.1 + flicker * 0.3, 0, 1);
        return new(ToByte(heat * 255), ToByte(heat * heat * 140), 0);
    }

    private static RgbColor Wheel(int row, int column, WheelLightingParameters value, TimeSpan elapsed)
    {
        double angle = Math.Atan2(row - 2.5, column - 7.5) / (2 * Math.PI);
        double rotation = Phase(elapsed, 6);
        return Hue(angle + (value.Direction == LightingRotationDirection.Clockwise ? -rotation : rotation));
    }

    private static RgbColor Tidal(int row, int column, TidalLightingParameters value, TimeSpan elapsed)
    {
        double x = (column - 7.5) / 7.5;
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
        if (!Blade14KeyboardLightingLayout.TryGetKeyPosition(slot, out double x, out double y))
        {
            return default;
        }
        double strongest = 0;
        double duration = PulseDuration().TotalSeconds;
        foreach ((int origin, TimeSpan at) in pulses)
        {
            double age = (elapsed - at).TotalSeconds;
            if (age < 0 || age >= duration) { continue; }
            double intensity;
            if (settings.Effect == LightingEffect.Reactive)
            {
                intensity = slot == origin ? 1 - age / duration : 0;
            }
            else
            {
                _ = Blade14KeyboardLightingLayout.TryGetKeyPosition(origin, out double ox, out double oy);
                double distance = Math.Sqrt((x - ox) * (x - ox) + (y - oy) * (y - oy));
                double band = Math.Max(0, 1 - Math.Abs(distance - age / duration * 18) / 1.25);
                intensity = band * (1 - age / duration);
            }
            strongest = Math.Max(strongest, intensity);
        }
        RgbColor color = settings.Parameters is ReactiveLightingParameters reactive
            ? reactive.Color : ((RippleLightingParameters)settings.Parameters!).Color;
        return Scale(color, strongest);
    }

    private static double Phase(TimeSpan elapsed, int seconds) =>
        elapsed.Ticks % (seconds * TimeSpan.TicksPerSecond) / (double)(seconds * TimeSpan.TicksPerSecond);

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
