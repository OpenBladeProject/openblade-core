using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ActiveProfileDraftStateTests
{
    [Fact]
    public void Stage_RejectsInactiveAndUnsupportedPowerSources()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var state = new ActiveProfileDraftState();

        Assert.False(state.Stage(
            profiles,
            PowerSource.Battery,
            PowerSource.PluggedIn,
            performance: PerformancePreset.Performance));
        Assert.False(state.Stage(
            profiles,
            PowerSource.ExternalUnknown,
            PowerSource.ExternalUnknown,
            performance: PerformancePreset.Performance));

        Assert.False(state.HasActiveProfileDraft);
        Assert.Null(state.Source);
        Assert.Null(state.Performance);
    }

    [Fact]
    public void Stage_NormalizesDisplayAndLaptopChangesBackToOriginalBaselines()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var state = new ActiveProfileDraftState();
        var originalDisplay = new DisplayProfileSettings("fixed-60", false, "native");
        var changedDisplay = new DisplayProfileSettings("fixed-240", true, "srgb");
        var originalLaptop = new LaptopBehaviorProfileSettings(
            StartupAnimationEnabled: false,
            FunctionKeyPrimary: FunctionKeyPrimaryMode.MultimediaKeys,
            GamingModeEnabled: false);
        var changedLaptop = new LaptopBehaviorProfileSettings(
            StartupAnimationEnabled: true,
            FunctionKeyPrimary: FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled: true);

        Assert.True(state.Stage(
            profiles,
            PowerSource.PluggedIn,
            PowerSource.PluggedIn,
            display: changedDisplay,
            previousDisplay: originalDisplay));
        Assert.True(state.Stage(
            profiles,
            PowerSource.PluggedIn,
            PowerSource.PluggedIn,
            laptopBehavior: changedLaptop,
            previousLaptopBehavior: originalLaptop));
        Assert.Equal(originalDisplay, state.OriginalDisplay);
        Assert.Equal(originalLaptop, state.OriginalLaptopBehavior);

        Assert.True(state.Stage(
            profiles,
            PowerSource.PluggedIn,
            PowerSource.PluggedIn,
            display: originalDisplay));
        Assert.True(state.Stage(
            profiles,
            PowerSource.PluggedIn,
            PowerSource.PluggedIn,
            laptopBehavior: originalLaptop));

        Assert.False(state.HasActiveProfileDraft);
        Assert.Null(state.Display);
        Assert.Null(state.LaptopBehavior);
        Assert.Null(state.OriginalDisplay);
        Assert.Null(state.OriginalLaptopBehavior);
    }

    [Fact]
    public void Stage_ConfiguredSavedValuesTakePriorityOverOriginalBaselines()
    {
        var savedDisplay = new DisplayProfileSettings("fixed-60", false, "native");
        var savedLaptop = new LaptopBehaviorProfileSettings(
            StartupAnimationEnabled: false,
            FunctionKeyPrimary: FunctionKeyPrimaryMode.MultimediaKeys,
            GamingModeEnabled: false);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Display = savedDisplay,
                LaptopBehavior = savedLaptop,
            },
        };
        var state = new ActiveProfileDraftState();

        Assert.True(state.Stage(
            profiles,
            PowerSource.PluggedIn,
            PowerSource.PluggedIn,
            display: new DisplayProfileSettings("fixed-240", true, "srgb"),
            previousDisplay: new DisplayProfileSettings("dynamic", true, "p3")));
        Assert.True(state.Stage(
            profiles,
            PowerSource.PluggedIn,
            PowerSource.PluggedIn,
            laptopBehavior: new LaptopBehaviorProfileSettings(
                true,
                FunctionKeyPrimaryMode.FunctionKeys,
                true),
            previousLaptopBehavior: LaptopBehaviorProfileSettings.Unconfigured()));

        Assert.True(state.Stage(
            profiles,
            PowerSource.PluggedIn,
            PowerSource.PluggedIn,
            display: savedDisplay));
        Assert.True(state.Stage(
            profiles,
            PowerSource.PluggedIn,
            PowerSource.PluggedIn,
            laptopBehavior: savedLaptop));

        Assert.False(state.HasActiveProfileDraft);
        Assert.Null(state.OriginalDisplay);
        Assert.Null(state.OriginalLaptopBehavior);
    }

    [Fact]
    public void LaptopGroup_MergesAndClearsLaptopAndThermalValuesTogether()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var state = new ActiveProfileDraftState();
        LightingSettings lighting = LightingSettingsFactory.Static(
            new RgbColor(12, 34, 56),
            brightness: 67);
        var laptop = new LaptopBehaviorProfileSettings(
            StartupAnimationEnabled: true,
            FunctionKeyPrimary: FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled: true);
        WindowsPowerModeAssociations windowsModes =
            WindowsPowerModeAssociations.LeaveUnchanged with
            {
                Performance = WindowsPowerMode.BestPerformance,
            };

        Assert.True(state.Stage(
            profiles,
            PowerSource.PluggedIn,
            PowerSource.PluggedIn,
            performance: PerformancePreset.Performance,
            fans: FanMode.Maximum,
            lighting: lighting,
            laptopBehavior: laptop,
            windowsPowerModes: windowsModes));

        ProfileSet laptopProfiles = state.CreateProfileSet(
            profiles,
            ProfileDraftGroup.Laptop);

        Assert.Equal(PerformancePreset.Performance, laptopProfiles.PluggedIn.Performance);
        Assert.Equal(FanMode.Maximum, laptopProfiles.PluggedIn.Fans);
        Assert.Equal(laptop, laptopProfiles.PluggedIn.LaptopBehavior);
        Assert.Equal(windowsModes, laptopProfiles.WindowsPowerModes);
        Assert.Equal(profiles.PluggedIn.Lighting, laptopProfiles.PluggedIn.Lighting);

        state.Clear(ProfileDraftGroup.Laptop);

        Assert.False(state.HasThermalProfileDraft);
        Assert.False(state.HasLaptopProfileDraft);
        Assert.True(state.HasLightingProfileDraft);
        Assert.True(state.HasActiveProfileDraft);
        Assert.Null(state.WindowsPowerModes);
        Assert.Equal(lighting, state.Lighting);

        state.Clear(ProfileDraftGroup.Lighting);
        Assert.False(state.HasActiveProfileDraft);
    }

    [Fact]
    public void ChangePowerSource_PreservesOnlyGlobalWindowsPowerModeDraft()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var state = new ActiveProfileDraftState();
        WindowsPowerModeAssociations windowsModes =
            WindowsPowerModeAssociations.LeaveUnchanged with
            {
                Balanced = WindowsPowerMode.BestPerformance,
            };

        Assert.True(state.Stage(
            profiles,
            PowerSource.PluggedIn,
            PowerSource.PluggedIn,
            performance: PerformancePreset.Performance,
            windowsPowerModes: windowsModes));

        Assert.True(state.ChangePowerSource(PowerSource.Battery));
        Assert.Equal(PowerSource.Battery, state.Source);
        Assert.Equal(windowsModes, state.WindowsPowerModes);
        Assert.Null(state.Performance);
        Assert.True(state.HasThermalProfileDraft);

        Assert.True(state.ChangePowerSource(PowerSource.UsbC));
        Assert.Equal(PowerSource.UsbC, state.Source);
        Assert.Equal(windowsModes, state.WindowsPowerModes);
        Assert.False(state.ChangePowerSource(PowerSource.ExternalUnknown));
        Assert.Equal(PowerSource.UsbC, state.Source);
        Assert.Equal(windowsModes, state.WindowsPowerModes);
    }

    [Fact]
    public void CaptureSaveableUnsetFields_CapturesSupportedLightingAndLaptopValues()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var state = new ActiveProfileDraftState();
        var values = new ActiveProfileDraftValues(
            LogoLightingMode.Static,
            StartupAnimationEnabled: true,
            FunctionKeyPrimary: FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled: true);

        Assert.True(ActiveProfileDraftState.HasSaveableUnsetField(
            ProfileDraftGroup.Lighting,
            profiles.PluggedIn,
            values));
        Assert.True(state.CaptureSaveableUnsetFields(
            profiles,
            PowerSource.PluggedIn,
            ProfileDraftGroup.Lighting,
            values));
        Assert.Equal(LogoLightingMode.Static, state.Lighting?.LogoMode);

        state.Clear();

        Assert.True(ActiveProfileDraftState.HasSaveableUnsetField(
            ProfileDraftGroup.Laptop,
            profiles.PluggedIn,
            values));
        Assert.True(state.CaptureSaveableUnsetFields(
            profiles,
            PowerSource.PluggedIn,
            ProfileDraftGroup.Laptop,
            values));
        Assert.Equal(true, state.LaptopBehavior?.StartupAnimationEnabled);
        Assert.Equal(FunctionKeyPrimaryMode.FunctionKeys, state.LaptopBehavior?.FunctionKeyPrimary);
        Assert.Equal(true, state.LaptopBehavior?.GamingModeEnabled);
        Assert.False(ActiveProfileDraftState.HasSaveableUnsetField(
            ProfileDraftGroup.Display,
            profiles.PluggedIn,
            values));
    }

    [Fact]
    public void CaptureSaveableUnsetFields_IgnoresNullValuesAndPreservesConfiguredValues()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Lighting = defaults.PluggedIn.Lighting with
                {
                    LogoMode = LogoLightingMode.Static,
                },
                LaptopBehavior = new LaptopBehaviorProfileSettings(
                    StartupAnimationEnabled: false,
                    FunctionKeyPrimary: null,
                    GamingModeEnabled: false),
            },
        };
        var emptyValues = new ActiveProfileDraftValues(null, null, null, null);
        var state = new ActiveProfileDraftState();

        Assert.False(ActiveProfileDraftState.HasSaveableUnsetField(
            ProfileDraftGroup.Laptop,
            defaults.PluggedIn,
            emptyValues));
        Assert.False(state.CaptureSaveableUnsetFields(
            defaults,
            PowerSource.PluggedIn,
            ProfileDraftGroup.Laptop,
            emptyValues));

        var currentValues = new ActiveProfileDraftValues(
            LogoLightingMode.Breathing,
            StartupAnimationEnabled: true,
            FunctionKeyPrimary: FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled: true);
        Assert.False(state.CaptureSaveableUnsetFields(
            profiles,
            PowerSource.PluggedIn,
            ProfileDraftGroup.Lighting,
            currentValues));
        Assert.Null(state.Lighting);

        Assert.True(state.CaptureSaveableUnsetFields(
            profiles,
            PowerSource.PluggedIn,
            ProfileDraftGroup.Laptop,
            currentValues));
        Assert.Equal(false, state.LaptopBehavior?.StartupAnimationEnabled);
        Assert.Equal(FunctionKeyPrimaryMode.FunctionKeys, state.LaptopBehavior?.FunctionKeyPrimary);
        Assert.Equal(false, state.LaptopBehavior?.GamingModeEnabled);
    }
}
