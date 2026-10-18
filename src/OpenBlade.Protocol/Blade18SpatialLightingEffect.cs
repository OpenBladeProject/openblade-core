using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Experimental host effects using product 672's physical key matrix and shared
/// Synapse algorithms. Continuous sampling approximates the native rasterizer;
/// no other laptop's captured bands, colors or templates are used.
/// </summary>
internal sealed class Blade18SpatialLightingEffect
{
    // main.a383ea9e.js SHA256 C82C7074E5DA8ABCFFBFF95F23A6B962C1E14B04586715B9D4B7A6F11FE9E6D2:
    // module 8905 supplies Ripple Width=200/Speed=25 and Tidal Angle=160,
    // Width=100/Speed=10; UTF-8 415936 supplies Tidal's color stops.
    // RzLightingEngineApi_v4.0.55.0.dll SHA256
    // CD9FC2A61FF920B9B73E1F5E27632020E2F8586F443E426A875AD3B042B714FA:
    // RVA 0x675c0 derives Tidal period Width/(2*Speed), 0x68300 projects
    // absolute center-relative coordinates; Ripple 0x53690 uses cells/second.
    internal static readonly TimeSpan TidalCycleDuration = TimeSpan.FromSeconds(5);
    private const double RippleCellsPerSecond = 25;
    private const double RippleWidth = 2;
    private const double TidalAngle = 160 * Math.PI / 180;
    private static readonly double[] FixedStops = [0, 10, 25, 40, 60, 75, 90, 100];
    private static readonly double[] RandomStops = [0, 15, 30, 40, 65, 70, 90, 100];
    private static readonly RgbColor[] RandomPalette = CreateRandomPalette();
    private readonly LightingSettings settings;
    private readonly TimeSpan?[] lastPulseAt = new TimeSpan?[Blade18LightingFrame.SlotCount];
    private readonly double fadeMilliseconds;

