using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class LongBatteryLifePresetTests
{
    [Fact]
    public void Preview_UsesLowestSupportedBatterySettingsWithoutMutatingBase()
    {
        PowerProfile original = CreateProfile() with
        {
            LaptopBehavior = new LaptopBehaviorProfileSettings(true, null, false),
            BatteryProtectionEnabled = false,
        };
        DeviceCapabilities capabilities = Capabilities(
            new HashSet<PerformancePreset>
            {
                PerformancePreset.Performance,
                PerformancePreset.BatterySaver,
            },
            new HashSet<FanMode> { FanMode.Fixed, FanMode.Automatic },
            new HashSet<LightingEffect> { LightingEffect.Static, LightingEffect.Off });
        DisplaySettingsSnapshot display = Display(
            [
                new DisplayRefreshRateOption("fixed-240", "240 Hz"),
                new DisplayRefreshRateOption("dynamic-48-240", "Dynamic", true),
                new DisplayRefreshRateOption("fixed-60", "60 Hz"),
            ],
            [0, 50, 20, 80]);

        LongBatteryLifePresetPlan plan = LongBatteryLifePreset.Preview(
            original,
            capabilities,
            display);

        Assert.Equal(PerformancePreset.Performance, original.Performance);
        Assert.Equal(FanMode.Fixed, original.Fans);
        Assert.Equal(LightingEffect.Static, original.Lighting.Effect);
        Assert.Equal("fixed-240", original.Display?.RefreshRateKey);
        Assert.Equal(PerformancePreset.BatterySaver, plan.Candidate.Performance);
        Assert.Equal(FanMode.Automatic, plan.Candidate.Fans);
        Assert.Equal(FanMode.Automatic, plan.Candidate.FanControl?.Mode);
        Assert.Equal(LightingEffect.Off, plan.Candidate.Lighting.Effect);
        Assert.Equal("fixed-60", plan.Candidate.Display?.RefreshRateKey);
        Assert.Equal((byte)20, plan.Candidate.Display?.BrightnessPercent);
        Assert.Same(original.LaptopBehavior, plan.Candidate.LaptopBehavior);
        Assert.Equal(original.BatteryProtectionEnabled, plan.Candidate.BatteryProtectionEnabled);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void Preview_PreservesEveryUnavailableOrUnsupportedField()
    {
        PowerProfile original = CreateProfile();
        DeviceCapabilities capabilities = Capabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Performance },
            new HashSet<FanMode> { FanMode.Fixed },
            new HashSet<LightingEffect> { LightingEffect.Static });

        LongBatteryLifePresetPlan plan = LongBatteryLifePreset.Preview(
            original,
            capabilities,
            display: null);

        Assert.Same(original, plan.Original);
        Assert.Equal(original, plan.Candidate);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void Preview_IgnoresUnrecognizedAndDynamicRefreshKeys()
    {
        PowerProfile original = CreateProfile();
        DisplaySettingsSnapshot display = Display(
            [
                new DisplayRefreshRateOption("dynamic-48-240", "Dynamic", true),
                new DisplayRefreshRateOption("other", "Unknown"),
            ],
            []);

        LongBatteryLifePresetPlan plan = LongBatteryLifePreset.Preview(
            original,
            Capabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Performance },
                new HashSet<FanMode> { FanMode.Fixed },
                new HashSet<LightingEffect> { LightingEffect.Static }),
            display);

        Assert.Equal(original.Display, plan.Candidate.Display);
    }

    [Fact]
    public void Preview_DoesNotIntroduceAnAcOnlyPerformanceModeOnBattery()
    {
        PowerProfile original = CreateProfile();
        DeviceCapabilities capabilities = Capabilities(new HashSet<PerformancePreset> { PerformancePreset.Silent },
            new HashSet<FanMode>(), new HashSet<LightingEffect>());
        LongBatteryLifePresetPlan plan = LongBatteryLifePreset.Preview(original, capabilities, null);
        Assert.Equal(original.Performance, plan.Candidate.Performance);
    }

    [Theory]
    [InlineData(true, 100)]
    [InlineData(false, 80)]
    public void Preview_DoesNotChooseAZeroBrightnessPreset(bool hasUsableLevel, byte expected)
    {
        PowerProfile original = CreateProfile();
        LongBatteryLifePresetPlan plan = LongBatteryLifePreset.Preview(original,
            Capabilities(new HashSet<PerformancePreset>(), new HashSet<FanMode>(), new HashSet<LightingEffect>()),
            Display([], hasUsableLevel ? [0, 100] : [0]));
        Assert.Equal(expected, plan.Candidate.Display?.BrightnessPercent);
    }

    private static PowerProfile CreateProfile() => new(
        PerformancePreset.Performance,
        FanMode.Fixed,
        LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 70),
        new DisplayProfileSettings("fixed-240", true, "native")
        {
            BrightnessPercent = 80,
        })
    {
        FanControl = new FanControlSettings(
            FanMode.Fixed,
            3500,
            ManualFanCurve.Default),
        GpuClockTuning = GpuClockTuningSettings.Disabled,
    };

    private static DeviceCapabilities Capabilities(
        IReadOnlySet<PerformancePreset> performance,
        IReadOnlySet<FanMode> fans,
        IReadOnlySet<LightingEffect> lighting) => new(
            performance,
            fans,
            lighting,
            new HashSet<int>(),
            SupportsBatteryProtectionOff: false,
            SupportsTemporaryFullCharge: false)
        {
            FanSpeedRange = fans.Any(mode => mode is FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum)
                ? FanSpeedRange.Standard
                : null,
        };

    private static DisplaySettingsSnapshot Display(
        IReadOnlyList<DisplayRefreshRateOption> refreshRates,
        IReadOnlyList<byte> brightnessLevels) => new(
            IsAvailable: true,
            RefreshRateOptions: refreshRates,
            SelectedRefreshRateKey: "fixed-240",
            IsHdrSupported: true,
            IsHdrEnabled: true,
            ColorProfileOptions: [],
            SelectedColorProfileKey: "native")
        {
            BrightnessPercent = 80,
            SupportedBrightnessLevels = brightnessLevels,
        };
}
