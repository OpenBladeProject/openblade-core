using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Host-rendered Product 672 standard effects whose requested parameters cannot
/// be confirmed completely through the firmware effect getter.
/// </summary>
internal sealed class Blade18StandardLightingEffect
{
    // Product 672 class rzDevice25LedMatrixBlade.dced3c2efdc9705a346c.js,
    // SHA256 56B9309797CB177012726D2EAA1C2BFD43906E9ADED6AC39488487440E72036C,
    // module 77127 inherits the common effect implementation in module 71513 of
    // rzDevice25LedMatrix.21f461fce761cff01be3.js, SHA256
    // 8F8C464818EF7E7AD75F9B18594E74A47D911F5F869C91DF3923AC2880F7F7F6.
    // That exact route defines Breathing Single/Dual colors and Wave Left/Right.
    // Product 672 chunk 6737.e50bb836b878190315d7.js, SHA256
    // 651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997,
    // module 39182 at UTF-8 offset 510919 defines Left=1 and Right=2.
    // The source does not establish Product 672's period or spatial waveform;
    // these deterministic cycles are an explicit host-software extrapolation.
    internal static readonly TimeSpan BreathingCycleDuration = TimeSpan.FromSeconds(4);
    internal static readonly TimeSpan WaveCycleDuration = TimeSpan.FromSeconds(1);

    private readonly LightingSettings settings;
    private readonly byte? region;

    internal Blade18StandardLightingEffect(LightingSettings settings, byte? region = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool supported = settings.Parameters is
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Single or BreathingColorMode.Dual,
            }
            or WaveLightingParameters { Direction: LightingDirection.Leftward };
        if (!LightingSettingsValidation.IsWellFormed(settings) || !supported)
        {
            throw new ArgumentException(
                "Only typed Breathing Single/Dual and Wave Leftward settings are supported.",
                nameof(settings));
        }

        this.settings = settings;
        this.region = region;
    }

    internal Blade18LightingFrame RenderFrame(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        var colors = new RgbColor[Blade18LightingFrame.SlotCount];
        if (settings.Parameters is BreathingLightingParameters breathing)
        {
            RenderBreathing(colors, breathing, elapsed);
        }
        else
        {
            RenderWaveLeftward(colors, elapsed);
        }
        return new(colors);
    }

    private void RenderBreathing(
        RgbColor[] colors,
        BreathingLightingParameters parameters,
        TimeSpan elapsed)
    {
        long cycle = elapsed.Ticks / BreathingCycleDuration.Ticks;
        double phase = elapsed.Ticks % BreathingCycleDuration.Ticks
            / (double)BreathingCycleDuration.Ticks;
        double intensity = 0.5 - (0.5 * Math.Cos(phase * 2 * Math.PI));
        RgbColor color = parameters.ColorMode == BreathingColorMode.Single
            || (cycle & 1) == 0
                ? parameters.PrimaryColor!.Value
                : parameters.SecondaryColor!.Value;
        RgbColor sample = Scale(color, intensity);
        for (int slot = 0; slot < colors.Length; slot++)
        {
            if (Blade18KeyboardLightingLayout.TryGetKeyPosition(region, slot, out _, out _))
            {
                colors[slot] = sample;
            }
        }
    }

    private void RenderWaveLeftward(RgbColor[] colors, TimeSpan elapsed)
    {
        double phase = elapsed.Ticks % WaveCycleDuration.Ticks
            / (double)WaveCycleDuration.Ticks;
        for (int slot = 0; slot < colors.Length; slot++)
        {
            if (Blade18KeyboardLightingLayout.TryGetKeyPosition(
                region, slot, out double x, out _))
            {
                // Increasing phase moves a fixed hue toward decreasing X.
                colors[slot] = Hue((x / 15.0) + phase);
            }
        }
    }

    private static RgbColor Scale(RgbColor color, double intensity) => new(
        ToByte(color.Red * intensity),
        ToByte(color.Green * intensity),
        ToByte(color.Blue * intensity));

    private static RgbColor Hue(double turns)
    {
        turns -= Math.Floor(turns);
        double position = turns * 6;
        int sector = (int)position;
        double fraction = position - sector;
        byte rising = ToByte(fraction * 255);
        byte falling = ToByte((1 - fraction) * 255);
        return sector switch
        {
            0 => new(255, rising, 0),
            1 => new(falling, 255, 0),
            2 => new(0, 255, rising),
            3 => new(0, falling, 255),
            4 => new(rising, 0, 255),
            _ => new(255, 0, falling),
        };
    }

    private static byte ToByte(double value) => checked((byte)Math.Clamp(
        Math.Round(value, MidpointRounding.AwayFromZero),
        byte.MinValue,
        byte.MaxValue));
}