    internal Blade18SpatialLightingEffect(LightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!LightingSettingsValidation.IsWellFormed(settings)
            || settings.Effect is not (LightingEffect.Reactive or LightingEffect.Ripple or LightingEffect.Tidal))
        {
            throw new ArgumentException("Only valid Reactive, Ripple and Tidal settings are supported.", nameof(settings));
        }
        this.settings = settings;
        ReactiveLightingDuration duration = settings.Parameters is ReactiveLightingParameters reactive
            ? reactive.Duration : ReactiveLightingDuration.Medium;
        fadeMilliseconds = duration switch
        {
            ReactiveLightingDuration.Short => 500,
            ReactiveLightingDuration.Long => 2000,
            _ => 1500,
        };
    }

    internal bool IsKeyTriggered => settings.Effect is LightingEffect.Reactive or LightingEffect.Ripple;

    internal void Pulse(int slot, TimeSpan at)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(at, TimeSpan.Zero);
        if (!IsKeyTriggered)
        {
            throw new InvalidOperationException("Tidal does not consume key pulses.");
        }
        if (!Blade18KeyboardLightingLayout.IsKeySlot(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
        // One timestamp per admitted key bounds storage independently of input rate.
        if (lastPulseAt[slot] is not { } previous || at >= previous)
        {
            lastPulseAt[slot] = at;
        }
    }

    internal Blade18LightingFrame RenderFrame(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        var colors = new RgbColor[Blade18LightingFrame.SlotCount];
        if (settings.Parameters is TidalLightingParameters tidal)
        {
            RenderTidal(colors, tidal, elapsed);
        }
        else
        {
            RenderPulses(colors, elapsed);
        }
        return new(colors);
    }

    private void RenderPulses(RgbColor[] colors, TimeSpan elapsed)
    {
        for (int origin = 0; origin < lastPulseAt.Length; origin++)
        {
            if (lastPulseAt[origin] is not { } at || at > elapsed)
            {
                continue;
            }
            double ageMilliseconds = (elapsed - at).TotalMilliseconds;
            if (settings.Effect == LightingEffect.Reactive)
            {
                if (ageMilliseconds < fadeMilliseconds)
                {
                    colors[origin] = Interpolate(default, settings.Color, 1 - ageMilliseconds / fadeMilliseconds);
                }
                continue;
            }
            Blade18KeyboardLightingLayout.TryGetKeyPosition(origin, out double originX, out double originY);
            double outer = ageMilliseconds / 1000 * RippleCellsPerSecond;
            double inner = Math.Max(0, outer - RippleWidth);
            for (int slot = 0; slot < colors.Length; slot++)
            {
                if (!Blade18KeyboardLightingLayout.TryGetKeyPosition(slot, out double x, out double y))
                {
                    continue;
                }
                double distance = Math.Sqrt((x - originX) * (x - originX) + (y - originY) * (y - originY));
                if (distance >= inner && distance <= outer)
                {
                    colors[slot] = settings.Color;
                }
            }
        }
    }

    private static void RenderTidal(RgbColor[] colors, TidalLightingParameters parameters, TimeSpan elapsed)
    {
        // Exact 672 manifest: 15 columns, 7 rows, no Tidal center override.
        // Synapse defaults to floor(dimension/2), with far-side extents 8 and 4.
        double time = elapsed.TotalSeconds / TidalCycleDuration.TotalSeconds;
        bool outward = parameters.Direction == LightingDirection.Leftward;
        for (int slot = 0; slot < colors.Length; slot++)
        {
            if (!Blade18KeyboardLightingLayout.TryGetKeyPosition(slot, out double x, out double y))
            {
                continue;
            }
            double projection = Math.Abs((x - 7) / 8 * Math.Cos(TidalAngle)
                + (y - 3) / 4 * Math.Sin(TidalAngle));
            double waveAge = time - (outward ? projection : 1 - projection);
            if (waveAge < 0)
            {
                continue;
            }
            long wave = (long)Math.Floor(waveAge);
            double fraction = waveAge - wave;
            double percentage = (outward ? fraction : 1 - fraction) * 100;
            RgbColor first = parameters.PrimaryColor;
            RgbColor second = parameters.SecondaryColor;
            if (parameters.RandomColors)
            {
                // Shared native generator RVA 0x69600 uses RGB 0/128/255,
                // excluding gray. Wave-local selection keeps traveling colors
                // stable; this host implementation does not reproduce its seed.
                first = RandomPalette[(int)((ulong)wave * 7 % (ulong)RandomPalette.Length)];
                second = RandomPalette[(int)(((ulong)wave * 7 + 11) % (ulong)RandomPalette.Length)];
            }
            double[] stops = parameters.RandomColors ? RandomStops : FixedStops;
            for (int i = 1; i < stops.Length; i++)
            {
                if (percentage > stops[i])
                {
                    continue;
                }
                RgbColor previous = i - 1 == 2 ? first : i - 1 == 5 ? second : default;
                RgbColor next = i == 2 ? first : i == 5 ? second : default;
                double amount = (percentage - stops[i - 1]) / (stops[i] - stops[i - 1]);
                colors[slot] = Interpolate(previous, next, amount);
                break;
            }
        }
    }

    private static RgbColor Interpolate(RgbColor first, RgbColor second, double amount) => new(
        (byte)Math.Clamp(Math.Round(first.Red + (second.Red - first.Red) * amount), 0, 255),
        (byte)Math.Clamp(Math.Round(first.Green + (second.Green - first.Green) * amount), 0, 255),
        (byte)Math.Clamp(Math.Round(first.Blue + (second.Blue - first.Blue) * amount), 0, 255));

    private static RgbColor[] CreateRandomPalette()
    {
        byte[] levels = [0, 128, 255];
        var colors = new List<RgbColor>(24);
        foreach (byte red in levels)
        {
            foreach (byte green in levels)
            {
                foreach (byte blue in levels)
                {
                    if (red != green || green != blue)
                    {
                        colors.Add(new(red, green, blue));
                    }
                }
            }
        }
        return colors.ToArray();
    }
}
