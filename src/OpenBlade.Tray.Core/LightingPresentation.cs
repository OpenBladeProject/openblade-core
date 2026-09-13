using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public static class LightingPresentation
{
    public static string LightingEffectLabel(LightingEffect effect) => effect switch
    {
        LightingEffect.Off => TrayText.Get("Off"),
        LightingEffect.Static => TrayText.Get("Static"),
        LightingEffect.Spectrum => TrayText.Get("Spectrum Cycling"),
        LightingEffect.Breathing => TrayText.Get("Breathing"),
        LightingEffect.Reactive => TrayText.Get("Reactive"),
        LightingEffect.Tidal => TrayText.Get("Tidal"),
        LightingEffect.Starlight => TrayText.Get("Starlight"),
        LightingEffect.Wave => TrayText.Get("Wave"),
        LightingEffect.Wheel => TrayText.Get("Wheel"),
        LightingEffect.Ripple => TrayText.Get("Ripple"),
        LightingEffect.AmbientAwareness => TrayText.Get("Ambient Awareness"),
        LightingEffect.AudioMeter => TrayText.Get("Audio Meter"),
        LightingEffect.Fire => TrayText.Get("Fire"),
        LightingEffect.Sequence => TrayText.Get("Imported animation"),
        _ => effect.ToString(),
    };

    public static string LogoLightingModeLabel(LogoLightingMode mode) => mode switch
    {
        LogoLightingMode.Static => TrayText.Get("Static"),
        LogoLightingMode.Breathing => TrayText.Get("Breathing"),
        LogoLightingMode.Off => TrayText.Get("Off"),
        _ => mode.ToString(),
    };

    public static string AmbientAwarenessRegionLabel(
        AmbientAwarenessRegion region) => region switch
        {
            AmbientAwarenessRegion.Full => TrayText.Get("Full screen"),
            AmbientAwarenessRegion.Left => TrayText.Get("Left"),
            AmbientAwarenessRegion.Top => TrayText.Get("Top"),
            AmbientAwarenessRegion.Right => TrayText.Get("Right"),
            AmbientAwarenessRegion.Bottom => TrayText.Get("Bottom"),
            _ => region.ToString(),
        };

    private static string AmbientAwarenessRegionFragment(
        AmbientAwarenessRegion region) => region switch
        {
            AmbientAwarenessRegion.Full => TrayText.Get("Fragment.FullScreen", "full screen"),
            AmbientAwarenessRegion.Left => TrayText.Get("Fragment.Left", "left"),
            AmbientAwarenessRegion.Top => TrayText.Get("Fragment.Top", "top"),
            AmbientAwarenessRegion.Right => TrayText.Get("Fragment.Right", "right"),
            AmbientAwarenessRegion.Bottom => TrayText.Get("Fragment.Bottom", "bottom"),
            _ => region.ToString(),
        };

    private static string LightingDirectionLabel(LightingDirection direction) =>
        direction switch
        {
            LightingDirection.Leftward => TrayText.Get("Leftward"),
            LightingDirection.Rightward => TrayText.Get("Rightward"),
            _ => direction.ToString(),
        };

    private static string LightingDirectionFragment(LightingDirection direction) =>
        direction switch
        {
            LightingDirection.Leftward => TrayText.Get("Fragment.Leftward", "leftward"),
            LightingDirection.Rightward => TrayText.Get("Fragment.Rightward", "rightward"),
            _ => direction.ToString(),
        };

    private static string ReactiveLightingDurationLabel(
        ReactiveLightingDuration duration) => duration switch
        {
            ReactiveLightingDuration.Short => TrayText.Get("Short"),
            ReactiveLightingDuration.Medium => TrayText.Get("Medium"),
            ReactiveLightingDuration.Long => TrayText.Get("Long"),
            _ => duration.ToString(),
        };

    private static string ReactiveLightingDurationFragment(
        ReactiveLightingDuration duration) => duration switch
        {
            ReactiveLightingDuration.Short => TrayText.Get("Fragment.Short", "short"),
            ReactiveLightingDuration.Medium => TrayText.Get("Fragment.Medium", "medium"),
            ReactiveLightingDuration.Long => TrayText.Get("Fragment.Long", "long"),
            _ => duration.ToString(),
        };

    private static string StarlightLightingDurationLabel(
        StarlightLightingDuration duration) => duration switch
        {
            StarlightLightingDuration.Short => TrayText.Get("Short"),
            StarlightLightingDuration.Medium => TrayText.Get("Medium"),
            StarlightLightingDuration.Long => TrayText.Get("Long"),
            _ => duration.ToString(),
        };

    private static string StarlightLightingDurationFragment(
        StarlightLightingDuration duration) => duration switch
        {
            StarlightLightingDuration.Short => TrayText.Get("Fragment.Short", "short"),
            StarlightLightingDuration.Medium => TrayText.Get("Fragment.Medium", "medium"),
            StarlightLightingDuration.Long => TrayText.Get("Fragment.Long", "long"),
            _ => duration.ToString(),
        };

    public static string FormatLightingProfile(LightingSettings lighting)
    {
        if (lighting.Effect == LightingEffect.Off)
        {
            return TrayText.Get("Off");
        }

        string effect = LightingEffectLabel(lighting.Effect);
        string details = lighting.Parameters switch
        {
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Dual,
                SecondaryColor: { } secondary,
            } =>
                TrayText.Format(
                    "two colors, {0} and {1}",
                    FormatColor(lighting.Color),
                    FormatColor(secondary)),
            BreathingLightingParameters { ColorMode: BreathingColorMode.Random } =>
                TrayText.Get("random colors"),
            BreathingLightingParameters => FormatColor(lighting.Color),
            ReactiveLightingParameters reactive =>
                TrayText.Format(
                    "{0}, {1}",
                    ReactiveLightingDurationFragment(reactive.Duration),
                    FormatColor(lighting.Color)),
            RippleLightingParameters => FormatColor(lighting.Color),
            TidalLightingParameters { RandomColors: true } tidal =>
                TrayText.Format(
                    "random colors, {0}",
                    LightingDirectionFragment(tidal.Direction)),
            TidalLightingParameters tidal =>
                TrayText.Format(
                    "{0} and {1}, {2}",
                    FormatColor(tidal.PrimaryColor),
                    FormatColor(tidal.SecondaryColor),
                    LightingDirectionFragment(tidal.Direction)),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Random,
            } starlight =>
                TrayText.Format(
                    "random colors, {0}",
                    StarlightLightingDurationFragment(starlight.Duration)),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Dual,
                SecondaryColor: { } secondary,
            } starlight =>
                TrayText.Format(
                    "{0} and {1}, {2}",
                    FormatColor(lighting.Color),
                    FormatColor(secondary),
                    StarlightLightingDurationFragment(starlight.Duration)),
            StarlightLightingParameters starlight =>
                TrayText.Format(
                    "{0}, {1}",
                    FormatColor(lighting.Color),
                    StarlightLightingDurationFragment(starlight.Duration)),
            WaveLightingParameters wave =>
                LightingDirectionFragment(wave.Direction),
            WheelLightingParameters wheel =>
                FormatRotationDirection(wheel.Direction),
            AmbientAwarenessLightingParameters ambient =>
                ambient.Region == AmbientAwarenessRegion.Full
                    ? TrayText.Get("Fragment.FullScreen", "full screen")
                    : AmbientAwarenessRegionFragment(ambient.Region),
            AudioMeterLightingParameters audioMeter =>
                TrayText.Format(
                    "color boost {0}, smoothing: {1}",
                    audioMeter.ColorBoost,
                    FormatAudioMeterDecay(audioMeter.DecayMilliseconds)),
            _ when lighting.Effect == LightingEffect.Static => FormatColor(lighting.Color),
            _ => string.Empty,
        };
        return string.IsNullOrEmpty(details)
            ? TrayText.Format("{0} | {1}%", effect, lighting.Brightness)
            : TrayText.Format("{0} | {1}% | {2}", effect, lighting.Brightness, details);
    }

    private static string FormatColor(RgbColor color) =>
        $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    public static string FormatAudioMeterDecay(int milliseconds) => milliseconds switch
    {
        0 => TrayText.Get("Off"),
        1000 => TrayText.Get("Up to 1 second"),
        _ => TrayText.Format("Up to {0:0.##} seconds", milliseconds / 1000d),
    };

    public static string FormatLightingEffect(LightingSettings lighting) => lighting switch
    {
        { Effect: LightingEffect.Off } => TrayText.Get("Off"),
        { Effect: LightingEffect.Spectrum } => TrayText.Get("Spectrum Cycling"),
        {
            Effect: LightingEffect.AudioMeter,
            Parameters: AudioMeterLightingParameters audioMeter,
        } => TrayText.Format(
            "Audio Meter, color boost {0}, smoothing: {1}",
            audioMeter.ColorBoost,
            FormatAudioMeterDecay(audioMeter.DecayMilliseconds)),
        {
            Effect: LightingEffect.AmbientAwareness,
            Parameters: AmbientAwarenessLightingParameters ambient,
        } => ambient.Region == AmbientAwarenessRegion.Full
            ? TrayText.Get("Ambient Awareness, full screen")
            : TrayText.Format(
                "Ambient Awareness, {0}",
                AmbientAwarenessRegionFragment(ambient.Region)),
        {
            Effect: LightingEffect.Breathing,
            Parameters: BreathingLightingParameters { ColorMode: BreathingColorMode.Dual },
        } => TrayText.Get("Breathing, two colors"),
        {
            Effect: LightingEffect.Breathing,
            Parameters: BreathingLightingParameters { ColorMode: BreathingColorMode.Random },
        } => TrayText.Get("Breathing, random colors"),
        { Effect: LightingEffect.Breathing } => TrayText.Get("Breathing, one color"),
        {
            Effect: LightingEffect.Reactive,
            Parameters: ReactiveLightingParameters reactive,
        } => TrayText.Format(
            "Reactive, {0}",
            ReactiveLightingDurationFragment(reactive.Duration)),
        { Effect: LightingEffect.Ripple } => TrayText.Get("Ripple"),
        {
            Effect: LightingEffect.Tidal,
            Parameters: TidalLightingParameters tidal,
        } => tidal.Direction == LightingDirection.Leftward
            ? TrayText.Get("Tidal, outward")
            : TrayText.Get("Tidal, inward"),
        {
            Effect: LightingEffect.Starlight,
            Parameters: StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Dual,
            } starlight,
        } => TrayText.Format(
            "Starlight, two colors, {0}",
            StarlightLightingDurationFragment(starlight.Duration)),
        {
            Effect: LightingEffect.Starlight,
            Parameters: StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Random,
            } starlight,
        } => TrayText.Format(
            "Starlight, random colors, {0}",
            StarlightLightingDurationFragment(starlight.Duration)),
        {
            Effect: LightingEffect.Starlight,
            Parameters: StarlightLightingParameters starlight,
        } => TrayText.Format(
            "Starlight, one color, {0}",
            StarlightLightingDurationFragment(starlight.Duration)),
        {
            Effect: LightingEffect.Wave,
            Parameters: WaveLightingParameters wave,
        } => TrayText.Format(
            "Wave, {0}",
            LightingDirectionFragment(wave.Direction)),
        {
            Effect: LightingEffect.Wheel,
            Parameters: WheelLightingParameters wheel,
        } => TrayText.Format("Wheel, {0}", FormatRotationDirection(wheel.Direction)),
        _ => LightingEffectLabel(lighting.Effect),
    };

    public static string FormatLightingColor(LightingSettings lighting) => lighting switch
    {
        { Effect: LightingEffect.Off } => TrayText.Get("Not applicable"),
        { Effect: LightingEffect.Spectrum } => TrayText.Get("Automatic"),
        {
            Effect: LightingEffect.Wave or LightingEffect.Wheel
                or LightingEffect.AmbientAwareness
                or LightingEffect.AudioMeter
                or LightingEffect.Fire
                or LightingEffect.Sequence,
        } => TrayText.Get("Automatic"),
        {
            Parameters: BreathingLightingParameters { ColorMode: BreathingColorMode.Random },
        } => TrayText.Get("Random"),
        {
            Parameters: TidalLightingParameters { RandomColors: true },
        } => TrayText.Get("Random"),
        {
            Parameters: TidalLightingParameters tidal,
        } => TrayText.Format(
            "{0} and {1}",
            FormatColor(tidal.PrimaryColor),
            FormatColor(tidal.SecondaryColor)),
        {
            Parameters: StarlightLightingParameters { ColorMode: StarlightColorMode.Random },
        } => TrayText.Get("Random"),
        {
            Parameters: StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Dual,
                SecondaryColor: { } secondary,
            },
        } => TrayText.Format(
            "{0} and {1}",
            FormatColor(lighting.Color),
            FormatColor(secondary)),
        {
            Parameters: BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Dual,
                SecondaryColor: { } secondary,
            },
        } => TrayText.Format(
            "{0} and {1}",
            FormatColor(lighting.Color),
            FormatColor(secondary)),
        _ => FormatColor(lighting.Color),
    };

    private static string FormatRotationDirection(LightingRotationDirection direction) =>
        direction == LightingRotationDirection.Counterclockwise
            ? TrayText.Get("counterclockwise")
            : TrayText.Get("clockwise");

}
