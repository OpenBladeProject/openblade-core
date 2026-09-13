using System.Globalization;
using System.IO;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public static class ProfilePresentationFactory
{
    public static ProfilePresentation Create(
        PowerProfile profile,
        WindowsPowerModeAssociations windowsPowerModes,
        string profileName,
        LaptopBehaviorProfileSettings laptopBehaviorFallback)
    {
        ProfileValuePresentation Value(string settingName, string text) =>
            new(text, settingName, profileName);

        DisplayProfileSettings display = profile.Display ?? DisplayProfileSettings.Unconfigured();
        LaptopBehaviorProfileSettings savedLaptopBehavior =
            profile.EffectiveLaptopBehavior;
        bool? presentedStartupAnimation = savedLaptopBehavior.StartupAnimationEnabled
            ?? laptopBehaviorFallback.StartupAnimationEnabled;
        FunctionKeyPrimaryMode? presentedFunctionKeyPrimary =
            savedLaptopBehavior.FunctionKeyPrimary
            ?? laptopBehaviorFallback.FunctionKeyPrimary;
        bool? presentedGamingMode = savedLaptopBehavior.GamingModeEnabled
            ?? laptopBehaviorFallback.GamingModeEnabled;
        return new ProfilePresentation(
            Value(TrayText.Get("Laptop performance"), FormatPerformanceProfile(profile)),
            Value(
                TrayText.Get("Windows power mode"),
                FormatWindowsPowerMode(windowsPowerModes.ForPerformance(profile.Performance))),
            Value(TrayText.Get("GPU tuning"), FormatGpuClockOffsets(profile)),
            Value(TrayText.Get("Fans"), FormatFanControl(profile)),
            Value(
                TrayText.Get("Battery protection"),
                FormatOptionalToggle(profile.BatteryProtectionEnabled)),
            Value(TrayText.Get("Lighting effect"), LightingPresentation.FormatLightingEffect(profile.Lighting)),
            Value(TrayText.Get("Lighting brightness"), profile.Lighting.Effect == LightingEffect.Off
                ? TrayText.Get("Not applicable")
                : TrayText.Format("{0}%", profile.Lighting.Brightness)),
            Value(TrayText.Get("Lighting color"), LightingPresentation.FormatLightingColor(profile.Lighting)),
            Value(TrayText.Get("Laptop lid logo"), profile.Lighting.LogoMode switch
            {
                LogoLightingMode.Static => TrayText.Get("Static"),
                LogoLightingMode.Breathing => TrayText.Get("Breathing"),
                LogoLightingMode.Off => TrayText.Get("Off"),
                null => TrayText.Get("Not set"),
                _ => TrayText.Get("Not set"),
            }),
            Value(TrayText.Get("Display turns off"), profile.EffectiveLightingPowerPolicy.TurnOffWhenDisplayTurnsOff
                ? TrayText.Get("Turn lighting off")
                : TrayText.Get("Keep lighting on")),
            Value(TrayText.Get("Inactivity timeout"), profile.EffectiveLightingPowerPolicy.IdleTimeoutMinutes is int minutes
                ? TrayText.Format("{0} minutes", minutes)
                : TrayText.Get("Never")),
            Value(TrayText.Get("Refresh rate"), FormatDisplayRefreshRate(display.RefreshRateKey)),
            Value(TrayText.Get("HDR"), display.HdrEnabled switch
            {
                true => TrayText.Get("On"),
                false => TrayText.Get("Off"),
                null => TrayText.Get("Not set"),
            }),
            Value(TrayText.Get("Color profile"), FormatDisplayColorProfile(display.ColorProfileKey)),
            Value(TrayText.Get("Function keys"), presentedFunctionKeyPrimary switch
            {
                FunctionKeyPrimaryMode.FunctionKeys => TrayText.Get("Function keys"),
                FunctionKeyPrimaryMode.MultimediaKeys => TrayText.Get("Media keys"),
                _ => TrayText.Get("Not set"),
            }),
            Value(TrayText.Get("Gaming mode"), FormatOptionalToggle(presentedGamingMode)),
            Value(TrayText.Get("Block Windows key"), FormatOptionalToggle(
                profile.EffectiveLaptopBehavior
                    .EffectiveGamingModeKeyPolicy
                    .BlockWindowsKey)),
            Value(TrayText.Get("Block Alt+Tab"), FormatOptionalToggle(
                profile.EffectiveLaptopBehavior
                    .EffectiveGamingModeKeyPolicy
                    .BlockAltTab)),
            Value(
                TrayText.Get("Startup animation"),
                FormatOptionalToggle(presentedStartupAnimation)));
    }

    private static string FormatOptionalToggle(bool? value) => value switch
    {
        true => TrayText.Get("On"),
        false => TrayText.Get("Off"),
        null => TrayText.Get("Not set"),
    };

    private static string FormatFanControl(PowerProfile profile)
    {
        FanControlSettings settings = profile.FanControl
            ?? FanControlSettings.FromLegacy(profile.Fans);
        return settings.Mode switch
        {
            FanMode.Automatic => TrayText.Get("Automatic"),
            FanMode.Fixed =>
                TrayText.Format("Fixed, {0:N0} RPM", settings.FixedSpeedRpm),
            FanMode.ManualCurve => TrayText.Get("Manual fan curve"),
            FanMode.Maximum => TrayText.Get("Maximum"),
            _ => TrayText.Get(settings.Mode.ToString()),
        };
    }

    private static string FormatPerformanceProfile(PowerProfile profile) =>
        profile is
        {
            Performance: PerformancePreset.Custom,
            CustomPerformance: { } custom,
        }
            ? TrayText.Format(
                "Custom, CPU {0}, GPU {1}",
                CustomPerformanceLevelLabel(custom.Cpu),
                CustomPerformanceLevelLabel(custom.Gpu))
            : PerformancePresetPresentation.Label(profile.Performance);

    private static string FormatGpuClockOffsets(PowerProfile profile) =>
        ResolveProfileGpuClockTuning(profile) switch
        {
            { Enabled: true, Offsets: { } offsets } =>
                TrayText.Format(
                    "Core {0}, memory {1}",
                    FormatSignedMegahertz(offsets.CoreMegahertz),
                    FormatSignedMegahertz(offsets.MemoryMegahertz)),
            { Enabled: false } => TrayText.Get("Off"),
            _ => TrayText.Get("Not set"),
        };

    private static GpuClockTuningSettings? ResolveProfileGpuClockTuning(
        PowerProfile profile) => profile.GpuClockTuning
        ?? (profile.Performance == PerformancePreset.Custom
            && profile.CustomPerformance?.GpuClockOffsets is { } legacyOffsets
                ? new GpuClockTuningSettings(Enabled: true, legacyOffsets)
                : null);

    private static string FormatSignedMegahertz(int value) =>
        TrayText.Format("{0} MHz", value.ToString("+0;-0;0", CultureInfo.CurrentCulture));

    public static ProfilePresentation WithPending(
        ProfilePresentation saved,
        ProfilePresentation pending,
        bool fanControlChanged)
    {
        return new ProfilePresentation(
            MarkPending(saved.PowerMode, pending.PowerMode),
            MarkPending(saved.WindowsPowerMode, pending.WindowsPowerMode),
            MarkPending(saved.GpuTuning, pending.GpuTuning),
            MarkPending(
                saved.Fans,
                pending.Fans,
                fanControlChanged),
            MarkPending(saved.BatteryProtection, pending.BatteryProtection),
            MarkPending(saved.LightingEffect, pending.LightingEffect),
            MarkPending(saved.LightingBrightness, pending.LightingBrightness),
            MarkPending(saved.LightingColor, pending.LightingColor),
            MarkPending(saved.LogoLighting, pending.LogoLighting),
            MarkPending(saved.LightingDisplayOff, pending.LightingDisplayOff),
            MarkPending(saved.LightingIdle, pending.LightingIdle),
            MarkPending(saved.DisplayRefreshRate, pending.DisplayRefreshRate),
            MarkPending(saved.DisplayHdr, pending.DisplayHdr),
            MarkPending(saved.DisplayColorProfile, pending.DisplayColorProfile),
            MarkPending(saved.FunctionKeyBehavior, pending.FunctionKeyBehavior),
            MarkPending(saved.GamingMode, pending.GamingMode),
            MarkPending(saved.GamingModeWindowsKey, pending.GamingModeWindowsKey),
            MarkPending(saved.GamingModeAltTab, pending.GamingModeAltTab),
            MarkPending(saved.StartupAnimation, pending.StartupAnimation));
    }

    public static ProfileValuePresentation MarkPending(
        ProfileValuePresentation saved,
        ProfileValuePresentation pending,
        bool isPending = false) =>
        saved with
        {
            IsPending = isPending
                || !string.Equals(saved.Text, pending.Text, StringComparison.Ordinal),
            PendingText = pending.Text,
        };

    public static string FormatThermalProfile(PowerProfile profile) =>
        TrayText.Format(
            "{0} | {1} fans",
            FormatPerformanceProfile(profile),
            FanModePresentation.Label(profile.Fans));

    public static string FormatPowerProfile(
        PowerProfile profile,
        WindowsPowerModeAssociations windowsPowerModes) =>
        TrayText.Format(
            "Laptop performance: {0}{5}Windows power mode: {1}{5}Fans: {2}{5}Lighting: {3}{5}Display: {4}",
            FormatPerformanceProfile(profile),
            FormatWindowsPowerMode(windowsPowerModes.ForPerformance(profile.Performance)),
            FanModePresentation.Label(profile.Fans),
            LightingPresentation.FormatLightingProfile(profile.Lighting),
            FormatDisplayProfile(profile.Display),
            Environment.NewLine);

    private static string FormatWindowsPowerMode(WindowsPowerMode? mode) => mode switch
    {
        WindowsPowerMode.BestPowerEfficiency => TrayText.Get("Best power efficiency"),
        WindowsPowerMode.Balanced => TrayText.Get("Balanced"),
        WindowsPowerMode.BestPerformance => TrayText.Get("Best performance"),
        null => TrayText.Get("Leave unchanged"),
        _ => TrayText.Get("Unknown"),
    };

    public static string FormatDisplayProfile(DisplayProfileSettings? display)
    {
        DisplayProfileSettings value = display ?? DisplayProfileSettings.Unconfigured();
        List<string> settings = [];
        if (!string.IsNullOrWhiteSpace(value.RefreshRateKey))
        {
            settings.Add(value.RefreshRateKey);
        }
        if (value.HdrEnabled is bool hdrEnabled)
        {
            settings.Add(hdrEnabled ? TrayText.Get("HDR on") : TrayText.Get("HDR off"));
        }
        if (!string.IsNullOrWhiteSpace(value.ColorProfileKey))
        {
            settings.Add(value.ColorProfileKey);
        }
        return settings.Count == 0
            ? TrayText.Get("Not set")
            : string.Join(TrayText.Get(" | "), settings);
    }

    private static string FormatDisplayRefreshRate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TrayText.Get("Not set");
        }

        string[] parts = key.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && string.Equals(parts[0], "fixed", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int fixedHertz))
        {
            return TrayText.Format("{0} Hz", fixedHertz);
        }
        if (parts.Length == 3
            && (string.Equals(parts[0], "dynamic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[0], "drr", StringComparison.OrdinalIgnoreCase))
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int minimumHertz)
            && int.TryParse(parts[2], CultureInfo.InvariantCulture, out int maximumHertz))
        {
            return TrayText.Format(
                "Dynamic {0}–{1} Hz",
                minimumHertz,
                maximumHertz);
        }

        return key;
    }

    private static string FormatDisplayColorProfile(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TrayText.Get("Not set");
        }

        string fileName = Path.GetFileNameWithoutExtension(key);
        string normalized = fileName.Replace("_", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.ToLowerInvariant() switch
        {
            "srgb" => "sRGB",
            "dci p3" or "dcip3" => "DCI-P3",
            "adobe rgb" or "adobergb" => "Adobe RGB",
            "rec 709" or "rec709" => "Rec. 709",
            "native" => TrayText.Get("Native"),
            _ => string.IsNullOrWhiteSpace(normalized) ? key : normalized,
        };
    }

    public static string CustomPerformanceLevelLabel(
        CustomPerformanceLevel level) => level switch
        {
            CustomPerformanceLevel.Low => TrayText.Get("Low"),
            CustomPerformanceLevel.Medium => TrayText.Get("Medium"),
            CustomPerformanceLevel.High => TrayText.Get("High"),
            _ => level.ToString(),
        };

}
