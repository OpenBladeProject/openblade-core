using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class ProfilePresentationFactoryTests : IDisposable
{
    private readonly CultureScope culture = new("en-US");

    public void Dispose() => culture.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_DisplayOffAlwaysShowsEffectivePolicy(bool legacyTurnOffWithDisplay)
    {
        PowerProfile profile = CreateProfile() with
        {
            LightingPowerPolicy = new LightingPowerPolicy(legacyTurnOffWithDisplay, null),
        };

        Assert.Equal("Turn lighting off", Create(profile).LightingDisplayOff.Text);
        Assert.Equal(legacyTurnOffWithDisplay, profile.EffectiveLightingPowerPolicy.TurnOffWhenDisplayTurnsOff);
    }

    [Theory]
    [InlineData(null, null, "Not set", "Not set")]
    [InlineData(" ", " ", "Not set", "Not set")]
    [InlineData("fixed-240", "sRGB.icm", "240 Hz", "sRGB")]
    [InlineData("FIXED-60", "DCI_P3.icc", "60 Hz", "DCI-P3")]
    [InlineData("dynamic-48-240", "Adobe-RGB.icm", "Dynamic 48–240 Hz", "Adobe RGB")]
    [InlineData("drr-60-120", "rec709.icc", "Dynamic 60–120 Hz", "Rec. 709")]
    [InlineData("fixed-fast", "native.icm", "fixed-fast", "Native")]
    [InlineData("custom-refresh", @"C:\profiles\My_Profile.icm", "custom-refresh", "My Profile")]
    public void Create_FormatsDisplayKeysAndPreservesUnknownRefreshKeys(
        string? refreshRateKey,
        string? colorProfileKey,
        string expectedRefreshRate,
        string expectedColorProfile)
    {
        PowerProfile profile = CreateProfile() with
        {
            Display = new DisplayProfileSettings(refreshRateKey, false, colorProfileKey),
        };

        ProfilePresentation result = Create(profile);

        Assert.Equal(expectedRefreshRate, result.DisplayRefreshRate.Text);
        Assert.Equal(expectedColorProfile, result.DisplayColorProfile.Text);
        Assert.Equal("Off", result.DisplayHdr.Text);
    }

    [Fact]
    public void Create_UsesLaptopFallbackOnlyForUnsetFirmwareFields()
    {
        var fallback = new LaptopBehaviorProfileSettings(
            StartupAnimationEnabled: true,
            FunctionKeyPrimaryMode.MultimediaKeys,
            GamingModeEnabled: true)
        {
            GamingModeKeyPolicy = new GamingModeKeyPolicy(true, true),
        };
        PowerProfile profile = CreateProfile() with
        {
            LaptopBehavior = new LaptopBehaviorProfileSettings(
                StartupAnimationEnabled: false,
                FunctionKeyPrimary: null,
                GamingModeEnabled: false),
        };

        ProfilePresentation result = ProfilePresentationFactory.Create(
            profile,
            WindowsPowerModeAssociations.LeaveUnchanged,
            "AC",
            fallback);

        Assert.Equal("Off", result.StartupAnimation.Text);
        Assert.Equal("Media keys", result.FunctionKeyBehavior.Text);
        Assert.Equal("Off", result.GamingMode.Text);
        Assert.Equal("Off", result.GamingModeWindowsKey.Text);
        Assert.Equal("Off", result.GamingModeAltTab.Text);

        ProfilePresentation unconfigured = ProfilePresentationFactory.Create(
            CreateProfile(),
            WindowsPowerModeAssociations.LeaveUnchanged,
            "AC",
            fallback);

        Assert.Equal("On", unconfigured.StartupAnimation.Text);
        Assert.Equal("Media keys", unconfigured.FunctionKeyBehavior.Text);
        Assert.Equal("On", unconfigured.GamingMode.Text);
        Assert.Equal("Off", unconfigured.GamingModeWindowsKey.Text);
        Assert.Equal("Off", unconfigured.GamingModeAltTab.Text);
    }

    [Fact]
    public void Create_UsesSavedGamingKeyPolicyEvenWhenGamingModeIsOff()
    {
        PowerProfile profile = CreateProfile() with
        {
            LaptopBehavior = new LaptopBehaviorProfileSettings(null, null, false)
            {
                GamingModeKeyPolicy = new GamingModeKeyPolicy(true, true),
            },
        };

        ProfilePresentation result = Create(profile);

        Assert.Equal("Off", result.GamingMode.Text);
        Assert.Equal("On", result.GamingModeWindowsKey.Text);
        Assert.Equal("On", result.GamingModeAltTab.Text);
    }

    [Fact]
    public void Create_PrefersExplicitGpuTuningAndKeepsLegacyOffsetsDormantOutsideCustom()
    {
        PowerProfile profile = CreateProfile() with
        {
            Performance = PerformancePreset.Custom,
            CustomPerformance = new CustomPerformanceSettings(
                CustomPerformanceLevel.Low,
                CustomPerformanceLevel.High)
            {
                GpuClockOffsets = new GpuClockOffsetSettings(100, -50),
            },
        };

        Assert.Equal("Custom, CPU Low, GPU High", Create(profile).PowerMode.Text);
        Assert.Equal("Core +100 MHz, memory -50 MHz", Create(profile).GpuTuning.Text);
        Assert.Equal(
            "Not set",
            Create(profile with { Performance = PerformancePreset.Balanced }).GpuTuning.Text);
        Assert.Equal(
            "Off",
            Create(profile with { GpuClockTuning = GpuClockTuningSettings.Disabled }).GpuTuning.Text);
        Assert.Equal(
            "Core 0 MHz, memory -200 MHz",
            Create(profile with
            {
                Performance = PerformancePreset.Balanced,
                GpuClockTuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(0, -200)),
            }).GpuTuning.Text);
    }

    [Fact]
    public void WithPending_IgnoresHiddenGpuOffsetsAndEquivalentDisplayText()
    {
        PowerProfile profile = CreateProfile() with
        {
            GpuClockTuning = new GpuClockTuningSettings(false, new GpuClockOffsetSettings(100, 200)),
            Display = new DisplayProfileSettings("fixed-60", null, "sRGB.icm"),
        };
        PowerProfile draft = profile with
        {
            GpuClockTuning = GpuClockTuningSettings.Disabled,
            Display = new DisplayProfileSettings("FIXED-60", null, "srgb.icc"),
        };

        ProfilePresentation result = ProfilePresentationFactory.WithPending(
            Create(profile),
            Create(draft),
            fanControlChanged: false);

        Assert.Equal("Off", result.GpuTuning.Text);
        Assert.Equal("Off", result.GpuTuning.PendingText);
        Assert.False(result.GpuTuning.IsPending);
        Assert.Null(result.GpuTuning.PendingDescription);
        Assert.Equal("AC GPU tuning: Off", result.GpuTuning.AccessibilityDescription);
        Assert.False(result.DisplayRefreshRate.IsPending);
        Assert.False(result.DisplayColorProfile.IsPending);
    }

    [Fact]
    public void WithPending_UsesStructuralFanChangeEvenWhenManualCurveLabelsMatch()
    {
        PowerProfile profile = CreateProfile() with
        {
            Fans = FanMode.ManualCurve,
            FanControl = new FanControlSettings(FanMode.ManualCurve, 3700, ManualFanCurve.Default),
        };
        var changedCurve = new ManualFanCurve(
            ManualFanCurve.Default.Cpu,
            ManualFanCurve.Default.Gpu,
            useHigherTemperatureForBoth: false);
        PowerProfile draft = profile with
        {
            FanControl = new FanControlSettings(FanMode.ManualCurve, 3700, changedCurve),
        };
        ProfilePresentation saved = Create(profile);
        ProfilePresentation pending = Create(draft);

        Assert.Equal(saved.Fans, pending.Fans);
        ProfilePresentation result = ProfilePresentationFactory.WithPending(
            saved,
            pending,
            fanControlChanged: true);

        Assert.Equal("Manual fan curve", result.Fans.Text);
        Assert.Equal("Manual fan curve", result.Fans.PendingText);
        Assert.True(result.Fans.IsPending);
        Assert.Equal("Pending: Manual fan curve", result.Fans.PendingDescription);
        Assert.Equal(
            "AC Fans: Manual fan curve. Pending: Manual fan curve",
            result.Fans.AccessibilityDescription);
        Assert.False(ProfilePresentationFactory.WithPending(
            saved,
            pending,
            fanControlChanged: false).Fans.IsPending);
    }

    [Fact]
    public void WithPending_PreservesSavedTextAndUsesSuppliedWindowsAssociations()
    {
        PowerProfile profile = CreateProfile() with
        {
            WindowsPowerMode = WindowsPowerMode.BestPowerEfficiency,
        };
        WindowsPowerModeAssociations modes = WindowsPowerModeAssociations.LeaveUnchanged with
        {
            Silent = WindowsPowerMode.BestPerformance,
        };
        ProfilePresentation saved = Create(profile);
        ProfilePresentation pending = ProfilePresentationFactory.Create(
            profile with { Performance = PerformancePreset.Silent },
            modes,
            "AC",
            LaptopBehaviorProfileSettings.Unconfigured());

        ProfilePresentation result = ProfilePresentationFactory.WithPending(
            saved,
            pending,
            fanControlChanged: false);

        Assert.Equal("Balanced", result.PowerMode.Text);
        Assert.Equal("Silent", result.PowerMode.PendingText);
        Assert.True(result.PowerMode.IsPending);
        Assert.Equal("Leave unchanged", result.WindowsPowerMode.Text);
        Assert.Equal("Best performance", result.WindowsPowerMode.PendingText);
        Assert.True(result.WindowsPowerMode.IsPending);
        Assert.Equal(
            "AC Windows power mode: Leave unchanged. Pending: Best performance",
            result.WindowsPowerMode.AccessibilityDescription);
        Assert.False(saved.PowerMode.IsPending);
        Assert.Equal(string.Empty, saved.PowerMode.PendingText);
    }

    [Fact]
    public void Summaries_PreserveRawDisplayKeysAndFriendlyLightingDetails()
    {
        PowerProfile profile = CreateProfile() with
        {
            Performance = PerformancePreset.Custom,
            CustomPerformance = new CustomPerformanceSettings(
                CustomPerformanceLevel.Low,
                CustomPerformanceLevel.High),
            Lighting = LightingSettingsFactory.BreathingDual(
                new RgbColor(1, 2, 3),
                new RgbColor(171, 205, 239),
                73),
            Display = new DisplayProfileSettings("drr-48-240", false, "DCI_P3.icm"),
        };
        WindowsPowerModeAssociations modes = WindowsPowerModeAssociations.LeaveUnchanged with
        {
            Custom = WindowsPowerMode.BestPerformance,
        };

        Assert.Equal(
            "Custom, CPU Low, GPU High | Automatic fans",
            ProfilePresentationFactory.FormatThermalProfile(profile));
        Assert.Equal(
            "Breathing | 73% | two colors, #010203 and #ABCDEF",
            LightingPresentation.FormatLightingProfile(profile.Lighting));
        Assert.Equal(
            "drr-48-240 | HDR off | DCI_P3.icm",
            ProfilePresentationFactory.FormatDisplayProfile(profile.Display));
        Assert.Equal(
            string.Join(Environment.NewLine,
                "Laptop performance: Custom, CPU Low, GPU High",
                "Windows power mode: Best performance",
                "Fans: Automatic",
                "Lighting: Breathing | 73% | two colors, #010203 and #ABCDEF",
                "Display: drr-48-240 | HDR off | DCI_P3.icm"),
            ProfilePresentationFactory.FormatPowerProfile(profile, modes));
        Assert.Equal("Breathing, two colors", Create(profile).LightingEffect.Text);
        Assert.Equal("#010203 and #ABCDEF", Create(profile).LightingColor.Text);
    }

    [Fact]
    public void Create_RecomputesLocalizedTextAndRetainsRecordValueEquality()
    {
        PowerProfile profile = CreateProfile();
        ProfilePresentation english = Create(profile);

        Assert.Equal(english, Create(profile with { }));
        Assert.Equal(english.GetHashCode(), Create(profile).GetHashCode());
        using (new CultureScope("qps-ploc"))
        {
            ProfilePresentation pseudo = Create(profile);
            Assert.NotEqual(english, pseudo);
            Assert.StartsWith("[!! ", pseudo.PowerMode.Text, StringComparison.Ordinal);
            Assert.StartsWith("[!! ", pseudo.PowerMode.SettingName, StringComparison.Ordinal);
        }
        Assert.Equal(english, Create(profile));
    }

    [Fact]
    public void Create_PreservesUnknownLogoAndWindowsModeFallbacks()
    {
        PowerProfile profile = CreateProfile() with
        {
            Lighting = LightingSettingsFactory.Off() with
            {
                LogoMode = (LogoLightingMode)999,
            },
        };
        WindowsPowerModeAssociations modes = WindowsPowerModeAssociations.LeaveUnchanged with
        {
            Balanced = (WindowsPowerMode)999,
        };

        ProfilePresentation result = ProfilePresentationFactory.Create(
            profile,
            modes,
            "AC",
            LaptopBehaviorProfileSettings.Unconfigured());

        Assert.Equal("Not set", result.LogoLighting.Text);
        Assert.Equal("Unknown", result.WindowsPowerMode.Text);
        Assert.Equal("Not set", Create(CreateProfile()).LogoLighting.Text);
        Assert.Equal("Leave unchanged", Create(CreateProfile()).WindowsPowerMode.Text);
    }

    private static PowerProfile CreateProfile() => new(
        PerformancePreset.Balanced,
        FanMode.Automatic,
        LightingSettingsFactory.Off());

    private static ProfilePresentation Create(PowerProfile profile) =>
        ProfilePresentationFactory.Create(
            profile,
            WindowsPowerModeAssociations.LeaveUnchanged,
            "AC",
            LaptopBehaviorProfileSettings.Unconfigured());
}
