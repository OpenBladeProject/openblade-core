using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task FirmwareCards_PresentInstalledMcuVersionConsistently()
    {
        var firmware = new FirmwareVersions(
            "3.01",
            "1.04",
            "1.7.0.0",
            "95.04.5C.40.A9",
            RazerBladeModelCatalog.OpenBladeTargetModelNumber);
        var client = new FakeServiceClient();
        await using var mainViewModel = new MainViewModel(
            client,
            new RecordingDispatcher());
        await using var firmwareViewModel = new FirmwareUpdateAssistantViewModel(
            new StubFirmwareUpdateClient());

        await mainViewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            Firmware = firmware,
        });
        firmwareViewModel.UpdateInstalledFirmware(firmware);
        await WaitUntilAsync(() => mainViewModel.IsConnected);

        Assert.Equal("1.07", mainViewModel.AboutMcuVersionStatus);
        Assert.Equal(
            firmwareViewModel.InstalledMcuVersion,
            mainViewModel.AboutMcuVersionStatus);
        Assert.Equal(
            "OpenBlade has not tested this configuration: MCU 1.07.",
            mainViewModel.DashboardTestedPlatformWarningMessage);
        Assert.Equal("1.7.0.0", mainViewModel.DetectedFirmware?.McuVersion);
    }

    [Fact]
    public async Task LightingPowerPolicyEditor_StagesAndSavesRulesToActiveProfile()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.LightingTurnOffWithDisplayEditor = true;
        viewModel.LightingIdleTimeoutEnabledEditor = true;
        viewModel.LightingIdleTimeoutMinutesEditor = 12;
        LightingPowerPolicy draft = viewModel.LightingPowerPolicyDraft;

        Assert.True(viewModel.HasUnsavedLightingPowerPolicy);
        Assert.True(viewModel.SaveLightingPowerPolicyCommand.CanExecute(draft));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveLightingPowerPolicyCommand)
            .ExecuteAsync(draft);

        Assert.Null(client.LastProfiles);
        Assert.True(viewModel.HasLightingProfileDraft);
        Assert.False(viewModel.HasUnsavedLightingPowerPolicy);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveLightingProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Equal(
            new LightingPowerPolicy(true, 12),
            client.LastProfiles?.PluggedIn.EffectiveLightingPowerPolicy);
        Assert.Equal(
            LightingPowerPolicy.Disabled,
            client.LastProfiles?.UsbC.EffectiveLightingPowerPolicy);
        Assert.False(viewModel.HasLightingProfileDraft);
    }

    [Fact]
    public async Task ProfileEditors_ReportUnsavedChangesUntilSaveThenReloadCleanly()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        var unsavedTransitions = new List<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.HasUnsavedProfileChanges))
            {
                unsavedTransitions.Add(viewModel.HasUnsavedProfileChanges);
            }
        };
        await viewModel.StartAsync();
        LightingSettings usbCLighting = new(
            LightingEffect.Static,
            new RgbColor(10, 20, 30),
            45);
        ServiceStatusSnapshot original = CreateStatus(verified: true, blocked: false);
        ServiceStatusSnapshot baseline = original with
        {
            Profiles = original.Profiles with
            {
                UsbC = original.Profiles.UsbC with { Lighting = usbCLighting },
            },
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.False(viewModel.HasUnsavedProfileChanges);
        Assert.False(viewModel.SaveProfilesCommand.CanExecute(null));
        Assert.Equal("Ready", viewModel.ProfileStateLabel);

        viewModel.PluggedInPerformanceEditor = PerformancePreset.Performance;
        client.Publish(baseline with
        {
            Revision = 2,
            Message = "Periodic observation",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Periodic observation");

        Assert.True(viewModel.HasUnsavedProfileChanges);
        Assert.Equal(
            PerformancePreset.Performance,
            viewModel.PluggedInPerformanceEditor);
        Assert.True(viewModel.SaveProfilesCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand)
            .ExecuteAsync(null);

        Assert.False(viewModel.HasUnsavedProfileChanges);
        Assert.False(viewModel.SaveProfilesCommand.CanExecute(null));
        Assert.Equal(
            PerformancePreset.Performance,
            viewModel.Profiles.PluggedIn.Performance);
        Assert.Equal(usbCLighting, viewModel.Profiles.UsbC.Lighting);
        Assert.Equal(usbCLighting, client.LastProfiles?.UsbC.Lighting);

        ProfileSet reloaded = baseline.Profiles with
        {
            PluggedIn = baseline.Profiles.PluggedIn with
            {
                Performance = PerformancePreset.Silent,
            },
        };
        client.Publish(baseline with
        {
            Revision = 3,
            Message = "Profiles reloaded",
            Profiles = reloaded,
        });
        await WaitUntilAsync(() =>
            viewModel.PluggedInPerformanceEditor == PerformancePreset.Silent);

        Assert.False(viewModel.HasUnsavedProfileChanges);
        Assert.Equal([true, false], unsavedTransitions);
    }

    [Fact]
    public async Task ActiveProfileChanges_StayDraftedUntilSaveThenClearTemporaryOverrides()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseStatus = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        ProfileSet initialProfiles = baseStatus.Profiles with
        {
            PluggedIn = baseStatus.Profiles.PluggedIn with
            {
                Performance = PerformancePreset.Silent,
            },
        };
        ServiceStatusSnapshot original = baseStatus with
        {
            Profiles = initialProfiles,
            ConfirmedState = baseStatus.ConfirmedState! with
            {
                Performance = PerformancePreset.Silent,
                Fans = FanMode.Automatic,
            },
            DesiredState = baseStatus.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Silent,
                    SettingValueSource.PowerProfile),
            },
        };
        client.Publish(original);
        await WaitUntilAsync(() => viewModel.IsConnected);

        LightingSettings lighting = LightingSettingsFactory.Static(
            new RgbColor(90, 80, 70),
            brightness: 68);
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Balanced);
        client.Publish(original with
        {
            Revision = 2,
            Message = "Performance applied",
            ConfirmedState = original.ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
            },
            DesiredState = original.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    SettingValueSource.TemporaryOverride),
            },
            PerformanceOverride = client.PerformanceOverride,
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Performance applied");
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetFanModeCommand)
            .ExecuteAsync(FanMode.Maximum);
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(lighting);
        var display = new DisplayProfileSettings("drr-48-240", false, "srgb");
        viewModel.RecordDisplayProfileChange(PowerSource.PluggedIn, display);

        Assert.True(viewModel.HasActiveProfileDraft);
        Assert.True(viewModel.HasUnsavedProfileChanges);
        Assert.True(viewModel.IsPluggedInProfileDraft);
        Assert.False(viewModel.IsBatteryProfileDraft);
        Assert.False(viewModel.IsUsbCProfileDraft);
        Assert.Equal("AC", viewModel.ActiveProfileDraftName);
        Assert.False(viewModel.HasAttention);
        Assert.Contains("Maximum", viewModel.PendingActiveProfileSummary);
        Assert.Contains("Static", viewModel.PendingActiveProfileSummary);
        Assert.Contains("drr-48-240", viewModel.PendingActiveProfileSummary);
        Assert.Contains("Maximum", viewModel.PluggedInProfileThermalSummary);
        Assert.Contains("Static", viewModel.PluggedInProfileLightingSummary);
        Assert.Contains("drr-48-240", viewModel.PluggedInProfileDisplaySummary);
        Assert.Equal("Silent", viewModel.PluggedInProfilePresentation.PowerMode.Text);
        Assert.Equal("Not set", viewModel.PluggedInProfilePresentation.GpuTuning.Text);
        Assert.Equal("Automatic", viewModel.PluggedInProfilePresentation.Fans.Text);
        Assert.Equal("Off", viewModel.PluggedInProfilePresentation.LightingEffect.Text);
        Assert.Equal("Not set", viewModel.PluggedInProfilePresentation.DisplayRefreshRate.Text);
        Assert.Equal("Not set", viewModel.PluggedInProfilePresentation.DisplayColorProfile.Text);
        Assert.True(viewModel.PluggedInProfilePresentation.PowerMode.IsPending);
        Assert.Equal(
            "Pending: Balanced",
            viewModel.PluggedInProfilePresentation.PowerMode.PendingDescription);
        Assert.True(viewModel.PluggedInProfilePresentation.Fans.IsPending);
        Assert.Equal(
            "Pending: Maximum",
            viewModel.PluggedInProfilePresentation.Fans.PendingDescription);
        Assert.True(viewModel.PluggedInProfilePresentation.LightingEffect.IsPending);
        Assert.True(viewModel.PluggedInProfilePresentation.LightingBrightness.IsPending);
        Assert.True(viewModel.PluggedInProfilePresentation.LightingColor.IsPending);
        Assert.True(viewModel.PluggedInProfilePresentation.DisplayRefreshRate.IsPending);
        Assert.True(viewModel.PluggedInProfilePresentation.DisplayHdr.IsPending);
        Assert.True(viewModel.PluggedInProfilePresentation.DisplayColorProfile.IsPending);
        Assert.False(viewModel.PluggedInProfilePresentation.GpuTuning.IsPending);
        Assert.Null(viewModel.PluggedInProfilePresentation.GpuTuning.PendingDescription);
        Assert.Equal(
            "AC GPU tuning: Not set",
            viewModel.PluggedInProfilePresentation.GpuTuning.AccessibilityDescription);
        Assert.False(viewModel.PluggedInProfilePresentation.LogoLighting.IsPending);
        Assert.False(viewModel.PluggedInProfilePresentation.LightingDisplayOff.IsPending);
        Assert.False(viewModel.PluggedInProfilePresentation.LightingIdle.IsPending);
        Assert.False(viewModel.PluggedInProfilePresentation.FunctionKeyBehavior.IsPending);
        Assert.False(viewModel.UsbCProfilePresentation.PowerMode.IsPending);
        Assert.False(viewModel.BatteryProfilePresentation.PowerMode.IsPending);
        Assert.Equal(PowerSource.PluggedIn, viewModel.SelectedProfileComparisonOption.Source);
        Assert.True(viewModel.IsSelectedProfileActive);
        Assert.True(viewModel.IsSelectedProfileDraft);
        Assert.Equal(
            viewModel.PluggedInProfilePresentation,
            viewModel.SelectedProfilePresentation);
        Assert.Equal(original.Profiles, viewModel.Profiles);
        Assert.Equal(
            OverrideLifetime.UntilPowerSourceChange,
            client.PerformanceOverride!.Lifetime);
        Assert.Equal(
            OverrideLifetime.UntilPowerSourceChange,
            client.FanOverride!.Lifetime);
        Assert.Equal(
            OverrideLifetime.UntilPowerSourceChange,
            client.LightingOverride!.Lifetime);
        Assert.True(viewModel.SaveProfilesCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand)
            .ExecuteAsync(null);

        Assert.NotNull(client.LastProfiles);
        Assert.Equal(PerformancePreset.Balanced, client.LastProfiles.PluggedIn.Performance);
        Assert.Equal(FanMode.Maximum, client.LastProfiles.PluggedIn.Fans);
        Assert.Equal(lighting, client.LastProfiles.PluggedIn.Lighting);
        Assert.Equal(display, client.LastProfiles.PluggedIn.Display);
        Assert.Equal(original.Profiles.Battery, client.LastProfiles.Battery);
        Assert.Equal(original.Profiles.UsbC, client.LastProfiles.UsbC);
        Assert.Equal(original.Profiles.BatteryPolicy, client.LastProfiles.BatteryPolicy);
        Assert.Null(client.PerformanceOverride);
        Assert.Null(client.FanOverride);
        Assert.Null(client.LightingOverride);
        Assert.False(viewModel.HasActiveProfileDraft);
        Assert.False(viewModel.HasUnsavedProfileChanges);
        Assert.False(viewModel.IsPluggedInProfileDraft);
        Assert.Contains("Maximum", viewModel.PluggedInProfileThermalSummary);
        Assert.Equal("Balanced", viewModel.PluggedInProfilePresentation.PowerMode.Text);
        Assert.False(viewModel.PluggedInProfilePresentation.PowerMode.IsPending);
        Assert.Null(viewModel.PluggedInProfilePresentation.PowerMode.PendingDescription);
        Assert.Equal("Maximum", viewModel.PluggedInProfilePresentation.Fans.Text);
        Assert.False(viewModel.PluggedInProfilePresentation.Fans.IsPending);
    }

    [Fact]
    public async Task SubsystemProfileActions_SaveAndDiscardOnlyTheirOwnChanges()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsConnected);
        LightingSettings lighting = LightingSettingsFactory.Static(
            new RgbColor(12, 34, 56),
            brightness: 67);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(lighting);

        Assert.True(viewModel.HasThermalProfileDraft);
        Assert.True(viewModel.HasLightingProfileDraft);
        Assert.Equal(
            "Temporary change not saved to this profile",
            viewModel.ThermalProfileRelationshipStatus);
        Assert.True(viewModel.SaveThermalProfileDraftCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveThermalProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Equal(PerformancePreset.Performance, viewModel.Profiles.PluggedIn.Performance);
        Assert.Equal(baseline.Profiles.PluggedIn.Lighting, viewModel.Profiles.PluggedIn.Lighting);
        Assert.Null(client.PerformanceOverride);
        Assert.NotNull(client.LightingOverride);
        Assert.False(viewModel.HasThermalProfileDraft);
        Assert.True(viewModel.HasLightingProfileDraft);

        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardLightingProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Null(client.LightingOverride);
        Assert.False(viewModel.HasLightingProfileDraft);
        Assert.False(viewModel.HasActiveProfileDraft);
    }

    [Fact]
    public async Task DashboardDiscard_ClearsThermalAndLightingOverridesTogether()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
            });
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(LightingSettingsFactory.Static(
                new RgbColor(12, 34, 56),
                brightness: 67));

        Assert.True(viewModel.HasThermalProfileDifference);
        Assert.True(viewModel.HasLightingProfileDraft);

        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Contains("performance:<clear>", client.MutationOperations);
        Assert.Contains("lighting:<clear>", client.MutationOperations);
        Assert.Null(client.PerformanceOverride);
        Assert.Null(client.LightingOverride);
        Assert.False(viewModel.HasActiveProfileDraft);
    }

    [Fact]
    public async Task UseLightingProfileSettings_RestoresConfirmedExternalDifferenceWithoutDraft()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
            });
        LightingSettings external = LightingSettingsFactory.Static(
            new RgbColor(12, 34, 56),
            brightness: 67);
        client.Publish(baseline with
        {
            ConfirmedState = baseline.ConfirmedState! with { Lighting = external },
            ExternalChanges = [DeviceSubsystem.Lighting],
        });
        await WaitUntilAsync(() => viewModel.HasLightingProfileDifference);

        Assert.False(viewModel.HasLightingProfileDraft);
        Assert.True(viewModel.HasLightingProfileDifference);
        Assert.True(viewModel.DiscardLightingProfileDraftCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardLightingProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Contains("lighting:<clear>", client.MutationOperations);
        Assert.False(viewModel.HasLightingProfileDifference);
    }

    [Fact]
    public async Task UseLightingProfileSettings_RestoresUnreadableExternalDifferenceWithoutDraft()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
            });
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsKeyboardEffectUnconfirmed);

        Assert.False(viewModel.HasLightingProfileDifference);

        client.Publish(baseline with
        {
            Revision = baseline.Revision + 1,
            ExternalChanges = [DeviceSubsystem.Lighting],
        });
        await WaitUntilAsync(() =>
            viewModel.LightingSourceLabel == "Changed outside OpenBlade");

        Assert.False(viewModel.HasLightingProfileDraft);
        Assert.True(viewModel.IsKeyboardEffectUnconfirmed);
        Assert.True(viewModel.HasLightingProfileDifference);
        Assert.True(viewModel.DiscardLightingProfileDraftCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardLightingProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Contains("lighting:<clear>", client.MutationOperations);

        client.Publish(baseline with { Revision = baseline.Revision + 2 });
        await WaitUntilAsync(() =>
            viewModel.LightingSourceLabel != "Changed outside OpenBlade");

        Assert.False(viewModel.HasLightingProfileDifference);
    }

    [Fact]
    public async Task PowerSourceRefresh_DoesNotPublishTransientProfileDifference()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot ac = CreateStatus(verified: true, blocked: false);
        client.Publish(ac);
        await WaitUntilAsync(() =>
            viewModel.IsConnected
            && viewModel.ActivePowerSource == PowerSource.PluggedIn);

        var publishedDifferences = new List<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.HasLaptopPageProfileDifference))
            {
                publishedDifferences.Add(viewModel.HasLaptopPageProfileDifference);
            }
        };

        PowerProfile battery = ac.Profiles.Battery;
        client.Publish(ac with
        {
            Revision = ac.Revision + 1,
            ActivePowerSource = PowerSource.Battery,
            ConfirmedState = ac.ConfirmedState! with
            {
                Performance = battery.Performance,
                Fans = battery.Fans,
            },
            DesiredState = ac.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    battery.Performance,
                    SettingValueSource.PowerProfile),
                Fans = new ResolvedSetting<FanMode>(
                    battery.Fans,
                    SettingValueSource.PowerProfile),
                FanControl = battery.FanControl,
            },
        });
        await WaitUntilAsync(() =>
            viewModel.ActivePowerSource == PowerSource.Battery
            && viewModel.SelectedPerformance == battery.Performance);

        Assert.False(viewModel.HasLaptopPageProfileDifference);
        Assert.DoesNotContain(true, publishedDifferences);
    }

    [Fact]
    public async Task SaveLightingProfileDraft_PersistsVisibleSelectionBeforeClearingOverride()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsConnected);
        LightingSettings visibleSelection = LightingSettingsFactory.Static(
            new RgbColor(12, 34, 56),
            brightness: 67);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(visibleSelection);

        Assert.Equal(visibleSelection, client.LightingOverride?.Value);
        Assert.True(viewModel.HasLightingProfileDraft);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveLightingProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Equal(visibleSelection, client.LastProfiles?.PluggedIn.Lighting);
        Assert.Equal(visibleSelection, viewModel.Profiles.PluggedIn.Lighting);
        Assert.Null(client.LightingOverride);
        Assert.False(viewModel.HasLightingProfileDraft);
    }

    [Fact]
    public async Task SavedProfileRemainsSavedWhenEquivalentOverrideCleanupFails()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.IsConnected);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);
        client.MutationResults.Enqueue(
            new ControlOperationResult(ControlOperationStatus.Applied, "Profile saved."));
        client.MutationResults.Enqueue(
            new ControlOperationResult(ControlOperationStatus.Failed, "Could not clear override."));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand)
            .ExecuteAsync(null);

        Assert.NotNull(client.LastProfiles);
        Assert.Equal(PerformancePreset.Performance, viewModel.Profiles.PluggedIn.Performance);
        Assert.False(viewModel.HasActiveProfileDraft);
        Assert.False(viewModel.HasUnsavedProfileChanges);
        Assert.Equal("Could not clear override.", viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task ActiveProfileDraft_DiscardRestoresSavedControlsAndDisplay()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        ServiceStatusSnapshot original = baseline with
        {
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Automatic,
            },
        };
        client.Publish(original);
        await WaitUntilAsync(() => viewModel.IsConnected);
        DisplayProfileSettings? restoredDisplay = null;
        PowerSource? restoredSource = null;
        viewModel.SavedDisplayProfileRestoreRequested += (source, settings, _) =>
        {
            restoredSource = source;
            restoredDisplay = settings;
            return Task.FromResult(true);
        };

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetFanModeCommand)
            .ExecuteAsync(FanMode.Maximum);
        var originalDisplay = new DisplayProfileSettings("60", false, "native");
        viewModel.RecordDisplayProfileChange(
            PowerSource.PluggedIn,
            new DisplayProfileSettings("240", true, null),
            originalDisplay);
        Assert.True(viewModel.HasActiveProfileDraft);
        Assert.True(viewModel.PluggedInProfilePresentation.Fans.IsPending);
        Assert.Equal(
            "Pending: Maximum",
            viewModel.PluggedInProfilePresentation.Fans.PendingDescription);
        Assert.True(viewModel.PluggedInProfilePresentation.DisplayRefreshRate.IsPending);

        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Null(client.FanOverride);
        Assert.Equal(PowerSource.PluggedIn, restoredSource);
        Assert.Equal(originalDisplay, restoredDisplay);
        Assert.Equal(original.Profiles, viewModel.Profiles);
        Assert.False(viewModel.HasActiveProfileDraft);
        Assert.False(viewModel.PluggedInProfilePresentation.Fans.IsPending);
        Assert.False(viewModel.PluggedInProfilePresentation.DisplayRefreshRate.IsPending);
    }

    [Fact]
    public async Task PendingProfileValue_MarksChangedManualFanCurveWithSameLabel()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        var savedFanControl = new FanControlSettings(
            FanMode.ManualCurve,
            3700,
            ManualFanCurve.Default);
        ProfileSet profiles = baseline.Profiles with
        {
            PluggedIn = baseline.Profiles.PluggedIn with
            {
                Fans = FanMode.ManualCurve,
                FanControl = savedFanControl,
            },
        };
        client.Publish(baseline with
        {
            Profiles = profiles,
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.ManualCurve,
            },
            DesiredState = baseline.DesiredState with
            {
                Fans = new ResolvedSetting<FanMode>(
                    FanMode.ManualCurve,
                    SettingValueSource.PowerProfile),
                FanControl = savedFanControl,
            },
        });
        await WaitUntilAsync(() =>
            viewModel.IsConnected
            && viewModel.SelectedFanMode == FanMode.ManualCurve);
        FanCurvePoint[] changedCpu = ManualFanCurve.Default.Cpu
            .Select((point, index) => index == 1
                ? new FanCurvePoint(point.TemperatureCelsius, point.SpeedRpm + 100)
                : point)
            .ToArray();
        var changedFanControl = new FanControlSettings(
            FanMode.ManualCurve,
            3700,
            new ManualFanCurve(
                changedCpu,
                ManualFanCurve.Default.Gpu,
                ManualFanCurve.Default.UseHigherTemperatureForBoth));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetFanControlCommand)
            .ExecuteAsync(changedFanControl);

        Assert.Equal("Manual fan curve", viewModel.PluggedInProfilePresentation.Fans.Text);
        Assert.True(viewModel.PluggedInProfilePresentation.Fans.IsPending);
        Assert.Equal(
            "Pending: Manual fan curve",
            viewModel.PluggedInProfilePresentation.Fans.PendingDescription);
        Assert.Equal(
            "AC Fans: Manual fan curve. Pending: Manual fan curve",
            viewModel.PluggedInProfilePresentation.Fans.AccessibilityDescription);
        Assert.Equal(changedFanControl, client.LastFanControlSettings);
    }

    [Fact]
    public async Task PendingProfileValue_IgnoresHiddenOffsetsWhileGpuTuningIsOff()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        var savedOffsets = new GpuClockOffsetSettings(100, 200);
        var currentOffsets = new GpuClockOffsetSettings(0, 0);
        var gpuCapabilities = new GpuClockOffsetCapabilities(
            "NVIDIA GeForce RTX 5090 Laptop GPU",
            new GpuClockOffsetRange(-1000, 1000, currentOffsets.CoreMegahertz),
            new GpuClockOffsetRange(-1000, 3000, currentOffsets.MemoryMegahertz));
        ProfileSet profiles = baseline.Profiles with
        {
            PluggedIn = baseline.Profiles.PluggedIn with
            {
                GpuClockTuning = new GpuClockTuningSettings(
                    Enabled: false,
                    savedOffsets),
            },
        };
        client.Publish(baseline with
        {
            Profiles = profiles,
            Capabilities = baseline.Capabilities! with
            {
                GpuOverclock = new GpuOverclockCapabilities(
                    new DeviceNumericRange(-1000, 400, null),
                    new DeviceNumericRange(-1000, 3000, null),
                    DeviceFeatureAccess.ReadWrite),
            },
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                GpuClockOffsets = gpuCapabilities,
            },
            DesiredState = baseline.DesiredState with
            {
                GpuClockTuning = new GpuClockTuningSettings(
                    Enabled: false,
                    currentOffsets),
            },
        });
        await WaitUntilAsync(() => viewModel.CanToggleGpuClockTuning);

        await viewModel.SetGpuClockTuningEnabledAsync(
            enabled: false,
            CancellationToken.None);

        Assert.True(viewModel.HasActiveProfileDraft);
        Assert.Equal("Off", viewModel.PluggedInProfilePresentation.GpuTuning.Text);
        Assert.False(viewModel.PluggedInProfilePresentation.GpuTuning.IsPending);
        Assert.Null(viewModel.PluggedInProfilePresentation.GpuTuning.PendingDescription);
    }

    [Fact]
    public async Task PendingProfileValue_MarksBrightnessWhenEffectMakesItVisible()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsConnected);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(LightingSettingsFactory.Static(new RgbColor(12, 34, 56), 50));

        Assert.True(viewModel.PluggedInProfilePresentation.LightingEffect.IsPending);
        Assert.Equal("Not applicable", viewModel.PluggedInProfilePresentation.LightingBrightness.Text);
        Assert.True(viewModel.PluggedInProfilePresentation.LightingBrightness.IsPending);
        Assert.Equal(
            "Pending: 50%",
            viewModel.PluggedInProfilePresentation.LightingBrightness.PendingDescription);
    }

    [Fact]
    public async Task PendingProfileValue_MarksOnlyTheBatteryDraftColumn()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline with { ActivePowerSource = PowerSource.Battery });
        await WaitUntilAsync(() =>
            viewModel.IsConnected
            && viewModel.ActivePowerSource == PowerSource.Battery);

        viewModel.RecordDisplayProfileChange(
            PowerSource.Battery,
            new DisplayProfileSettings("dynamic-48-240", false, null));

        Assert.False(viewModel.PluggedInProfilePresentation.DisplayRefreshRate.IsPending);
        Assert.False(viewModel.UsbCProfilePresentation.DisplayRefreshRate.IsPending);
        Assert.True(viewModel.BatteryProfilePresentation.DisplayRefreshRate.IsPending);
        Assert.Equal(
            "On battery Refresh rate: Not set. Pending: Dynamic 48–240 Hz",
            viewModel.BatteryProfilePresentation.DisplayRefreshRate.AccessibilityDescription);
    }

    [Fact]
    public async Task DisplayDiscard_MergesPartialSavedProfileOverPreEditState()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        ProfileSet profiles = baseline.Profiles with
        {
            PluggedIn = baseline.Profiles.PluggedIn with
            {
                Display = new DisplayProfileSettings("fixed-60", null, null),
            },
        };
        client.Publish(baseline with { Profiles = profiles });
        await WaitUntilAsync(() => viewModel.Profiles == profiles);
        DisplayProfileSettings? restoredDisplay = null;
        viewModel.SavedDisplayProfileRestoreRequested += (_, settings, _) =>
        {
            restoredDisplay = settings;
            return Task.FromResult(true);
        };
        var original = new DisplayProfileSettings("fixed-240", false, "native");
        viewModel.RecordDisplayProfileChange(
            PowerSource.PluggedIn,
            new DisplayProfileSettings("fixed-240", true, null),
            original);

        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Equal(
            new DisplayProfileSettings("fixed-60", false, "native"),
            restoredDisplay);
        Assert.False(viewModel.HasActiveProfileDraft);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisplayOnlyDraftDoesNotClearUnrelatedHardwareOverrides(bool save)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline with
        {
            PerformanceOverride = new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            FanOverride = new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.Persistent),
            LightingOverride = new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
                OverrideLifetime.Persistent),
        });
        await WaitUntilAsync(() => viewModel.IsConnected);
        viewModel.SavedDisplayProfileRestoreRequested += (_, _, _) =>
            Task.FromResult(true);
        viewModel.RecordDisplayProfileChange(
            PowerSource.PluggedIn,
            new DisplayProfileSettings("fixed-240", false, "native"),
            new DisplayProfileSettings("fixed-60", false, "native"));

        await Assert.IsType<AsyncRelayCommand>(save
                ? viewModel.SaveProfilesCommand
                : viewModel.DiscardProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Empty(client.MutationOperations);
        Assert.False(viewModel.HasActiveProfileDraft);
    }

    [Fact]
    public async Task CapturedCurrentDisplaySettings_CanBeSavedToActiveProfile()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.IsConnected);
        var current = new DisplayProfileSettings(
            "dynamic-48-240",
            false,
            "srgb");

        viewModel.RecordDisplayProfileChange(PowerSource.PluggedIn, current);

        Assert.True(viewModel.HasActiveProfileDraft);
        Assert.Equal("Not set", viewModel.PluggedInProfilePresentation.DisplayRefreshRate.Text);
        Assert.Equal("Not set", viewModel.PluggedInProfilePresentation.DisplayHdr.Text);
        Assert.Equal("Not set", viewModel.PluggedInProfilePresentation.DisplayColorProfile.Text);
        Assert.True(viewModel.PluggedInProfilePresentation.DisplayRefreshRate.IsPending);
        Assert.Equal(
            "Pending: Dynamic 48–240 Hz",
            viewModel.PluggedInProfilePresentation.DisplayRefreshRate.PendingDescription);
        Assert.True(viewModel.PluggedInProfilePresentation.DisplayHdr.IsPending);
        Assert.True(viewModel.PluggedInProfilePresentation.DisplayColorProfile.IsPending);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand)
            .ExecuteAsync(null);

        Assert.Equal(current, client.LastProfiles?.PluggedIn.Display);
        Assert.False(viewModel.HasActiveProfileDraft);
        Assert.Equal("Dynamic 48–240 Hz", viewModel.PluggedInProfilePresentation.DisplayRefreshRate.Text);
        Assert.Equal("Off", viewModel.PluggedInProfilePresentation.DisplayHdr.Text);
        Assert.Equal("sRGB", viewModel.PluggedInProfilePresentation.DisplayColorProfile.Text);
        Assert.False(viewModel.PluggedInProfilePresentation.DisplayRefreshRate.IsPending);
        Assert.False(viewModel.PluggedInProfilePresentation.DisplayHdr.IsPending);
        Assert.False(viewModel.PluggedInProfilePresentation.DisplayColorProfile.IsPending);
    }

    [Fact]
    public async Task UnsetLogoMode_OffersAndSavesConfirmedCurrentValue()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                LogoLightingModes = new HashSet<LogoLightingMode>(
                    Enum.GetValues<LogoLightingMode>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(status with
        {
            ConfirmedState = status.ConfirmedState! with
            {
                LogoLighting = LogoLightingMode.Breathing,
            },
        });
        await WaitUntilAsync(() =>
            viewModel.LogoLightingModeEditor == LogoLightingMode.Breathing
            && viewModel.CanControlLighting
            && viewModel.HasLightingProfileSaveOpportunity);

        Assert.Equal("Not set", viewModel.PluggedInProfilePresentation.LogoLighting.Text);
        Assert.False(viewModel.HasLightingProfileDraft);
        Assert.True(viewModel.HasLightingProfileSaveOpportunity);
        Assert.True(viewModel.CanControlHardware);
        Assert.True(viewModel.CanControlLighting);
        Assert.True(viewModel.SaveLightingProfileDraftCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveLightingProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Equal(
            LogoLightingMode.Breathing,
            client.LastProfiles?.PluggedIn.Lighting.LogoMode);
        Assert.False(viewModel.HasLightingProfileDraft);
        Assert.False(viewModel.HasLightingProfileSaveOpportunity);
    }

    [Fact]
    public async Task UnsetLaptopBehavior_OffersAndSavesKnownCurrentValues()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                OneShotLaptopControls = new HashSet<OneShotLaptopControl>(
                    Enum.GetValues<OneShotLaptopControl>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(status with
        {
            StartupAnimationEnabled = true,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled = false,
        });
        await WaitUntilAsync(() =>
            viewModel.StartupAnimationEnabled is true
            && viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.FunctionKeys
            && viewModel.GamingModeEnabled is false);

        Assert.Equal(
            "Not set",
            viewModel.PluggedInProfilePresentation.StartupAnimation.Text);
        Assert.False(viewModel.HasLaptopPageProfileDraft);
        Assert.True(viewModel.HasLaptopPageProfileSaveOpportunity);
        Assert.True(viewModel.SaveLaptopProfileDraftCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveLaptopProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Equal(
            new LaptopBehaviorProfileSettings(
                StartupAnimationEnabled: true,
                FunctionKeyPrimary: FunctionKeyPrimaryMode.FunctionKeys,
                GamingModeEnabled: false),
            client.LastProfiles?.PluggedIn.LaptopBehavior);
        Assert.False(viewModel.HasLaptopPageProfileDraft);
        Assert.False(viewModel.HasLaptopPageProfileSaveOpportunity);
    }

    [Theory]
    [InlineData(false, false, false, "Off", "Off")]
    [InlineData(true, true, false, "On", "Off")]
    public async Task ProfilePresentation_UsesEffectiveGamingModeKeyPolicy(
        bool hasPolicy,
        bool blockWindowsKey,
        bool blockAltTab,
        string expectedWindowsKey,
        string expectedAltTab)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        ProfileSet profiles = status.Profiles with
        {
            PluggedIn = status.Profiles.PluggedIn with
            {
                LaptopBehavior = new LaptopBehaviorProfileSettings(
                    StartupAnimationEnabled: null,
                    FunctionKeyPrimary: null,
                    GamingModeEnabled: false)
                {
                    GamingModeKeyPolicy = hasPolicy
                        ? new GamingModeKeyPolicy(blockWindowsKey, blockAltTab)
                        : null,
                },
            },
        };

        client.Publish(status with
        {
            Profiles = profiles,
            GamingModeEnabled = false,
        });
        await WaitUntilAsync(() =>
            viewModel.PluggedInProfilePresentation.GamingMode.Text == "Off");

        Assert.Equal(
            expectedWindowsKey,
            viewModel.PluggedInProfilePresentation.GamingModeWindowsKey.Text);
        Assert.Equal(
            expectedAltTab,
            viewModel.PluggedInProfilePresentation.GamingModeAltTab.Text);
        Assert.Equal(
            expectedWindowsKey,
            viewModel.SelectedProfilePresentation.GamingModeWindowsKey.Text);
        Assert.Equal(
            expectedAltTab,
            viewModel.SelectedProfilePresentation.GamingModeAltTab.Text);
    }

    [Fact]
    public async Task GamingModeKeyPolicy_PersistsImmediatelyToTheActiveProfile()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                OneShotLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.GamingMode,
                },
                ChargeLimits = ChargeLimits(),
            }) with
        {
            GamingModeEnabled = true,
        };
        client.Publish(status);
        await WaitUntilAsync(() =>
            viewModel.GamingModeEnabled is true
            && viewModel.CanEditGamingModeKeyPolicy);

        bool accepted = await viewModel.SetGamingModeKeyPolicyAsync(
            blockWindowsKey: true,
            blockAltTab: true,
            CancellationToken.None);

        Assert.True(accepted);
        Assert.Equal(
            new GamingModeKeyPolicy(true, true),
            client.LastProfiles?.PluggedIn
                .EffectiveLaptopBehavior
                .EffectiveGamingModeKeyPolicy);
        Assert.True(viewModel.BlockWindowsKeyInGamingMode);
        Assert.True(viewModel.BlockAltTabInGamingMode);
        Assert.False(viewModel.HasLaptopProfileDraft);
    }

    [Fact]
    public async Task GamingModeKeyPolicy_RestoresTheSavedStateWhenPersistenceFails()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(
                ControlOperationStatus.Failed,
                "Profile storage failed."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                OneShotLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.GamingMode,
                },
                ChargeLimits = ChargeLimits(),
            }) with
        {
            GamingModeEnabled = true,
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.CanEditGamingModeKeyPolicy);

        bool accepted = await viewModel.SetGamingModeKeyPolicyAsync(
            blockWindowsKey: true,
            blockAltTab: true,
            CancellationToken.None);

        Assert.False(accepted);
        Assert.False(viewModel.BlockWindowsKeyInGamingMode);
        Assert.False(viewModel.BlockAltTabInGamingMode);
        Assert.False(viewModel.HasLaptopProfileDraft);
    }

    [Fact]
    public async Task DisplayDiscard_KeepsDraftWhenReadbackIsNotConfirmed()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.IsConnected);
        viewModel.SavedDisplayProfileRestoreRequested += (_, _, _) =>
            Task.FromResult(false);
        viewModel.RecordDisplayProfileChange(
            PowerSource.PluggedIn,
            new DisplayProfileSettings("fixed-240", false, "native"),
            new DisplayProfileSettings("fixed-60", false, "native"));

        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.True(viewModel.HasActiveProfileDraft);
        Assert.Contains(
            "did not confirm",
            viewModel.OperationStatusMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisplayDiscard_CarriesOriginalSourceAndDoesNotReportSuccessAfterTransition()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot pluggedIn = CreateStatus(verified: true, blocked: false);
        client.Publish(pluggedIn);
        await WaitUntilAsync(() => viewModel.IsConnected);
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PowerSource? expectedSource = null;
        viewModel.SavedDisplayProfileRestoreRequested += async (source, _, _) =>
        {
            expectedSource = source;
            restoreStarted.TrySetResult();
            await allowRestore.Task;
            return true;
        };
        viewModel.RecordDisplayProfileChange(
            PowerSource.PluggedIn,
            new DisplayProfileSettings("fixed-240", false, "native"),
            new DisplayProfileSettings("fixed-60", false, "native"));

        Task discard = Assert.IsType<AsyncRelayCommand>(viewModel.DiscardProfileDraftCommand)
            .ExecuteAsync(null);
        await restoreStarted.Task;
        client.Publish(pluggedIn with
        {
            Revision = pluggedIn.Revision + 1,
            ActivePowerSource = PowerSource.Battery,
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.Battery);
        allowRestore.TrySetResult();
        await discard;

        Assert.Equal(PowerSource.PluggedIn, expectedSource);
        Assert.Contains(
            "power source changed",
            viewModel.OperationStatusMessage,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.HasActiveProfileDraft);
    }

    [Fact]
    public async Task StaleDisplayCompletion_IsNotStagedToDestinationProfile()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot original = CreateStatus(verified: true, blocked: false);
        client.Publish(original);
        await WaitUntilAsync(() => viewModel.IsConnected);
        client.Publish(original with
        {
            Revision = original.Revision + 1,
            ActivePowerSource = PowerSource.Battery,
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.Battery);

        viewModel.RecordDisplayProfileChange(
            PowerSource.PluggedIn,
            new DisplayProfileSettings("fixed-240", false, "native"),
            new DisplayProfileSettings("fixed-60", false, "native"));

        Assert.False(viewModel.HasActiveProfileDraft);
    }

    [Fact]
    public async Task ActiveProfileDraft_IsDiscardedWhenConfirmedPowerSourceChanges()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot original = CreateStatus(verified: true, blocked: false);
        client.Publish(original);
        await WaitUntilAsync(() => viewModel.IsConnected);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);
        Assert.True(viewModel.HasActiveProfileDraft);

        client.Publish(original with
        {
            Revision = 2,
            ActivePowerSource = PowerSource.Battery,
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.Battery);

        Assert.False(viewModel.HasActiveProfileDraft);
        Assert.False(viewModel.HasUnsavedProfileChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAtSignInReflectsCurrentShortcutState(bool enabled)
    {
        var registration = new FakeStartupRegistration(enabled);
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new RecordingDispatcher(),
            new FakeDiagnosticArchiveSaver(),
            registration);

        await viewModel.StartAsync();

        Assert.True(viewModel.StartupStateKnown);
        Assert.Equal(enabled, viewModel.StartAtSignIn);
        Assert.Equal(
            "Launch OpenBlade automatically when you sign in.",
            viewModel.StartupDescription);
        Assert.True(viewModel.SetStartAtSignInCommand.CanExecute(!enabled));
        Assert.False(viewModel.SetStartAtSignInCommand.CanExecute(enabled));
    }

    [Fact]
    public async Task StartAtSignInCanChangeWhileServiceIsDisconnected()
    {
        var registration = new FakeStartupRegistration(false);
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new RecordingDispatcher(),
            new FakeDiagnosticArchiveSaver(),
            registration);
        await viewModel.StartAsync();

        await ((AsyncRelayCommand)viewModel.SetStartAtSignInCommand).ExecuteAsync(true);

        Assert.True(viewModel.StartAtSignIn);
        Assert.Equal([true], registration.Requests);
        Assert.Equal("OpenBlade will start when you sign in.", viewModel.StartupStatusMessage);
        Assert.False(viewModel.IsConnected);
    }

    [Fact]
    public async Task StartAtSignInFailureKeepsConfirmedStateAndReportsError()
    {
        var registration = new FakeStartupRegistration(true)
        {
            Failure = new IOException("startup folder unavailable"),
        };
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new RecordingDispatcher(),
            new FakeDiagnosticArchiveSaver(),
            registration);
        await viewModel.StartAsync();

        await ((AsyncRelayCommand)viewModel.SetStartAtSignInCommand).ExecuteAsync(false);

        Assert.True(viewModel.StartAtSignIn);
        Assert.Equal([false], registration.Requests);
        Assert.Contains(
            "startup folder unavailable",
            viewModel.StartupStatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdvisoriesAreVisibleWithoutDisablingAdmittedFanControl()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            conflicts: ["RazerAppEngine", "Razer Synapse Service"]) with
        {
            ControllerDetectionWarning = "Controller detection was incomplete.",
        });
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.True(viewModel.CanControlHardware);
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
        Assert.True(viewModel.HasControllerAdvisory);
        Assert.Equal(
            "Another controller may change laptop settings: RazerAppEngine, Razer Synapse Service. OpenBlade yields automatic keyboard lighting to Synapse while external lighting override is off. Confirmed external changes pause automatic profile updates; manual actions still apply.",
            viewModel.ControllerAdvisoryText);
        Assert.Equal("Other controller may be active", viewModel.LightingSourceLabel);
        Assert.True(viewModel.HasControllerDetectionWarning);
        Assert.Equal("Controller detection was incomplete.", viewModel.ControllerDetectionWarningText);
        Assert.True(viewModel.HasAttention);
        Assert.Equal("Controller detection needs attention", viewModel.AttentionSummary);
        Assert.Equal("Check status", viewModel.AttentionBadgeLabel);
        Assert.Equal(
            "RazerAppEngine, Razer Synapse Service; check incomplete: Controller detection was incomplete",
            viewModel.TroubleshootingOtherRazerSoftwareStatus);
    }

    [Fact]
    public async Task NonCompetingRazerHelperDoesNotClaimLightingOwnership()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            conflicts: ["Razer Central Service"]);
        client.Publish(status with
        {
            ConfirmedState = status.ConfirmedState! with { Lighting = null },
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        });
        await WaitUntilAsync(() => viewModel.HasControllerAdvisory);

        Assert.True(viewModel.HasControllerAdvisory);
        Assert.Equal("Controls unavailable", viewModel.LightingSourceLabel);
        Assert.DoesNotContain(
            "yields automatic keyboard lighting",
            viewModel.ControllerAdvisoryText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttentionTextClearsWhenTheLastWarningClears()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            conflicts: ["RazerAppEngine"]));
        await WaitUntilAsync(() => viewModel.HasAttention);
        Assert.Equal("Another Razer app is running", viewModel.AttentionSummary);
        Assert.Equal("Razer app active", viewModel.AttentionBadgeLabel);

        client.Publish(CreateStatus(verified: true, blocked: false) with { Revision = 2 });
        await WaitUntilAsync(() => !viewModel.HasAttention);

        Assert.Empty(viewModel.AttentionSummary);
        Assert.Empty(viewModel.AttentionBadgeLabel);
        Assert.Empty(viewModel.AttentionDetails);
    }

    [Fact]
    public async Task AttentionPresentationPreservesRemainingWarningAndClearsOnDisconnect()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            conflicts: ["RazerAppEngine"]) with
        {
            ExternalChanges = [DeviceSubsystem.Fans],
        });
        await WaitUntilAsync(() => viewModel.HasExternalChanges);
        Assert.Equal(
            "Laptop controls changed outside OpenBlade",
            viewModel.AttentionSummary);

        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            conflicts: ["RazerAppEngine"]) with
        {
            Revision = 2,
        });
        await WaitUntilAsync(() => !viewModel.HasExternalChanges);

        Assert.True(viewModel.HasAttention);
        Assert.Equal("Another Razer app is running", viewModel.AttentionSummary);
        Assert.Equal("Razer app active", viewModel.AttentionBadgeLabel);
        Assert.NotEmpty(viewModel.AttentionDetails);

        client.CompleteWatch();
        await WaitUntilAsync(() => !viewModel.IsConnected);

        Assert.False(viewModel.HasAttention);
        Assert.Empty(viewModel.AttentionSummary);
        Assert.Empty(viewModel.AttentionBadgeLabel);
        Assert.Empty(viewModel.AttentionDetails);
    }

    [Fact]
    public async Task TroubleshootingOtherRazerSoftware_IncludesDetectionFailureAndRecovers()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ControllerDetectionWarning = "Controller detection was incomplete.",
        });
        await WaitUntilAsync(() => viewModel.IsConnected);
        Assert.Equal(
            "Could not check: Controller detection was incomplete",
            viewModel.TroubleshootingOtherRazerSoftwareStatus);

        client.Publish(CreateStatus(verified: true, blocked: false) with { Revision = 2 });
        await WaitUntilAsync(() => !viewModel.HasControllerDetectionWarning);
        Assert.Equal("None", viewModel.TroubleshootingOtherRazerSoftwareStatus);
    }

    [Fact]
    public async Task ConfirmedValuesOverrideDesiredWithoutResettingSettingsDrafts()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        viewModel.ProfileChargeLimitEditor = 75;
        await viewModel.StartAsync();
        LightingSettings confirmedLighting = new(
            LightingEffect.Static,
            new RgbColor(10, 20, 30),
            40);
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ConfirmedState = new ConfirmedDeviceState(
                PerformancePreset.Performance,
                FanMode.Maximum,
                new ConfirmedBatteryState(true, 65, false),
                confirmedLighting),
            ExternalChanges = [DeviceSubsystem.Lighting, DeviceSubsystem.Fans],
        });
        await WaitUntilAsync(() => viewModel.SelectedFanMode == FanMode.Maximum);

        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);
        Assert.Equal(FanMode.Maximum, viewModel.SelectedFanMode);
        Assert.Equal(confirmedLighting, viewModel.SelectedLighting);
        Assert.Equal(65, viewModel.ChargeLimitPercent);
        Assert.Equal(75, viewModel.ProfileChargeLimitEditor);
        Assert.Equal("Current setting", viewModel.PerformanceSourceLabel);
        Assert.Equal("Changed outside OpenBlade", viewModel.FanSourceLabel);
        Assert.Equal("Changed outside OpenBlade", viewModel.LightingSourceLabel);
        Assert.Equal(
            "Current setting · Charge limit 65%",
            viewModel.BatterySourceLabel);
        Assert.True(viewModel.HasExternalChanges);
        Assert.Equal("Changed outside OpenBlade", viewModel.AttentionBadgeLabel);
        Assert.Equal(
            "Laptop controls changed outside OpenBlade",
            viewModel.AttentionSummary);
        Assert.Equal(
            "Changed outside OpenBlade: Fans, Keyboard lighting. Your saved profiles are unchanged. Automatic profile updates for these controls are paused until you change them in OpenBlade.",
            viewModel.ExternalChangesText);
    }

    [Fact]
    public async Task ReapplyChangedSettings_IsAvailableOnlyForControllableWarnings()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(
                ControlOperationStatus.Applied,
                "OpenBlade reapplied Fans from the current settings."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false) with
        {
            ExternalChanges = [DeviceSubsystem.Fans],
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.CanReapplyChangedSettings);

        bool accepted = await viewModel.ReapplyChangedSettingsAsync(
            CancellationToken.None);

        Assert.True(accepted);
        Assert.Equal(1, client.ReapplyChangedSettingsCalls);
        Assert.False(viewModel.IsReapplyChangedSettingsOperationInProgress);
        Assert.Equal(
            "OpenBlade reapplied Fans from the current settings.",
            viewModel.OperationStatusMessage);

        client.Publish(status with { Revision = 2, WritesBlocked = true });
        await WaitUntilAsync(() => !viewModel.CanReapplyChangedSettings);

        client.Publish(status with
        {
            Revision = 3,
            ExternalChanges = [DeviceSubsystem.Lighting],
            KeyboardLightingControlMode =
                KeyboardLightingControlMode.WindowsDynamicLighting,
        });
        await WaitUntilAsync(() => viewModel.HasExternalChanges);
        Assert.False(viewModel.CanReapplyChangedSettings);

        client.Publish(status with
        {
            Revision = 4,
            ExternalChanges = [DeviceSubsystem.Lighting, DeviceSubsystem.Fans],
            KeyboardLightingControlMode =
                KeyboardLightingControlMode.WindowsDynamicLighting,
        });
        await WaitUntilAsync(() => viewModel.CanReapplyChangedSettings);
    }

    [Fact]
    public async Task UsbC_InvalidObservedThermalStateKeepsProfileSelectionPresentable()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        PowerProfile usbC = baseline.Profiles.UsbC;
        client.Publish(baseline with
        {
            ActivePowerSource = PowerSource.UsbC,
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    usbC.Performance,
                    SettingValueSource.PowerProfile),
                Fans = new ResolvedSetting<FanMode>(
                    usbC.Fans,
                    SettingValueSource.PowerProfile),
            },
            ConfirmedState = new ConfirmedDeviceState(
                PerformancePreset.Performance,
                FanMode.Maximum,
                new ConfirmedBatteryState(true, 80, false),
                usbC.Lighting),
            ExternalChanges = [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
        });
        await WaitUntilAsync(() =>
            viewModel.IsConnected && viewModel.ActivePowerSource == PowerSource.UsbC);

        Assert.Equal(PerformancePreset.Balanced, viewModel.SelectedPerformance);
        Assert.Equal("Balanced", viewModel.ActivePerformanceLabel);
        Assert.Equal(
            PerformancePreset.Balanced,
            Assert.IsType<PerformancePresetOption>(viewModel.SelectedPerformanceOption).Value);
        Assert.Equal(FanMode.Automatic, viewModel.SelectedFanMode);
        Assert.True(viewModel.HasExternalChanges);
        Assert.Contains("Power mode", viewModel.ExternalChangesText, StringComparison.Ordinal);
        Assert.Contains("Fans", viewModel.ExternalChangesText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveChanges_FormatsReactiveLightingForPeople()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        LightingSettings reactive = LightingSettingsFactory.Reactive(
            new RgbColor(0, 255, 0),
            77,
            ReactiveLightingDuration.Medium);
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            LightingOverride = new ManualOverride<LightingSettings>(
                reactive,
                OverrideLifetime.UntilPowerSourceChange),
        });
        await WaitUntilAsync(() => viewModel.HasActiveChanges);

        const string expected =
            "Keyboard lighting: Reactive | 77% | medium, #00FF00 until the power source changes";
        Assert.Equal(expected, viewModel.ActiveChangesText);
        Assert.Contains(
            "Lighting: Reactive | 77% | medium, #00FF00 until the power source changes",
            viewModel.ManualOverrideStatus,
            StringComparison.Ordinal);
        Assert.DoesNotContain("LightingSettings {", viewModel.ActiveChangesText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveChanges_IncludesUnsavedBatteryOverrides()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            BatteryProtectionOverride = false,
            BatteryPolicyOverride = BatteryPolicy.Create(75),
        });
        await WaitUntilAsync(() => viewModel.HasActiveChanges);

        Assert.Contains(
            "Battery protection: Off (not saved to profile)",
            viewModel.ActiveChangesText,
            StringComparison.Ordinal);
        Assert.Contains(
            "Charge limit: 75% not saved to profile",
            viewModel.ActiveChangesText,
            StringComparison.Ordinal);
        Assert.Contains(
            "Battery protection: Off (not saved to profile)",
            viewModel.ManualOverrideStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearManualOverrides_RestoresSavedBatterySettings()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            BatteryProtectionOverride = false,
            BatteryPolicyOverride = BatteryPolicy.Create(75),
        });
        await WaitUntilAsync(() => viewModel.HasActiveChanges);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ClearManualOverridesCommand)
            .ExecuteAsync(null);

        Assert.True(client.BatteryProtectionEnabled);
        Assert.Equal(BatteryPolicy.Create(80), client.BatteryPolicy);
        Assert.Equal("Manual overrides cleared.", viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task ConfirmedProtectionOffKeepsDesiredFallbackAndReportsProtectionOff()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ConfirmedState = new ConfirmedDeviceState(
                null,
                null,
                new ConfirmedBatteryState(false, null, false),
                null),
        });
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.Equal(80, viewModel.ChargeLimitPercent);
        Assert.Contains("protection off", viewModel.BatterySourceLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full charge", viewModel.BatterySourceLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExternallyChangedProtectionOffStillReportsProtectionOff()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ConfirmedState = new ConfirmedDeviceState(
                null,
                null,
                new ConfirmedBatteryState(false, null, false),
                null),
            ExternalChanges = [DeviceSubsystem.Battery],
            DesiredState = CreateStatus(true, false).DesiredState with
            {
                Battery = new DesiredBatteryState(
                    BatteryPolicy.Create(65),
                    protectionEnabled: true,
                    temporaryFullCharge: false),
            },
        });
        await WaitUntilAsync(() => viewModel.BatterySourceLabel.Contains(
            "protection off",
            StringComparison.OrdinalIgnoreCase));

        Assert.Equal(65, viewModel.ChargeLimitPercent);
        Assert.Contains("Changed outside OpenBlade", viewModel.BatterySourceLabel, StringComparison.Ordinal);
        Assert.Contains("protection off", viewModel.BatterySourceLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full charge", viewModel.BatterySourceLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OlderDirectObservationCannotRegressNewerSubscriptionRevision()
    {
        var client = new FakeServiceClient();
        var observation = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PendingObservation = observation;
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(true, false) with
        {
            Revision = 9,
            Message = "newer subscription",
            ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Performance, null, null, null),
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "newer subscription");

        Task direct = viewModel.ObserveStateAsync(CancellationToken.None);
        await client.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        observation.TrySetResult(CreateStatus(true, false) with
        {
            Revision = 8,
            Message = "older direct",
            ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Silent, null, null, null),
        });
        await direct;

        Assert.Equal("newer subscription", viewModel.ConnectionStatus);
        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);
    }

    [Fact]
    public async Task DirectObservationSkipsServiceCallWhenLiveReadingsAreDisabled()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task enabledRequest = viewModel.ObserveStateAsync(CancellationToken.None);
        client.PendingObservation.SetResult(CreateStatus(true, false));
        await enabledRequest;

        viewModel.LiveReadingsEnabled = false;
        await viewModel.ObserveStateAsync(CancellationToken.None);

        Assert.Equal([true], client.IncludeLiveReadingsRequests);
        Assert.Null(viewModel.LiveReadings);
    }

    [Fact]
    public async Task ObservationRequestsOnlySensorsNeededByTheVisibleUiOrTray()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        viewModel.ApplyTelemetryPreferences(
            [LiveReadingItemKind.CpuTemperature, LiveReadingItemKind.GpuTemperature],
            [
                new(TrayHoverLabelItemKind.AppName, true),
                new(TrayHoverLabelItemKind.BatteryPower, true),
            ]);

        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task visible = viewModel.ObserveStateAsync(CancellationToken.None);
        client.PendingObservation.SetResult(CreateStatus(true, false));
        await visible;

        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task hidden = viewModel.ObserveTrayStateAsync(CancellationToken.None);
        client.PendingObservation.SetResult(CreateStatus(true, false));
        await hidden;

        Assert.Equal(
            [
                LiveReadingSensors.BatteryPower
                    | LiveReadingSensors.CpuTemperature
                    | LiveReadingSensors.GpuTemperature,
                LiveReadingSensors.BatteryPower,
            ],
            client.RequestedLiveReadings);
        Assert.True(viewModel.HasEnabledTrayHoverLiveReadings);

        viewModel.ApplyTelemetryPreferences(
            [LiveReadingItemKind.CpuTemperature],
            [new(TrayHoverLabelItemKind.AppName, true)]);
        Assert.False(viewModel.HasEnabledTrayHoverLiveReadings);
    }

    [Fact]
    public async Task LiveReadingsRefreshOptionsMapCadenceAndOffToTelemetryGate()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        Assert.Equal(
            ["Off", "Every second", "Every 2 seconds", "Every 5 seconds", "Every 10 seconds"],
            viewModel.LiveReadingsRefreshOptions.Select(option => option.Label));
        Assert.Equal(
            LiveReadingsCadence.EveryTwoSeconds,
            viewModel.SelectedLiveReadingsRefreshOption.Cadence);
        Assert.Equal(TimeSpan.FromSeconds(2), viewModel.LiveReadingsObservationInterval);

        viewModel.SelectedLiveReadingsRefreshOption =
            viewModel.LiveReadingsRefreshOptions.Single(
                option => option.Cadence == LiveReadingsCadence.EverySecond);
        Assert.True(viewModel.LiveReadingsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(1), viewModel.LiveReadingsObservationInterval);

        viewModel.SelectedLiveReadingsRefreshOption =
            viewModel.LiveReadingsRefreshOptions.Single(
                option => option.Cadence == LiveReadingsCadence.Off);
        Assert.False(viewModel.LiveReadingsEnabled);
        Assert.Equal(Timeout.InfiniteTimeSpan, viewModel.LiveReadingsObservationInterval);

        int observationsBeforeDisabledCheck = client.RequestedLiveReadings.Count;
        await viewModel.ObserveStateAsync(CancellationToken.None);
        await viewModel.ObserveTrayStateAsync(CancellationToken.None);
        Assert.Equal(observationsBeforeDisabledCheck, client.RequestedLiveReadings.Count);
    }

    [Fact]
    public async Task SuccessfulRepeatedLiveChecksAdvanceTooltipEvenWhenValuesMatch()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 17, 20, 15, 10, TimeSpan.Zero));
        await using var viewModel = new MainViewModel(
            client,
            new RecordingDispatcher(),
            time);
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(true, false) with
        {
            Revision = 4,
            LiveReadings = new LiveReadings(37_046, 2200, 2100),
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.Equal(
            "Waiting for the first live-reading check.",
            viewModel.LiveReadingsRefreshToolTip);

        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = viewModel.ObserveStateAsync(CancellationToken.None);
        client.PendingObservation.SetResult(status);
        await first;
        string firstTooltip = viewModel.LiveReadingsRefreshToolTip;

        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        time.Advance(TimeSpan.FromSeconds(5));
        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task second = viewModel.ObserveStateAsync(CancellationToken.None);
        client.PendingObservation.SetResult(status);
        await second;

        Assert.NotEqual(firstTooltip, viewModel.LiveReadingsRefreshToolTip);
        Assert.Contains(
            $"{Environment.NewLine}Values change only when Windows or firmware reports new data.",
            viewModel.LiveReadingsRefreshToolTip,
            StringComparison.Ordinal);
        Assert.Contains(nameof(viewModel.LiveReadingsRefreshToolTip), changed);

        viewModel.LiveReadingsEnabled = false;
        Assert.Equal(
            "Waiting for the first live-reading check.",
            viewModel.LiveReadingsRefreshToolTip);
    }

    [Fact]
    public async Task HiddenTrayObservationUpdatesTooltipWithoutNotifyingXamlPresentation()
    {
        var client = new FakeServiceClient();
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero));
        await using var viewModel = new MainViewModel(
            client,
            new RecordingDispatcher(),
            time);
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(true, false) with
        {
            Revision = 4,
            LiveReadings = new LiveReadings(-18_000, 2200, 2100, 46, 51),
        };
        var baselineApplied = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBaselinePropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(viewModel.HasLightingProfileActionOpportunity))
            {
                baselineApplied.TrySetResult();
            }
        }
        viewModel.PropertyChanged += OnBaselinePropertyChanged;
        client.Publish(baseline);
        await baselineApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.PropertyChanged -= OnBaselinePropertyChanged;
        Assert.Equal(baseline.LiveReadings, viewModel.LiveReadings);

        viewModel.SetDashboardTelemetryPresentationActive(false);
        var changed = new ConcurrentPropertySet();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        ServiceStatusSnapshot update = baseline with
        {
            Revision = 5,
            LiveReadings = new LiveReadings(-18_500, 2250, 2150, 47, 52),
        };
        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task observation = viewModel.ObserveTrayStateAsync(CancellationToken.None);
        client.PendingObservation.SetResult(update);
        await observation;

        Assert.Equal(update.LiveReadings, viewModel.LiveReadings);
        Assert.Contains(nameof(viewModel.TrayHoverLabelText), changed);
        Assert.DoesNotContain(nameof(viewModel.LiveReadings), changed);
        Assert.DoesNotContain(nameof(viewModel.LiveReadingsRefreshToolTip), changed);
        Assert.DoesNotContain(nameof(viewModel.BatteryPowerFlowLabel), changed);
        Assert.DoesNotContain(nameof(viewModel.Fan1RpmValue), changed);
        Assert.DoesNotContain(nameof(viewModel.Fan2RpmValue), changed);
        Assert.DoesNotContain(nameof(viewModel.CpuTemperatureValue), changed);
        Assert.DoesNotContain(nameof(viewModel.GpuTemperatureValue), changed);
        Assert.DoesNotContain(nameof(viewModel.VisibleDashboardTelemetryGroups), changed);
        Assert.NotEqual(
            "Waiting for the first live-reading check.",
            viewModel.LiveReadingsRefreshToolTip);

        changed.Clear();
        viewModel.SetDashboardTelemetryPresentationActive(true);

        Assert.Contains(nameof(viewModel.LiveReadingsRefreshToolTip), changed);
    }

    [Fact]
    public async Task HiddenTrayObservationWithMetadataChangePublishesFullPresentation()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(true, false) with
        {
            Revision = 4,
            LiveReadings = new LiveReadings(-18_000, 2200, 2100, 46, 51),
        };
        var baselineApplied = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBaselinePropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(viewModel.HasLightingProfileActionOpportunity))
            {
                baselineApplied.TrySetResult();
            }
        }
        viewModel.PropertyChanged += OnBaselinePropertyChanged;
        client.Publish(baseline);
        await baselineApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.PropertyChanged -= OnBaselinePropertyChanged;

        viewModel.SetDashboardTelemetryPresentationActive(false);
        var changed = new ConcurrentPropertySet();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        ServiceStatusSnapshot update = baseline with
        {
            Revision = 5,
            DeviceVerified = false,
            LiveReadings = null,
        };
        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task observation = viewModel.ObserveTrayStateAsync(CancellationToken.None);
        client.PendingObservation.SetResult(update);
        await observation;

        Assert.Null(viewModel.LiveReadings);
        Assert.Contains(nameof(viewModel.LiveReadings), changed);
        Assert.Contains(nameof(viewModel.IsServiceUnavailable), changed);
        Assert.Contains(nameof(viewModel.ControlAccessStatus), changed);
        Assert.Contains(nameof(viewModel.LiveReadingsRefreshToolTip), changed);
    }

    [Fact]
    public async Task DirectRequestBegunDisconnectedCannotEstablishEpochOrBlockRevisionZero()
    {
        var client = new FakeServiceClient();
        var observation = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PendingObservation = observation;
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        Task direct = viewModel.ObserveStateAsync(CancellationToken.None);
        await client.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        observation.TrySetResult(CreateStatus(true, false) with
        {
            Revision = 8,
            Message = "disconnected direct",
        });
        await direct;

        Assert.False(viewModel.IsConnected);
        client.Publish(CreateStatus(true, false) with
        {
            Revision = 0,
            Message = "subscription revision zero",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "subscription revision zero");

        Assert.True(viewModel.IsConnected);
    }

    [Fact]
    public async Task DirectResponseFromPreviousGenerationCannotOverrideRestartedRevisionZero()
    {
        var client = new FakeServiceClient();
        var observation = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PendingObservation = observation;
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(true, false) with
        {
            Revision = 7,
            Message = "original service",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "original service");

        Task direct = viewModel.ObserveStateAsync(CancellationToken.None);
        await client.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        client.PublishReconnecting();
        await WaitUntilAsync(() => !viewModel.IsConnected);
        client.Publish(CreateStatus(true, false) with
        {
            Revision = 0,
            Message = "restarted service",
            ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Silent, null, null, null),
        });
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "restarted service"
            && viewModel.SelectedPerformance == PerformancePreset.Silent);

        observation.TrySetResult(CreateStatus(true, false) with
        {
            Revision = 8,
            Message = "stale direct",
            ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Performance, null, null, null),
        });
        await direct;

        Assert.Equal("restarted service", viewModel.ConnectionStatus);
        Assert.Equal(PerformancePreset.Silent, viewModel.SelectedPerformance);
    }

    [Fact]
    public async Task DirectErrorFromPreviousGenerationCannotDisconnectRestartedSubscription()
    {
        var client = new FakeServiceClient();
        var observation = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PendingObservation = observation;
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(true, false) with
        {
            Revision = 7,
            Message = "original service",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "original service");

        Task direct = viewModel.ObserveStateAsync(CancellationToken.None);
        await client.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        client.PublishReconnecting();
        await WaitUntilAsync(() => !viewModel.IsConnected);
        client.Publish(CreateStatus(true, false) with
        {
            Revision = 0,
            Message = "restarted service",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "restarted service");

        observation.TrySetException(new IOException("stale direct failure"));
        IOException error = await Assert.ThrowsAsync<IOException>(() => direct);

        Assert.Equal("stale direct failure", error.Message);
        Assert.True(viewModel.IsConnected);
        Assert.Equal("restarted service", viewModel.ConnectionStatus);
    }

    [Fact]
    public async Task DirectFailureCannotDisconnectOrRegressHealthyNewerSubscriptionState()
    {
        var client = new FakeServiceClient();
        var observation = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PendingObservation = observation;
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(true, false) with
        {
            Revision = 7,
            Message = "subscription revision seven",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "subscription revision seven");

        Task direct = viewModel.ObserveStateAsync(CancellationToken.None);
        await client.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        client.Publish(CreateStatus(true, false) with
        {
            Revision = 8,
            Message = "subscription revision eight",
            ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Performance, null, null, null),
        });
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "subscription revision eight"
            && viewModel.SelectedPerformance == PerformancePreset.Performance);

        observation.TrySetException(new IOException("direct observation failed"));
        IOException error = await Assert.ThrowsAsync<IOException>(() => direct);

        Assert.Equal("direct observation failed", error.Message);
        Assert.True(viewModel.IsConnected);
        Assert.Equal("subscription revision eight", viewModel.ConnectionStatus);
        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);
    }

    [Fact]
    public async Task DirectSuccessAfterFailureAppliesNewerConfirmedStateAndWarning()
    {
        var client = new FakeServiceClient();
        var failure = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PendingObservation = failure;
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(true, false) with
        {
            Revision = 7,
            Message = "healthy subscription",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "healthy subscription");

        Task failedDirect = viewModel.ObserveStateAsync(CancellationToken.None);
        await client.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        failure.TrySetException(new IOException("temporary observation failure"));
        IOException error = await Assert.ThrowsAsync<IOException>(() => failedDirect);
        Assert.Equal("temporary observation failure", error.Message);

        var recovery = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PendingObservation = recovery;
        Task recoveredDirect = viewModel.ObserveStateAsync(CancellationToken.None);
        await WaitUntilAsync(() => client.ObservationCalls == 2);
        recovery.TrySetResult(CreateStatus(true, false) with
        {
            Revision = 9,
            Message = "recovered observation",
            ConfirmedState = new ConfirmedDeviceState(PerformancePreset.Performance, null, null, null),
            ControllerDetectionWarning = "Observation recovered with advisory.",
        });
        await recoveredDirect;

        Assert.True(viewModel.IsConnected);
        Assert.Equal("recovered observation", viewModel.ConnectionStatus);
        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);
        Assert.True(viewModel.HasControllerDetectionWarning);
        Assert.Equal(
            "Observation recovered with advisory.",
            viewModel.ControllerDetectionWarningText);
    }

    [Fact]
    public async Task StartAsync_ConsumesWatchOnceAndMarshalsExactConflictStatus()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);

        await viewModel.StartAsync();
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: false,
            blocked: true,
            conflicts: ["Razer Central Service", "Razer Synapse Service"],
            message: "Razer controller conflicts detected: Razer Central Service, Razer Synapse Service."));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.Equal(1, client.WatchCalls);
        Assert.True(dispatcher.Calls > 0);
        Assert.True(viewModel.IsConnected);
        Assert.True(viewModel.IsReadOnly);
        Assert.Contains("Razer Central Service", viewModel.ControllerAdvisoryText, StringComparison.Ordinal);
        Assert.Contains("Razer Synapse Service", viewModel.ControllerAdvisoryText, StringComparison.Ordinal);
        Assert.Equal(
            "Razer controller conflicts detected: Razer Central Service, Razer Synapse Service.",
            viewModel.ConnectionStatus);
    }

    [Fact]
    public async Task Capabilities_DriveVisibilityAndCommandCanExecute()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(),
                SupportsBatteryProtectionOff: true,
                SupportsTemporaryFullCharge: true)
            {
                OneShotLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.StartupAnimation,
                    OneShotLaptopControl.FunctionKeyPrimary,
                    OneShotLaptopControl.GamingMode,
                },
            });
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.SupportsFunctionKeyPrimary);

        Assert.Equal("Checking", viewModel.FunctionKeyPrimaryStateLabel);
        Assert.DoesNotContain("â", viewModel.FunctionKeyPrimaryStateLabel, StringComparison.Ordinal);
        Assert.True(viewModel.IsFunctionKeyFieldLoading);
        Assert.False(viewModel.CanEditFunctionKeyPrimary);
        Assert.False(viewModel.ApplyOneShotLaptopControlCommand.CanExecute(
            viewModel.FunctionKeysPrimaryAction));

        client.Publish(status with
        {
            Revision = status.Revision + 1,
            StartupAnimationEnabled = true,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled = false,
        });
        await WaitUntilAsync(() =>
            viewModel.StartupAnimationEnabled is true
            && viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.FunctionKeys
            && viewModel.GamingModeEnabled is false
            && viewModel.IsPerformanceVisible);

        Assert.True(viewModel.IsPerformanceVisible);
        Assert.True(viewModel.IsFanControlVisible);
        Assert.True(viewModel.IsLightingVisible);
        Assert.Equal([PerformancePreset.Balanced], viewModel.AvailablePerformancePresets);
        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Balanced));
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Performance));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
        Assert.False(viewModel.SetFanModeCommand.CanExecute(FanMode.Maximum));
        Assert.True(viewModel.SetChargeLimitCommand.CanExecute(75));
        Assert.False(viewModel.SetChargeLimitCommand.CanExecute(77));
        Assert.True(viewModel.SetLightingCommand.CanExecute(
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting));
        Assert.True(viewModel.HasOneShotLaptopControls);
        Assert.True(viewModel.HasKeyboardLaptopControls);
        Assert.True(viewModel.SupportsStartupAnimation);
        Assert.True(viewModel.SupportsFunctionKeyPrimary);
        Assert.True(viewModel.SupportsGamingMode);
        Assert.True(viewModel.StartupAnimationEnabled);
        Assert.True(viewModel.StartupAnimationToggleIsOn);
        Assert.Equal("Function keys", viewModel.FunctionKeyPrimaryStateLabel);
        Assert.False(viewModel.IsFunctionKeyFieldLoading);
        Assert.True(viewModel.CanEditFunctionKeyPrimary);
        Assert.False(viewModel.GamingModeToggleIsOn);
        var oneShot = new ApplyOneShotLaptopControl(
            OneShotLaptopControl.StartupAnimation,
            Enabled: false);
        Assert.True(viewModel.ApplyOneShotLaptopControlCommand.CanExecute(oneShot));
        Assert.True(viewModel.ApplyOneShotLaptopControlCommand.CanExecute(
            viewModel.MultimediaKeysPrimaryAction));

        await Assert.IsType<AsyncRelayCommand>(viewModel.ApplyOneShotLaptopControlCommand)
            .ExecuteAsync(oneShot);

        Assert.Equal(oneShot, client.OneShotLaptopControl);
        Assert.True(viewModel.HasLaptopProfileDraft);
        Assert.True(viewModel.SaveLaptopProfileDraftCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveLaptopProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.False(viewModel.HasLaptopProfileDraft);
        Assert.Equal(
            new LaptopBehaviorProfileSettings(
                StartupAnimationEnabled: false,
                FunctionKeyPrimary: FunctionKeyPrimaryMode.FunctionKeys,
                GamingModeEnabled: false),
            client.LastProfiles?.PluggedIn.LaptopBehavior);

        client.Publish(status with
        {
            Revision = status.Revision + 2,
            StartupAnimationEnabled = null,
            FunctionKeyPrimary = null,
            GamingModeEnabled = null,
        });
        await WaitUntilAsync(() => viewModel.IsFunctionKeyFieldLoading);

        Assert.True(viewModel.IsStartupAnimationFieldLoading);
        Assert.True(viewModel.IsGamingModeFieldLoading);
        Assert.False(viewModel.StartupAnimationToggleIsOn);
        Assert.Equal("Function keys", viewModel.FunctionKeyPrimaryStateLabel);
        Assert.False(viewModel.GamingModeToggleIsOn);
        Assert.False(viewModel.CanEditStartupAnimation);
        Assert.False(viewModel.CanEditFunctionKeyPrimary);
        Assert.False(viewModel.CanEditGamingMode);
        Assert.False(viewModel.ApplyOneShotLaptopControlCommand.CanExecute(
            viewModel.MultimediaKeysPrimaryAction));
    }

    [Fact]
    public async Task OneShotFirmwareActionShowsRequestedValueWhileReadbackIsLoading()
    {
        var client = new FakeServiceClient { BlockMutations = true };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(),
                SupportsBatteryProtectionOff: true,
                SupportsTemporaryFullCharge: true)
            {
                OneShotLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.FunctionKeyPrimary,
                },
            }) with
        {
            FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys,
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.FunctionKeys);

        Task apply = Assert.IsType<AsyncRelayCommand>(viewModel.ApplyOneShotLaptopControlCommand)
            .ExecuteAsync(viewModel.MultimediaKeysPrimaryAction);
        await client.MutationStarted.Task;

        Assert.True(viewModel.IsFunctionKeyFieldLoading);
        Assert.False(viewModel.CanEditFunctionKeyPrimary);
        Assert.Equal("Media keys", viewModel.FunctionKeyPrimaryStateLabel);
        Assert.False(viewModel.ApplyOneShotLaptopControlCommand.CanExecute(
            viewModel.FunctionKeysPrimaryAction));

        client.Publish(status with
        {
            Revision = status.Revision + 1,
            FunctionKeyPrimary = null,
        });
        await WaitUntilAsync(() => viewModel.IsFunctionKeyFieldLoading);

        Assert.Equal("Media keys", viewModel.FunctionKeyPrimaryStateLabel);

        client.CompleteMutation();
        await apply;
    }

    [Fact]
    public async Task TransientFirmwareRefreshKeepsCurrentOneShotSelectionsWhileLoading()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(),
                SupportsBatteryProtectionOff: true,
                SupportsTemporaryFullCharge: true)
            {
                OneShotLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.StartupAnimation,
                    OneShotLaptopControl.FunctionKeyPrimary,
                    OneShotLaptopControl.GamingMode,
                },
            }) with
        {
            StartupAnimationEnabled = true,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys,
            GamingModeEnabled = true,
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.MultimediaKeys);

        client.Publish(status with
        {
            Revision = status.Revision + 1,
            Message = "Refreshing laptop controls after a temporary device-access failure.",
            WritesBlocked = true,
            Capabilities = DeviceCapabilities.Unavailable(),
            StartupAnimationEnabled = null,
            FunctionKeyPrimary = null,
            GamingModeEnabled = null,
        });
        await WaitUntilAsync(() => viewModel.IsFunctionKeyFieldLoading);

        Assert.True(viewModel.SupportsStartupAnimation);
        Assert.True(viewModel.SupportsFunctionKeyPrimary);
        Assert.True(viewModel.SupportsGamingMode);
        Assert.True(viewModel.StartupAnimationToggleIsOn);
        Assert.Equal("Media keys", viewModel.FunctionKeyPrimaryStateLabel);
        Assert.True(viewModel.GamingModeToggleIsOn);
        Assert.True(viewModel.IsStartupAnimationFieldLoading);
        Assert.True(viewModel.IsFunctionKeyFieldLoading);
        Assert.True(viewModel.IsGamingModeFieldLoading);
        Assert.False(viewModel.CanEditStartupAnimation);
        Assert.False(viewModel.CanEditFunctionKeyPrimary);
        Assert.False(viewModel.CanEditGamingMode);
    }

    [Fact]
    public async Task OneShotFirmwareActionRestoresPreviousValueWhenRejected()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(
                ControlOperationStatus.Failed,
                "Firmware rejected the request."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(),
                SupportsBatteryProtectionOff: true,
                SupportsTemporaryFullCharge: true)
            {
                OneShotLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.FunctionKeyPrimary,
                },
            }) with
        {
            FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys,
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.FunctionKeys);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ApplyOneShotLaptopControlCommand)
            .ExecuteAsync(viewModel.MultimediaKeysPrimaryAction);

        Assert.Equal("Function keys", viewModel.FunctionKeyPrimaryStateLabel);
        Assert.False(viewModel.IsFunctionKeyFieldLoading);
        Assert.True(viewModel.CanEditFunctionKeyPrimary);
    }

    [Fact]
    public async Task PerformanceOptionsAndCommands_FollowActivePowerSource()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot pluggedIn = CreateStatus(verified: true, blocked: false);
        client.Publish(pluggedIn);
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.Equal(
            [
                PerformancePreset.Balanced,
                PerformancePreset.Silent,
                PerformancePreset.Performance,
                PerformancePreset.Custom,
            ],
            viewModel.AvailableActivePerformanceOptions.Select(option => option.Value));
        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Silent));
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.BatterySaver));
        Assert.Equal("AC profile", viewModel.ActivePowerSourceLabel);
        Assert.Equal("Save to AC profile", viewModel.SaveToActiveProfileLabel);

        client.Publish(pluggedIn with
        {
            Revision = pluggedIn.Revision + 1,
            ActivePowerSource = PowerSource.Battery,
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.Battery);

        Assert.Equal(
            [PerformancePreset.BatteryBalanced, PerformancePreset.BatterySaver],
            viewModel.AvailableActivePerformanceOptions.Select(option => option.Value));
        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.BatterySaver));
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Silent));
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Performance));
        Assert.Equal(
            [FanMode.Automatic],
            viewModel.AvailableActiveFanModeOptions.Select(option => option.Value));
        Assert.False(viewModel.SetFanModeCommand.CanExecute(FanMode.Maximum));
        Assert.Equal("Save to battery profile", viewModel.SaveToActiveProfileLabel);

        client.Publish(pluggedIn with
        {
            Revision = pluggedIn.Revision + 2,
            ActivePowerSource = PowerSource.UsbC,
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.UsbC);

        Assert.Equal(
            [PerformancePreset.Balanced],
            viewModel.AvailableActivePerformanceOptions.Select(option => option.Value));
        Assert.Equal([FanMode.Automatic], viewModel.AvailableActiveFanModes);
        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Balanced));
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Silent));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
        Assert.False(viewModel.SetFanModeCommand.CanExecute(FanMode.Maximum));
        Assert.Equal("USB-C profile", viewModel.ActivePowerSourceLabel);
        Assert.Equal("Save to USB-C profile", viewModel.SaveToActiveProfileLabel);

        client.Publish(pluggedIn with
        {
            Revision = pluggedIn.Revision + 3,
            ActivePowerSource = PowerSource.ExternalUnknown,
        });
        await WaitUntilAsync(() =>
            viewModel.ActivePowerSource == PowerSource.ExternalUnknown);

        Assert.Empty(viewModel.AvailableActivePerformanceOptions);
        Assert.Empty(viewModel.AvailableActiveFanModes);
        Assert.False(viewModel.CanControlHardware);
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Balanced));
        Assert.False(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
        Assert.False(viewModel.SetLightingCommand.CanExecute(
            ProfileDefaults.CreateSafeDefaults().UsbC.Lighting));
        Assert.False(viewModel.ClearManualOverridesCommand.CanExecute(null));
        Assert.Equal("Checking power source", viewModel.ActivePowerSourceLabel);
        Assert.Equal("Save to profile", viewModel.SaveToActiveProfileLabel);

        viewModel.BatteryPerformanceOption = viewModel.AvailableBatteryPerformanceOptions
            .Single(option => option.Value == PerformancePreset.BatteryBalanced);
        Assert.Equal(PerformancePreset.BatteryBalanced, viewModel.BatteryPerformanceEditor);
        Assert.True(viewModel.SaveProfilesCommand.CanExecute(null));

        viewModel.BatteryPerformanceEditor = PerformancePreset.Silent;
        Assert.False(viewModel.SaveProfilesCommand.CanExecute(null));
    }

    [Fact]
    public async Task CustomPerformance_StartupPublishesDependentVisibility()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        int visibleCustomPerformanceNotifications = 0;
        int visibleGpuClockTuningNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsCustomPerformanceVisible)
                && viewModel.IsCustomPerformanceVisible)
            {
                Interlocked.Increment(ref visibleCustomPerformanceNotifications);
            }
            else if (args.PropertyName == nameof(MainViewModel.IsGpuClockTuningAvailable)
                     && viewModel.IsGpuClockTuningAvailable)
            {
                Interlocked.Increment(ref visibleGpuClockTuningNotifications);
            }
        };
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline with
        {
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = CustomPerformanceSettings.Default,
                GpuClockOffsets = new GpuClockOffsetCapabilities(
                    "NVIDIA GPU",
                    new GpuClockOffsetRange(-1000, 400, 0),
                    new GpuClockOffsetRange(-1000, 3000, 0)),
            },
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Custom,
                    SettingValueSource.PowerProfile),
                CustomPerformance = CustomPerformanceSettings.Default,
            },
        });
        await WaitUntilAsync(() => viewModel.IsGpuClockTuningAvailable);

        Assert.True(Volatile.Read(ref visibleCustomPerformanceNotifications) > 0);
        Assert.True(Volatile.Read(ref visibleGpuClockTuningNotifications) > 0);
    }

    [Fact]
    public async Task CustomPerformance_ExposesValidPairsAndSendsTypedOverride()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false) with
        {
            ConfirmedState = CreateStatus(true, false).ConfirmedState! with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = new CustomPerformanceSettings(
                    CustomPerformanceLevel.Medium,
                    CustomPerformanceLevel.High),
            },
            DesiredState = CreateStatus(true, false).DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Custom,
                    SettingValueSource.PowerProfile),
                CustomPerformance = new CustomPerformanceSettings(
                    CustomPerformanceLevel.Medium,
                    CustomPerformanceLevel.High),
            },
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.IsCustomPerformanceVisible);

        Assert.DoesNotContain(
            CustomPerformanceLevel.High,
            viewModel.AvailableCustomCpuLevels);
        var requested = new CustomPerformanceSettings(
            CustomPerformanceLevel.Low,
            CustomPerformanceLevel.High);
        Assert.True(viewModel.SetCustomPerformanceCommand.CanExecute(requested));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetCustomPerformanceCommand)
            .ExecuteAsync(requested);

        Assert.Equal(PerformancePreset.Custom, client.PerformanceOverride!.Value);
        Assert.Equal(requested, client.PerformanceOverride.CustomPerformance);
        Assert.Equal(OverrideLifetime.UntilPowerSourceChange, client.PerformanceOverride.Lifetime);
        Assert.Equal(requested.Cpu, viewModel.SelectedCustomCpuLevel);
        Assert.Equal(requested.Gpu, viewModel.SelectedCustomGpuLevel);
        Assert.True(viewModel.HasThermalProfileDraft);
    }

    [Fact]
    public async Task PerformanceSelection_NormalizesLegacyGpuTuningAfterDraft()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        (ServiceStatusSnapshot status, GpuClockOffsetSettings legacyOffsets) =
            CreateLegacyGpuTuningStatus("Legacy GPU tuning profile ready");
        client.Publish(status);
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "Legacy GPU tuning profile ready");
        Assert.Equal(legacyOffsets, viewModel.CurrentGpuClockOffsets);

        await viewModel.SetGpuClockTuningEnabledAsync(
            enabled: false,
            CancellationToken.None);
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Custom);

        PerformanceOverride requested = Assert.IsType<PerformanceOverride>(
            client.PerformanceOverride);
        Assert.True(requested.IsWellFormed());
        Assert.Null(requested.CustomPerformance?.GpuClockOffsets);
        Assert.Equal(
            new GpuClockTuningSettings(
                Enabled: false,
                legacyOffsets),
            requested.GpuClockTuning);
        // WriteRequestAsync validates the request before serializing it.
        using var encoded = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(
            encoded,
            new SetPerformanceOverrideRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                requested),
            CancellationToken.None);
        Assert.NotEqual(0, encoded.Length);
    }

    [Fact]
    public async Task PerformanceSelection_PromotesDormantLegacyGpuTuningForCustom()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        (ServiceStatusSnapshot status, GpuClockOffsetSettings legacyOffsets) =
            CreateLegacyGpuTuningStatus("Dormant legacy GPU tuning profile ready");
        client.Publish(status);
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "Dormant legacy GPU tuning profile ready");

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Custom);

        PerformanceOverride requested = Assert.IsType<PerformanceOverride>(
            client.PerformanceOverride);
        Assert.Null(requested.CustomPerformance?.GpuClockOffsets);
        Assert.Equal(
            new GpuClockTuningSettings(
                Enabled: true,
                legacyOffsets),
            requested.GpuClockTuning);
    }

    [Fact]
    public async Task ThermalProfileSave_NormalizesLegacyGpuTuningAfterDraft()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        (ServiceStatusSnapshot status, GpuClockOffsetSettings legacyOffsets) =
            CreateLegacyGpuTuningStatus("Legacy GPU tuning save profile ready");
        client.Publish(status);
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "Legacy GPU tuning save profile ready");

        await viewModel.SetGpuClockTuningEnabledAsync(
            enabled: false,
            CancellationToken.None);
        Assert.True(viewModel.SaveThermalProfileDraftCommand.CanExecute(null));
        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveThermalProfileDraftCommand)
            .ExecuteAsync(null);

        PowerProfile saved = Assert.IsType<ProfileSet>(client.LastProfiles).PluggedIn;
        Assert.Null(saved.CustomPerformance?.GpuClockOffsets);
        Assert.Equal(
            new GpuClockTuningSettings(
                Enabled: false,
                legacyOffsets),
            saved.GpuClockTuning);
        Assert.False(viewModel.HasThermalProfileDraft);
    }

    [Fact]
    public async Task PerformanceSelection_StaysRequestedAcrossInFlightStaleStatus()
    {
        var client = new FakeServiceClient { BlockMutations = true };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        Task apply = Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);
        await client.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);

        client.Publish(baseline with
        {
            Revision = baseline.Revision + 1,
            Message = "Intermediate status",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Intermediate status");

        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);

        client.CompleteMutation();
        await apply;

        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);
        Assert.Equal(
            PerformancePreset.Performance,
            viewModel.SelectedPerformanceOption?.Value);
    }

    [Fact]
    public async Task PerformanceSelection_StaysRequestedAcrossPostCompletionStaleStatus()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);

        Assert.False(viewModel.IsPerformanceOperationInProgress);
        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);

        client.Publish(baseline with
        {
            Revision = baseline.Revision + 1,
            Message = "Lagging status",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Lagging status");

        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);

        PerformanceOverride requested = Assert.IsType<PerformanceOverride>(
            client.PerformanceOverride);
        ServiceStatusSnapshot confirmed = baseline with
        {
            Revision = baseline.Revision + 2,
            Message = "Confirmed status",
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Performance,
            },
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Performance,
                    SettingValueSource.TemporaryOverride),
            },
            PerformanceOverride = requested,
        };
        client.Publish(confirmed);
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Confirmed status");

        client.Publish(confirmed with
        {
            Revision = baseline.Revision + 3,
            Message = "Later external status",
            ConfirmedState = confirmed.ConfirmedState! with
            {
                Performance = PerformancePreset.Silent,
            },
            DesiredState = confirmed.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Silent,
                    SettingValueSource.PowerProfile),
            },
            PerformanceOverride = null,
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Later external status");

        Assert.Equal(PerformancePreset.Silent, viewModel.SelectedPerformance);
    }

    [Fact]
    public async Task CustomPerformanceSelection_ReleasesForEchoedOverrideWithRicherReadback()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        ServiceStatusSnapshot baseline = initial with
        {
            ConfirmedState = initial.ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
            },
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Custom);

        PerformanceOverride requested = Assert.IsType<PerformanceOverride>(
            client.PerformanceOverride);
        Assert.Null(requested.CustomPerformance!.GpuClockOffsets);

        client.Publish(baseline with
        {
            Revision = baseline.Revision + 1,
            Message = "Lagging custom status",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Lagging custom status");

        Assert.Equal(requested.CustomPerformance.Cpu, viewModel.SelectedCustomCpuLevel);
        Assert.Equal(requested.CustomPerformance.Gpu, viewModel.SelectedCustomGpuLevel);

        CustomPerformanceSettings richerReadback = requested.CustomPerformance with
        {
            GpuClockOffsets = GpuClockOffsetSettings.Default,
        };
        ServiceStatusSnapshot confirmed = baseline with
        {
            Revision = baseline.Revision + 2,
            Message = "Confirmed custom status",
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = richerReadback,
            },
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Custom,
                    SettingValueSource.TemporaryOverride),
                CustomPerformance = requested.CustomPerformance,
            },
            PerformanceOverride = requested,
        };
        client.Publish(confirmed);
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Confirmed custom status");

        var later = new CustomPerformanceSettings(
            CustomPerformanceLevel.Medium,
            CustomPerformanceLevel.Low);
        client.Publish(confirmed with
        {
            Revision = baseline.Revision + 3,
            Message = "Later custom status",
            ConfirmedState = confirmed.ConfirmedState! with
            {
                CustomPerformance = later,
            },
            DesiredState = confirmed.DesiredState with
            {
                CustomPerformance = later,
            },
            PerformanceOverride = null,
        });
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "Later custom status"
            && viewModel.SelectedCustomCpuLevel == later.Cpu
            && viewModel.SelectedCustomGpuLevel == later.Gpu);

        Assert.Equal(later.Cpu, viewModel.SelectedCustomCpuLevel);
        Assert.Equal(later.Gpu, viewModel.SelectedCustomGpuLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerformanceSelection_ReleasesAfterConfirmationGracePeriod(
        bool includeMismatchedConfirmation)
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));
        await using var viewModel = new MainViewModel(client, dispatcher, time);
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);

        ServiceStatusSnapshot lagging = baseline with
        {
            Revision = baseline.Revision + 1,
            Message = "Lagging status",
            ConfirmedState = includeMismatchedConfirmation
                ? baseline.ConfirmedState! with
                {
                    Performance = PerformancePreset.Silent,
                }
                : null,
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Silent,
                    SettingValueSource.PowerProfile),
            },
        };
        client.Publish(lagging);
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Lagging status");
        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);

        time.Advance(TimeSpan.FromSeconds(3));
        int completedCalls = dispatcher.CompletedCalls;
        client.Publish(lagging with { Revision = baseline.Revision + 2 });
        await WaitUntilAsync(() => dispatcher.CompletedCalls > completedCalls);

        Assert.Equal(PerformancePreset.Silent, viewModel.SelectedPerformance);
    }

    [Fact]
    public async Task PerformanceSelection_ReleasesWhenPowerSourceChanges()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);

        client.Publish(baseline with
        {
            Revision = baseline.Revision + 1,
            ActivePowerSource = PowerSource.Battery,
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.BatteryBalanced,
                    SettingValueSource.PowerProfile),
            },
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.Battery);

        Assert.Equal(PerformancePreset.BatteryBalanced, viewModel.SelectedPerformance);
    }

    [Fact]
    public async Task FailedPerformanceSelection_RestoresPreviousPresentation()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(
                ControlOperationStatus.Failed,
                "Performance was not applied."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);

        Assert.Equal(PerformancePreset.Balanced, viewModel.SelectedPerformance);
    }

    [Fact]
    public async Task StoredPerformanceIntent_RemainsPresentedDuringManualFans()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(
                ControlOperationStatus.Stored,
                "Performance intent will apply with Automatic fans."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        client.Publish(status with
        {
            ConfirmedState = status.ConfirmedState! with
            {
                Performance = null,
                Fans = FanMode.Maximum,
            },
            DesiredState = status.DesiredState with
            {
                Fans = new ResolvedSetting<FanMode>(
                    FanMode.Fixed,
                    SettingValueSource.TemporaryOverride),
            },
        });
        await WaitUntilAsync(() => viewModel.SelectedFanMode == FanMode.Fixed);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Silent);

        Assert.Equal(PerformancePreset.Silent, viewModel.SelectedPerformance);
        Assert.Equal(PerformancePreset.Silent, client.PerformanceOverride!.Value);
        Assert.True(viewModel.HasThermalProfileDraft);
    }

    [Fact]
    public async Task ManualFanSelector_RemainsAvailableAcrossAcPerformanceModes()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline with
        {
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Custom,
                Fans = FanMode.Automatic,
            },
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Custom,
                    SettingValueSource.TemporaryOverride),
            },
        });
        await WaitUntilAsync(() => viewModel.SelectedPerformance == PerformancePreset.Custom);

        Assert.True(viewModel.CanEditFanControl);
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Fixed));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.ManualCurve));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Maximum));

        client.Publish(baseline with
        {
            Revision = baseline.Revision + 1,
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Automatic,
            },
        });
        await WaitUntilAsync(() => viewModel.SelectedPerformance == PerformancePreset.Balanced);

        Assert.True(viewModel.CanEditFanControl);
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Fixed));
    }

    [Fact]
    public async Task ExactModelCapabilities_ExposeDiscoveriesAndEnforceHighCpuGpuLowOnly()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        var exactCombinations = new CustomPerformanceCapabilities(
            new HashSet<CustomPerformanceCombination>
            {
                new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
                new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High),
                new(CustomPerformanceLevel.High, CustomPerformanceLevel.Low),
            });
        var observedGpuPolicy = new GpuOverclockCapabilities(
            new DeviceNumericRange(-1000, 400, null),
            new DeviceNumericRange(-1000, 3000, null),
            DeviceFeatureAccess.ObservedOnly)
        {
            SupportsEnableDisable = true,
        };
        client.Publish(baseline with
        {
            Capabilities = baseline.Capabilities! with
            {
                CustomPerformance = exactCombinations,
                CpuPowerLimit = new(
                    new DeviceNumericRange(45, 95, 1),
                    DeviceFeatureAccess.ObservedOnly),
                CpuVoltageOptimizer = new(
                    new DeviceNumericRange(-30, -1, 1),
                    DeviceFeatureAccess.ObservedOnly)
                {
                    SupportsEnableDisable = true,
                },
                GpuOverclock = observedGpuPolicy,
                SpecialKeyBindingsAccess = DeviceFeatureAccess.ObservedOnly,
                LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
                ObservedLightingEffects = new HashSet<LightingEffect>
                {
                    LightingEffect.Spectrum,
                    LightingEffect.Static,
                    LightingEffect.Starlight,
                    LightingEffect.Ripple,
                    LightingEffect.Tidal,
                    LightingEffect.Wave,
                    LightingEffect.Wheel,
                },
                ObservedPowerSourceBehaviors =
                [
                    new PowerSourceBehaviorObservation(
                        PowerSource.Battery,
                        PerformancePreset.BatterySaver,
                        FanMode.Automatic,
                        FixedFanControlAvailable: false,
                        PerformanceModeAvailable: false,
                        CustomModeAvailable: false,
                        InadequatePowerWarningVisible: false),
                    new PowerSourceBehaviorObservation(
                        PowerSource.UsbC,
                        PerformancePreset.Balanced,
                        FanMode.Automatic,
                        FixedFanControlAvailable: false,
                        PerformanceModeAvailable: false,
                        CustomModeAvailable: false,
                        InadequatePowerWarningVisible: true),
                ],
            },
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = new CustomPerformanceSettings(
                    CustomPerformanceLevel.High,
                    CustomPerformanceLevel.Low),
                GpuClockOffsets = new GpuClockOffsetCapabilities(
                    "NVIDIA GPU",
                    new GpuClockOffsetRange(-1000, 400, 0),
                    new GpuClockOffsetRange(-1000, 3000, 0)),
            },
        });
        await WaitUntilAsync(() =>
            viewModel.SelectedCustomCpuLevel == CustomPerformanceLevel.High);

        Assert.Equal([CustomPerformanceLevel.Low], viewModel.AvailableCustomGpuLevels);
        Assert.False(viewModel.SetCustomPerformanceCommand.CanExecute(
            new CustomPerformanceSettings(
                CustomPerformanceLevel.High,
                CustomPerformanceLevel.Medium)));
        Assert.Equal(
            DeviceFeatureAccess.ObservedOnly,
            viewModel.Capabilities.CpuPowerLimit?.Access);
        Assert.Equal(45, viewModel.Capabilities.CpuPowerLimit?.Watts.Minimum);
        Assert.Equal(95, viewModel.Capabilities.CpuPowerLimit?.Watts.Maximum);
        Assert.Equal(
            DeviceFeatureAccess.ObservedOnly,
            viewModel.Capabilities.CpuVoltageOptimizer?.Access);
        Assert.Equal(-30, viewModel.Capabilities.CpuVoltageOptimizer?.Value.Minimum);
        Assert.Equal(-1, viewModel.Capabilities.CpuVoltageOptimizer?.Value.Maximum);
        Assert.Equal(
            DeviceFeatureAccess.ObservedOnly,
            viewModel.Capabilities.GpuOverclock?.Access);
        Assert.Equal(-1000, viewModel.Capabilities.GpuOverclock?.CoreOffsetMegahertz.Minimum);
        Assert.Equal(400, viewModel.Capabilities.GpuOverclock?.CoreOffsetMegahertz.Maximum);
        Assert.False(viewModel.CanEditGpuClockTuning);
        Assert.False(viewModel.IsSpecialKeyBindingsEditable);
        client.Publish(baseline with
        {
            Revision = baseline.Revision + 1,
            ActivePowerSource = PowerSource.UsbC,
            Capabilities = baseline.Capabilities! with
            {
                ObservedPowerSourceBehaviors =
                [
                    new PowerSourceBehaviorObservation(
                        PowerSource.UsbC,
                        PerformancePreset.Balanced,
                        FanMode.Automatic,
                        FixedFanControlAvailable: false,
                        PerformanceModeAvailable: false,
                        CustomModeAvailable: false,
                        InadequatePowerWarningVisible: true),
                ],
            },
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.UsbC);

        PowerSourceBehaviorObservation observation = Assert.Single(
            viewModel.Capabilities.ObservedPowerSourceBehaviors);
        Assert.Equal(PowerSource.UsbC, observation.Source);
        Assert.Equal(PerformancePreset.Balanced, observation.SelectedPerformance);
        Assert.True(observation.InadequatePowerWarningVisible);
    }

    [Fact]
    public async Task PartialSpecialKeyAdmission_EnablesOnlyM3ThroughM5()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline with
        {
            Capabilities = baseline.Capabilities! with
            {
                SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite,
                WritableSpecialKeySlots =
                    SpecialKeySlotSet.M3
                    | SpecialKeySlotSet.M4
                    | SpecialKeySlotSet.M5,
            },
        });
        await WaitUntilAsync(() => viewModel.IsSpecialKeyBindingsEditable);

        Assert.False(viewModel.IsSpecialKeyBindingSlotEditable(SpecialKeySlot.M1));
        Assert.False(viewModel.IsSpecialKeyBindingSlotEditable(SpecialKeySlot.M2));
        Assert.True(viewModel.IsSpecialKeyBindingSlotEditable(SpecialKeySlot.M3));
        Assert.True(viewModel.IsSpecialKeyBindingSlotEditable(SpecialKeySlot.M4));
        Assert.True(viewModel.IsSpecialKeyBindingSlotEditable(SpecialKeySlot.M5));

        client.Publish(baseline with
        {
            Revision = baseline.Revision + 1,
            Capabilities = baseline.Capabilities! with
            {
                SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite,
                WritableSpecialKeySlots = SpecialKeySlotSet.M4,
            },
        });
        await WaitUntilAsync(() =>
            !viewModel.IsSpecialKeyBindingSlotEditable(SpecialKeySlot.M3)
            && viewModel.IsSpecialKeyBindingSlotEditable(SpecialKeySlot.M4));

        Assert.False(viewModel.IsSpecialKeyBindingSlotEditable(SpecialKeySlot.M3));
        Assert.True(viewModel.IsSpecialKeyBindingSlotEditable(SpecialKeySlot.M4));
    }

    [Fact]
    public async Task GpuClockTuning_UsesDriverRangesInBalancedMode()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        var current = new GpuClockOffsetSettings(0, 0);
        var gpuCapabilities = new GpuClockOffsetCapabilities(
            "NVIDIA GeForce RTX 5090 Laptop GPU",
            new GpuClockOffsetRange(-1000, 1000, current.CoreMegahertz),
            new GpuClockOffsetRange(-1000, 3000, current.MemoryMegahertz));
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline with
        {
            Capabilities = baseline.Capabilities! with
            {
                GpuOverclock = new GpuOverclockCapabilities(
                    new DeviceNumericRange(-1000, 400, null),
                    new DeviceNumericRange(-1000, 3000, null),
                    DeviceFeatureAccess.ReadWrite),
            },
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                CustomPerformance = null,
                GpuClockOffsets = gpuCapabilities,
            },
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    SettingValueSource.PowerProfile),
                CustomPerformance = null,
                GpuClockTuning = new GpuClockTuningSettings(
                    Enabled: true,
                    current),
            },
        });
        await WaitUntilAsync(() => viewModel.IsGpuClockTuningAvailable);

        Assert.Equal(-1000, viewModel.GpuCoreClockOffsetMinimum);
        Assert.Equal(1000, viewModel.GpuCoreClockOffsetMaximum);
        Assert.Equal(-1000, viewModel.GpuMemoryClockOffsetMinimum);
        Assert.Equal(3000, viewModel.GpuMemoryClockOffsetMaximum);
        Assert.True(viewModel.IsGpuClockTuningVisible);
        Assert.True(viewModel.IsGpuClockTuningEnabled);
        Assert.True(viewModel.CanEditGpuClockTuning);
        var requested = new GpuClockOffsetSettings(125, 500);

        await viewModel.SetGpuClockOffsetsAsync(requested, CancellationToken.None);

        Assert.Equal(PerformancePreset.Balanced, client.PerformanceOverride?.Value);
        Assert.Null(client.PerformanceOverride?.CustomPerformance);
        Assert.Equal(
            new GpuClockTuningSettings(Enabled: true, requested),
            client.PerformanceOverride?.GpuClockTuning);
    }

    [Fact]
    public async Task GpuClockTuning_CanBeEnabledFromUnmanagedBalancedProfile()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline with
        {
            Capabilities = baseline.Capabilities! with
            {
                GpuOverclock = new GpuOverclockCapabilities(
                    new DeviceNumericRange(-1000, 400, null),
                    new DeviceNumericRange(-1000, 3000, null),
                    DeviceFeatureAccess.ReadWrite),
            },
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                CustomPerformance = null,
                GpuClockOffsets = null,
            },
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    SettingValueSource.PowerProfile),
                CustomPerformance = null,
                GpuClockTuning = null,
            },
        });
        await WaitUntilAsync(() => viewModel.CanToggleGpuClockTuning);

        Assert.True(viewModel.IsGpuClockTuningVisible);
        Assert.False(viewModel.IsGpuClockTuningEnabled);
        Assert.False(viewModel.CanEditGpuClockTuning);

        await viewModel.SetGpuClockTuningEnabledAsync(
            enabled: true,
            CancellationToken.None);

        Assert.Equal(PerformancePreset.Balanced, client.PerformanceOverride?.Value);
        Assert.Null(client.PerformanceOverride?.CustomPerformance);
        Assert.Equal(
            new GpuClockTuningSettings(
                Enabled: true,
                GpuClockOffsetSettings.Default),
            client.PerformanceOverride?.GpuClockTuning);
    }

    [Fact]
    public async Task GpuClockTuning_IsVisibleForWritablePolicyWithoutCustomPreset()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline with
        {
            Capabilities = baseline.Capabilities! with
            {
                PerformancePresets = new HashSet<PerformancePreset>
                {
                    PerformancePreset.Balanced,
                },
                GpuOverclock = new GpuOverclockCapabilities(
                    new DeviceNumericRange(-100, 400, 5),
                    new DeviceNumericRange(-200, 1000, 5),
                    DeviceFeatureAccess.ReadWrite),
            },
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                CustomPerformance = null,
            },
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    SettingValueSource.PowerProfile),
                CustomPerformance = null,
            },
        });
        await WaitUntilAsync(() => viewModel.IsGpuClockTuningVisible);

        Assert.DoesNotContain(
            PerformancePreset.Custom,
            viewModel.AvailablePerformancePresets);
        Assert.True(viewModel.CanToggleGpuClockTuning);
    }

    [Fact]
    public async Task PerformanceOption_SameValueDoesNotNotifyTwoWayBindingAgain()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.AvailableActiveFanModes.Count > 0);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.PluggedInPerformanceOption = viewModel.PluggedInPerformanceOption;
        viewModel.BatteryPerformanceOption = viewModel.BatteryPerformanceOption;
        viewModel.UsbCPerformanceOption = viewModel.UsbCPerformanceOption;

        Assert.DoesNotContain(nameof(viewModel.PluggedInPerformanceOption), changed);
        Assert.DoesNotContain(nameof(viewModel.BatteryPerformanceOption), changed);
        Assert.DoesNotContain(nameof(viewModel.UsbCPerformanceOption), changed);
    }

    [Fact]
    public async Task UnchangedChargeLimitCapabilities_PreserveItemsSourceAcrossStatusEvents()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.IsConnected);
        IReadOnlyList<int> originalOptions = viewModel.ChargeLimitOptions;

        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Charge limit is already confirmed.",
        });
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "Charge limit is already confirmed.");

        Assert.Same(originalOptions, viewModel.ChargeLimitOptions);
    }

    [Fact]
    public async Task UnchangedFanCapabilities_PreserveItemsSourceAcrossStatusEvents()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.AvailableActiveFanModeOptions.Count > 0);
        IReadOnlyList<FanMode> originalOptions = viewModel.AvailableFanModes;
        IReadOnlyList<FanModeOption> originalPresentedOptions =
            viewModel.AvailableActiveFanModeOptions;
        FanModeOption originalSelection = Assert.IsType<FanModeOption>(
            viewModel.SelectedFanModeOption);

        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Fan mode is already confirmed.",
        });
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "Fan mode is already confirmed.");

        Assert.Same(originalOptions, viewModel.AvailableFanModes);
        Assert.Same(originalPresentedOptions, viewModel.AvailableActiveFanModeOptions);
        Assert.Same(originalSelection, viewModel.SelectedFanModeOption);
        Assert.Same(
            viewModel.SelectedFanModeOption,
            viewModel.AvailableActiveFanModeOptions.Single(option =>
                option.Value == viewModel.SelectedFanMode));
    }

    [Fact]
    public async Task UnchangedPerformanceCapabilities_PreserveItemsSourceAcrossStatusEvents()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.IsConnected);
        IReadOnlyList<PerformancePreset> originalOptions =
            viewModel.AvailablePerformancePresets;
        IReadOnlyList<PerformancePresetOption> originalPresentedOptions =
            viewModel.AvailableActivePerformanceOptions;
        PerformancePresetOption originalSelection = Assert.IsType<PerformancePresetOption>(
            viewModel.SelectedPerformanceOption);

        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Performance mode is already confirmed.",
        });
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "Performance mode is already confirmed.");

        Assert.Same(originalOptions, viewModel.AvailablePerformancePresets);
        Assert.Same(
            originalPresentedOptions,
            viewModel.AvailableActivePerformanceOptions);
        Assert.Same(originalSelection, viewModel.SelectedPerformanceOption);
        Assert.Same(
            viewModel.SelectedPerformanceOption,
            viewModel.AvailableActivePerformanceOptions.Single(option =>
                option.Value == viewModel.SelectedPerformance));
    }

    [Fact]
    public async Task ConditionalPickerOptionGetters_ReturnStableCollections()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());

        IReadOnlyList<CustomPerformanceLevel> cpuOptions =
            viewModel.AvailableCustomCpuLevels;
        IReadOnlyList<CustomPerformanceLevel> gpuOptions =
            viewModel.AvailableCustomGpuLevels;
        IReadOnlyList<LightingDirectionOption> standardDirections =
            viewModel.LightingDirectionOptions;

        Assert.Same(cpuOptions, viewModel.AvailableCustomCpuLevels);
        Assert.Same(gpuOptions, viewModel.AvailableCustomGpuLevels);
        Assert.Same(standardDirections, viewModel.LightingDirectionOptions);

        viewModel.LightingEffectEditor = LightingEffect.Tidal;
        IReadOnlyList<LightingDirectionOption> tidalDirections =
            viewModel.LightingDirectionOptions;

        Assert.NotSame(standardDirections, tidalDirections);
        Assert.Same(tidalDirections, viewModel.LightingDirectionOptions);

        viewModel.LightingEffectEditor = LightingEffect.Wave;
        Assert.Same(standardDirections, viewModel.LightingDirectionOptions);
    }

    [Fact]
    public async Task InitialFanCapabilityPopulation_ReappliesConfirmedSelectionAfterItemsSource()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changed.Add(args.PropertyName);
            }
        };
        await viewModel.StartAsync();

        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.AvailableActiveFanModes.Count > 0);

        int directItemsSourceChange = changed.IndexOf(nameof(MainViewModel.AvailableFanModes));
        int activeItemsSourceChange = changed.IndexOf(nameof(MainViewModel.AvailableActiveFanModes));
        int selectionReapply = changed.LastIndexOf(nameof(MainViewModel.SelectedFanMode));
        int pluggedInReapply = changed.LastIndexOf(nameof(MainViewModel.PluggedInFanModeEditor));
        int batteryReapply = changed.LastIndexOf(nameof(MainViewModel.BatteryFanModeEditor));
        int usbCReapply = changed.LastIndexOf(nameof(MainViewModel.UsbCFanModeEditor));
        Assert.True(directItemsSourceChange >= 0);
        Assert.True(activeItemsSourceChange > directItemsSourceChange);
        Assert.True(selectionReapply > activeItemsSourceChange);
        Assert.True(pluggedInReapply > directItemsSourceChange);
        Assert.True(batteryReapply > directItemsSourceChange);
        Assert.True(usbCReapply > directItemsSourceChange);
        Assert.Equal(FanMode.Automatic, viewModel.SelectedFanMode);
    }

    [Fact]
    public async Task PowerSourceChangeReappliesFanSelectionAfterActiveItemsSource()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changed.Add(args.PropertyName);
            }
        };
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.AvailableActiveFanModes.Count > 0);
        changed.Clear();

        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            ActivePowerSource = PowerSource.UsbC,
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.UsbC);
        await WaitUntilAsync(() => changed.Contains(nameof(MainViewModel.SelectedFanMode)));

        int itemsSourceChange = changed.IndexOf(nameof(MainViewModel.AvailableActiveFanModes));
        int selectionReapply = changed.IndexOf(nameof(MainViewModel.SelectedFanMode));
        Assert.True(itemsSourceChange >= 0);
        Assert.True(selectionReapply > itemsSourceChange);
        Assert.Equal(FanMode.Automatic, viewModel.SelectedFanMode);
    }

    [Fact]
    public async Task InitialPerformanceCapabilityPopulation_ReappliesProfileSelections()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changed.Add(args.PropertyName);
            }
        };
        await viewModel.StartAsync();

        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.IsConnected);

        int itemsSourceChange = changed.IndexOf(nameof(MainViewModel.AvailablePerformancePresets));
        int pluggedInReapply = changed.LastIndexOf(
            nameof(MainViewModel.PluggedInPerformanceEditor));
        int batteryReapply = changed.LastIndexOf(
            nameof(MainViewModel.BatteryPerformanceEditor));
        int usbCReapply = changed.LastIndexOf(
            nameof(MainViewModel.UsbCPerformanceEditor));
        Assert.True(itemsSourceChange >= 0);
        Assert.True(pluggedInReapply > itemsSourceChange);
        Assert.True(batteryReapply > itemsSourceChange);
        Assert.True(usbCReapply > itemsSourceChange);
        Assert.Equal(PerformancePreset.Balanced, viewModel.PluggedInPerformanceEditor);
        Assert.Equal(PerformancePreset.BatterySaver, viewModel.BatteryPerformanceEditor);
        Assert.Equal(PerformancePreset.Balanced, viewModel.UsbCPerformanceEditor);
    }

    [Fact]
    public async Task EditedChargeLimitSurvivesPeriodicConfirmedStatusUntilApply()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.ChargeLimitPercent = 75;
        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Periodic observation",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Periodic observation");

        Assert.Equal(75, viewModel.ChargeLimitPercent);
    }

    [Fact]
    public async Task AppliedChargeLimitReleasesEditGuardToConfirmedReadback()
    {
        var client = new FakeServiceClient { BlockMutations = true };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.ChargeLimitPercent = 75;
        Task apply = Assert.IsType<AsyncRelayCommand>(viewModel.SetChargeLimitCommand)
            .ExecuteAsync(viewModel.ChargeLimitPercent);
        await client.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Applied with readback",
            ConfirmedState = initial.ConfirmedState! with
            {
                Battery = new ConfirmedBatteryState(true, 70, false),
            },
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Applied with readback");
        Assert.Equal(75, viewModel.ChargeLimitPercent);

        client.CompleteMutation();
        await apply;

        Assert.Equal(70, viewModel.ChargeLimitPercent);

        client.Publish(initial with
        {
            Revision = initial.Revision + 2,
            Message = "Later confirmed change",
            ConfirmedState = initial.ConfirmedState! with
            {
                Battery = new ConfirmedBatteryState(true, 65, false),
            },
        });
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "Later confirmed change"
            && viewModel.ChargeLimitPercent == 65);

        Assert.Equal(65, viewModel.ChargeLimitPercent);
    }

    [Fact]
    public async Task AppliedBatteryProtectionToggle_MarksAndSavesOnlyActiveProfile()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.CanChangeBatteryProtection);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ToggleBatteryProtectionCommand)
            .ExecuteAsync(null);

        Assert.False(client.BatteryProtectionEnabled);
        Assert.True(viewModel.HasBatteryProtectionProfileDraft);
        Assert.True(viewModel.HasDashboardProfileDraft);
        Assert.True(viewModel.HasUnsavedProfileChanges);
        Assert.True(viewModel.PluggedInProfilePresentation.BatteryProtection.IsPending);
        Assert.False(viewModel.UsbCProfilePresentation.BatteryProtection.IsPending);
        Assert.False(viewModel.BatteryProfilePresentation.BatteryProtection.IsPending);
        Assert.Equal(
            "AC Battery protection: On. Pending: Off",
            viewModel.PluggedInProfilePresentation.BatteryProtection.AccessibilityDescription);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand)
            .ExecuteAsync(null);

        ProfileSet saved = Assert.IsType<ProfileSet>(client.LastProfiles);
        Assert.False(saved.PluggedIn.BatteryProtectionEnabled);
        Assert.True(saved.UsbC.BatteryProtectionEnabled);
        Assert.True(saved.Battery.BatteryProtectionEnabled);
        Assert.False(viewModel.HasBatteryProtectionProfileDraft);
        Assert.False(viewModel.HasUnsavedProfileChanges);
    }

    [Fact]
    public async Task AppliedChargeLimit_CreatesGlobalDraftThatSurvivesSourceChange()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.CanAdjustChargeLimit);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetChargeLimitCommand)
            .ExecuteAsync(75);

        Assert.Equal(BatteryPolicy.Create(75), client.BatteryPolicy);
        Assert.True(viewModel.HasGlobalBatteryPolicyDraft);
        Assert.True(viewModel.BatteryPolicyPresentation.IsPending);
        Assert.Equal("75%", viewModel.BatteryPolicyPresentation.PendingText);

        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            ActivePowerSource = PowerSource.Battery,
            DesiredState = initial.DesiredState with
            {
                Battery = new DesiredBatteryState(
                    BatteryPolicy.Create(75),
                    initial.DesiredState.Battery.ProtectionEnabled,
                    initial.DesiredState.Battery.TemporaryFullCharge),
            },
            BatteryPolicyOverride = BatteryPolicy.Create(75),
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.Battery);

        Assert.True(viewModel.HasGlobalBatteryPolicyDraft);
        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand)
            .ExecuteAsync(null);
        ProfileSet saved = Assert.IsType<ProfileSet>(client.LastProfiles);
        Assert.Equal(BatteryPolicy.Create(75), saved.BatteryPolicy);
        Assert.Equal(initial.Profiles.PluggedIn, saved.PluggedIn);
        Assert.Equal(initial.Profiles.Battery, saved.Battery);
        Assert.Equal(initial.Profiles.UsbC, saved.UsbC);
        Assert.False(viewModel.HasGlobalBatteryPolicyDraft);
    }

    [Fact]
    public async Task AppliedChargeLimit_CanBeDiscardedBeforeStatusRefresh()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.CanAdjustChargeLimit);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetChargeLimitCommand)
            .ExecuteAsync(75);

        Assert.True(viewModel.HasGlobalBatteryPolicyDraft);
        Assert.True(viewModel.HasDashboardProfileRestoreOpportunity);
        Assert.True(viewModel.DiscardProfileDraftCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Equal(initial.Profiles.BatteryPolicy, client.BatteryPolicy);
        Assert.False(viewModel.HasGlobalBatteryPolicyDraft);
        Assert.False(viewModel.HasUnsavedProfileChanges);
        Assert.False(viewModel.BatteryPolicyPresentation.IsPending);
    }

    [Fact]
    public async Task StoredBatteryResults_DoNotCreateSaveableDrafts()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(
                ControlOperationStatus.Stored,
                "Intent was stored without confirmed application."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.CanAdjustChargeLimit);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetChargeLimitCommand)
            .ExecuteAsync(75);
        await Assert.IsType<AsyncRelayCommand>(viewModel.ToggleBatteryProtectionCommand)
            .ExecuteAsync(null);

        Assert.False(viewModel.HasGlobalBatteryPolicyDraft);
        Assert.False(viewModel.HasBatteryProtectionProfileDraft);
        Assert.False(viewModel.HasUnsavedProfileChanges);
    }

    [Fact]
    public async Task AppliedLightingReleasesEditGuardToConfirmedReadback()
    {
        var client = new FakeServiceClient { BlockMutations = true };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.LightingEffectEditor = LightingEffect.Static;
        viewModel.LightingBrightnessEditor = 65;
        viewModel.LightingRedEditor = 12;
        LightingSettings draft = viewModel.LightingDraft;
        Task apply = Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(draft);
        await client.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        LightingSettings confirmed = LightingSettingsFactory.SpectrumCycling(40);
        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Lighting applied with readback",
            ConfirmedState = initial.ConfirmedState! with { Lighting = confirmed },
        });
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "Lighting applied with readback");
        Assert.Equal(draft, viewModel.LightingDraft);

        client.CompleteMutation();
        await apply;

        Assert.Equal(confirmed, viewModel.LightingDraft);
    }

    [Fact]
    public async Task UnknownEffectStatusDuringLightingWriteDoesNotReplaceRequestedEffect()
    {
        var client = new FakeServiceClient { BlockMutations = true };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            }) with
        {
            ConfirmedState = CreateStatus(
                verified: true,
                blocked: false).ConfirmedState! with
            { Lighting = null },
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        };
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.IsKeyboardEffectUnconfirmed);

        viewModel.LightingEffectEditor = LightingEffect.Static;
        viewModel.LightingBrightnessEditor = 63;
        viewModel.LightingPrimaryColorEditor = new RgbColor(0x47, 0xE1, 0x0C);
        LightingSettings requested = viewModel.LightingDraft;
        Task apply = Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(requested);
        await client.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Periodic lighting observation",
            LightingOverride = null,
        });
        await WaitUntilAsync(() =>
            viewModel.ConnectionStatus == "Periodic lighting observation");

        Assert.True(viewModel.IsLightingOperationInProgress);
        Assert.False(viewModel.IsKeyboardEffectUnconfirmed);
        Assert.Equal(LightingEffect.Static, viewModel.SelectedLightingEffectOption?.Effect);
        Assert.Equal(requested, viewModel.LightingDraft);

        client.CompleteMutation();
        await apply;

        Assert.False(viewModel.IsKeyboardEffectUnconfirmed);
        Assert.Equal(LightingEffect.Static, viewModel.SelectedLightingEffectOption?.Effect);
        Assert.Equal(requested, viewModel.LightingDraft);
    }

    [Fact]
    public async Task ExplicitLightingOverrideKeepsNativeEffectConfirmedInUi()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        LightingSettings requested = LightingSettingsFactory.Static(
            new RgbColor(0x47, 0xE1, 0x0C),
            63);
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(status with
        {
            ConfirmedState = status.ConfirmedState! with { Lighting = null },
            DesiredState = status.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    requested,
                    SettingValueSource.TemporaryOverride),
            },
            LightingOverride = new ManualOverride<LightingSettings>(
                requested,
                OverrideLifetime.UntilPowerSourceChange),
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        });
        await WaitUntilAsync(() =>
            viewModel.SelectedLightingEffectOption?.Effect == LightingEffect.Static);

        Assert.False(viewModel.IsKeyboardEffectUnconfirmed);
        Assert.Equal(LightingEffect.Static, viewModel.SelectedLightingEffectOption?.Effect);
        Assert.Equal(requested, viewModel.LightingDraft);
    }

    [Fact]
    public async Task ExternalKeyboardEffectIsNotMisreportedAndLogoUsesFirmwareReadback()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                LogoLightingModes = new HashSet<LogoLightingMode>(
                    Enum.GetValues<LogoLightingMode>()),
                ChargeLimits = ChargeLimits(),
            });
        LightingSettings savedOff = LightingSettingsFactory.Off();
        client.Publish(initial with
        {
            ConfirmedState = initial.ConfirmedState! with
            {
                Lighting = null,
                LogoLighting = LogoLightingMode.Breathing,
            },
            DesiredState = initial.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    savedOff,
                    SettingValueSource.PowerProfile),
            },
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        });
        await WaitUntilAsync(() =>
            viewModel.IsKeyboardEffectUnconfirmed
            && viewModel.LogoLightingModeEditor == LogoLightingMode.Breathing);

        Assert.True(viewModel.IsKeyboardEffectUnconfirmed);
        Assert.Null(viewModel.SelectedLightingEffectOption);
        Assert.Contains("outside OpenBlade", viewModel.KeyboardEffectDescription);
        Assert.Equal(LogoLightingMode.Breathing, viewModel.LogoLightingModeEditor);
        Assert.DoesNotContain(
            viewModel.AvailableLogoLightingModeOptions,
            option => option.Mode is null);

        LightingSettings spectrum = LightingSettingsFactory.SpectrumCycling(40);
        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            ConfirmedState = initial.ConfirmedState! with
            {
                Lighting = null,
                LogoLighting = LogoLightingMode.Static,
            },
            DesiredState = initial.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    spectrum,
                    SettingValueSource.TemporaryOverride),
            },
            LightingRendererOwnership = LightingRendererOwnershipState.Spectrum,
        });
        await WaitUntilAsync(() =>
            viewModel.SelectedLightingEffectOption?.Effect == LightingEffect.Spectrum
            && viewModel.LogoLightingModeEditor == LogoLightingMode.Static);

        Assert.False(viewModel.IsKeyboardEffectUnconfirmed);
        Assert.Equal(LogoLightingMode.Static, viewModel.LogoLightingModeEditor);
    }

    [Fact]
    public async Task LightingColorPickerAndAdvancedRgbFieldsStaySynchronized()
    {
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new RecordingDispatcher());
        viewModel.LightingEffectEditor = LightingEffect.Static;

        viewModel.LightingPrimaryColorEditor = new RgbColor(12, 34, 56);

        Assert.Equal(12, viewModel.LightingRedEditor);
        Assert.Equal(34, viewModel.LightingGreenEditor);
        Assert.Equal(56, viewModel.LightingBlueEditor);
        Assert.Equal(new RgbColor(12, 34, 56), viewModel.LightingDraft.Color);

        viewModel.LightingGreenEditor = 200;

        Assert.Equal(new RgbColor(12, 200, 56), viewModel.LightingPrimaryColorEditor);

        viewModel.LightingEffectEditor = LightingEffect.Breathing;
        viewModel.LightingBreathingColorModeEditor = BreathingColorMode.Dual;
        viewModel.LightingSecondaryColorEditor = new RgbColor(78, 90, 123);

        Assert.Equal(78, viewModel.LightingSecondaryRedEditor);
        Assert.Equal(90, viewModel.LightingSecondaryGreenEditor);
        Assert.Equal(123, viewModel.LightingSecondaryBlueEditor);
        Assert.Equal(
            new RgbColor(78, 90, 123),
            Assert.IsType<BreathingLightingParameters>(viewModel.LightingDraft.Parameters)
                .SecondaryColor);
    }

    [Fact]
    public async Task AmbientAwarenessDraftStoresTheSelectedScreenRegion()
    {
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new RecordingDispatcher());

        viewModel.LightingEffectEditor = LightingEffect.AmbientAwareness;
        viewModel.LightingAmbientAwarenessRegionEditor = AmbientAwarenessRegion.Bottom;

        Assert.True(viewModel.LightingShowsAmbientAwarenessRegion);
        Assert.Equal(
            AmbientAwarenessRegion.Bottom,
            Assert.IsType<AmbientAwarenessLightingParameters>(
                viewModel.LightingDraft.Parameters).Region);
        Assert.Equal("Bottom", viewModel.SelectedAmbientAwarenessRegionOption?.Label);
        Assert.True(LightingSettingsValidation.IsWellFormed(viewModel.LightingDraft));
    }

    [Fact]
    public async Task SelectingBreathingFromOffCreatesAVisibleDraft()
    {
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new RecordingDispatcher());
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal(LightingEffect.Off, viewModel.LightingEffectEditor);
        Assert.Equal(0, viewModel.LightingBrightnessEditor);

        viewModel.LightingEffectEditor = LightingEffect.Breathing;

        Assert.Equal(50, viewModel.LightingBrightnessEditor);
        Assert.Contains(nameof(MainViewModel.LightingBrightnessEditor), changed);
        Assert.Equal(
            LightingSettingsFactory.DefaultVisibleColor,
            viewModel.LightingPrimaryColorEditor);
        Assert.True(LightingSettingsValidation.IsWellFormed(viewModel.LightingDraft));
    }

    [Fact]
    public async Task SelectingTidalFromOffSeedsRazerGreenAsPrimaryColor()
    {
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new RecordingDispatcher());

        viewModel.LightingEffectEditor = LightingEffect.Tidal;

        Assert.Equal(
            LightingSettingsFactory.DefaultVisibleColor,
            viewModel.LightingPrimaryColorEditor);
        Assert.Equal(new RgbColor(0, 0, 255), viewModel.LightingSecondaryColorEditor);
        Assert.True(LightingSettingsValidation.IsWellFormed(viewModel.LightingDraft));
    }

    [Fact]
    public async Task LightingEffects_PutOffFirstThenSortTheRemainingLabels()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            }));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.Equal(LightingEffect.Off, viewModel.AvailableLightingEffectOptions[0].Effect);
        string[] remaining = viewModel.AvailableLightingEffectOptions
            .Skip(1)
            .Select(option => option.Label)
            .ToArray();
        Assert.Equal(
            remaining.OrderBy(label => label, StringComparer.OrdinalIgnoreCase),
            remaining);
    }

    [Fact]
    public async Task ImportProfiles_StoresAndLoadsEveryImportedEditor()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.IsConnected);
        ProfileSet imported = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };

        bool accepted = await viewModel.ImportProfilesAsync(
            imported,
            CancellationToken.None);

        Assert.True(accepted);
        Assert.Equal(imported, client.LastProfiles);
        Assert.Equal(imported, viewModel.Profiles);
        Assert.False(viewModel.HasActiveProfileDraft);
    }

    [Fact]
    public async Task LaptopPageProfileAction_SavesPowerAndCoolingChanges()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
                OneShotLaptopControls = new HashSet<OneShotLaptopControl>(
                    Enum.GetValues<OneShotLaptopControl>()),
            });
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsConnected);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);

        Assert.True(viewModel.HasThermalProfileDraft);
        Assert.True(viewModel.HasLaptopPageProfileDraft);
        Assert.True(viewModel.SaveLaptopProfileDraftCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveLaptopProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Equal(PerformancePreset.Performance, viewModel.Profiles.PluggedIn.Performance);
        Assert.Equal(PerformancePreset.Performance, client.LastProfiles?.PluggedIn.Performance);
        Assert.Null(client.PerformanceOverride);
        Assert.False(viewModel.HasThermalProfileDraft);
        Assert.False(viewModel.HasLaptopPageProfileDraft);
    }

    [Fact]
    public async Task ExternalLightingPolicy_ReflectsStatusAndSendsMutation()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            AlwaysOverrideExternalLightingChanges = true,
        });
        await WaitUntilAsync(() => viewModel.AlwaysOverrideExternalLightingChanges);

        bool accepted = await viewModel.SetExternalLightingOverridePolicyAsync(
            false,
            CancellationToken.None);

        Assert.True(accepted);
        Assert.False(viewModel.AlwaysOverrideExternalLightingChanges);
        Assert.False(client.ExternalLightingOverridePolicy);
    }

    [Fact]
    public async Task FirmwareCompatibilityOverride_ReflectsStatusAndSendsMutation()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: false, blocked: true) with
        {
            AllowUntestedFirmware = true,
        });
        await WaitUntilAsync(() => viewModel.AllowUntestedFirmware);

        bool accepted = await viewModel.SetFirmwareCompatibilityOverrideAsync(
            false,
            CancellationToken.None);

        Assert.True(accepted);
        Assert.False(viewModel.AllowUntestedFirmware);
        Assert.False(client.FirmwareCompatibilityOverride);
    }

    [Fact]
    public async Task SelectingEffectFromUnconfirmedZeroBrightnessUsesMatchingVisibleDefault()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(initial with
        {
            ConfirmedState = initial.ConfirmedState! with { Lighting = null },
            DesiredState = initial.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    LightingSettingsFactory.SpectrumCycling(0),
                    SettingValueSource.PowerProfile),
            },
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        });
        await WaitUntilAsync(() => viewModel.IsKeyboardEffectUnconfirmed);
        Assert.Equal(50, viewModel.LightingBrightnessEditor);

        viewModel.SelectedLightingEffectOption = viewModel.AvailableLightingEffectOptions
            .Single(option => option.Effect == LightingEffect.Starlight);

        Assert.Equal(50, viewModel.LightingBrightnessEditor);
        Assert.Equal(50, viewModel.LightingDraft.Brightness);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(viewModel.LightingDraft);

        ManualOverride<LightingSettings> lightingOverride = Assert.IsType<ManualOverride<LightingSettings>>(
            client.LightingOverride);
        Assert.Equal((byte)50, lightingOverride.Value.Brightness);
    }

    [Fact]
    public async Task VisibleLightingEditor_NormalizesZeroBrightnessToVisibleDefault()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });

        client.Publish(initial with
        {
            ConfirmedState = initial.ConfirmedState! with
            {
                Lighting = LightingSettingsFactory.SpectrumCycling(0),
            },
            DesiredState = initial.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    LightingSettingsFactory.SpectrumCycling(0),
                    SettingValueSource.PowerProfile),
            },
            LightingRendererOwnership = LightingRendererOwnershipState.Spectrum,
        });
        await WaitUntilAsync(() => viewModel.SelectedLighting.Effect == LightingEffect.Spectrum);

        Assert.Equal(50, viewModel.LightingBrightnessEditor);
        Assert.Equal(50, viewModel.LightingDraft.Brightness);
    }

    [Fact]
    public async Task SelectingHiddenDesiredEffectFromUnconfirmedStateAppliesVisibleDefault()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(initial with
        {
            ConfirmedState = initial.ConfirmedState! with { Lighting = null },
            DesiredState = initial.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    LightingSettingsFactory.SpectrumCycling(0),
                    SettingValueSource.PowerProfile),
            },
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        });
        await WaitUntilAsync(() => viewModel.IsKeyboardEffectUnconfirmed);

        viewModel.SelectedLightingEffectOption = viewModel.AvailableLightingEffectOptions
            .Single(option => option.Effect == LightingEffect.Spectrum);

        Assert.False(viewModel.IsKeyboardEffectUnconfirmed);
        Assert.Equal(50, viewModel.LightingBrightnessEditor);
        Assert.Equal(50, viewModel.LightingDraft.Brightness);
        Assert.True(viewModel.IsLightingApplyPending);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(viewModel.LightingDraft);

        ManualOverride<LightingSettings> lightingOverride =
            Assert.IsType<ManualOverride<LightingSettings>>(client.LightingOverride);
        Assert.Equal(LightingEffect.Spectrum, lightingOverride.Value.Effect);
        Assert.Equal((byte)50, lightingOverride.Value.Brightness);
    }

    [Fact]
    public async Task StoredLightingApplyStopsAutoRetryWithoutReloadingUnconfirmedFirmware()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(
                ControlOperationStatus.Stored,
                "Lighting override saved without hardware."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.LightingEffectEditor = LightingEffect.Spectrum;
        viewModel.LightingBrightnessEditor = 64;
        LightingSettings requested = viewModel.LightingDraft;
        Assert.True(viewModel.IsLightingApplyPending);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(requested);

        Assert.False(viewModel.IsLightingApplyPending);
        Assert.Equal(requested, viewModel.LightingDraft);
        Assert.True(viewModel.HasActiveProfileDraft);
        ManualOverride<LightingSettings> lightingOverride =
            Assert.IsType<ManualOverride<LightingSettings>>(client.LightingOverride);
        Assert.Equal(requested, lightingOverride.Value);
    }

    [Theory]
    [InlineData(PowerSource.Battery, false)]
    [InlineData(PowerSource.Battery, true)]
    [InlineData(PowerSource.UsbC, false)]
    [InlineData(PowerSource.UsbC, true)]
    public async Task UnappliedLightingEditor_IsReloadedForConfirmedPowerSourceTransition(
        PowerSource destination,
        bool passesThroughUnknown)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        LightingSettings destinationLighting = destination == PowerSource.Battery
            ? LightingSettingsFactory.SpectrumCycling(35)
            : LightingSettingsFactory.BreathingSingle(new RgbColor(12, 34, 56), 45);
        ServiceStatusSnapshot destinationStatus = initial with
        {
            Revision = passesThroughUnknown ? initial.Revision + 2 : initial.Revision + 1,
            ActivePowerSource = destination,
            ConfirmedState = initial.ConfirmedState! with { Lighting = destinationLighting },
            DesiredState = initial.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    destinationLighting,
                    SettingValueSource.PowerProfile),
            },
        };
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.LightingEffectEditor = LightingEffect.Static;
        viewModel.LightingBrightnessEditor = 77;
        viewModel.LightingRedEditor = 231;
        LightingSettings unapplied = viewModel.LightingDraft;

        if (passesThroughUnknown)
        {
            client.Publish(initial with
            {
                Revision = initial.Revision + 1,
                ActivePowerSource = PowerSource.ExternalUnknown,
            });
            await WaitUntilAsync(() =>
                viewModel.ActivePowerSource == PowerSource.ExternalUnknown);
            Assert.Equal(unapplied, viewModel.LightingDraft);
        }

        client.Publish(destinationStatus);
        await WaitUntilAsync(() => viewModel.ActivePowerSource == destination);

        Assert.NotEqual(unapplied, viewModel.LightingDraft);
        Assert.Equal(destinationLighting, viewModel.LightingDraft);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(viewModel.LightingDraft);
        Assert.Equal(destinationLighting, client.LightingOverride?.Value);
    }

    [Fact]
    public async Task UnappliedLightingEditor_IsPreservedAcrossTransientUnknownRecheck()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.LightingEffectEditor = LightingEffect.Static;
        viewModel.LightingBrightnessEditor = 67;
        viewModel.LightingGreenEditor = 123;
        LightingSettings unapplied = viewModel.LightingDraft;

        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            ActivePowerSource = PowerSource.ExternalUnknown,
        });
        await WaitUntilAsync(() =>
            viewModel.ActivePowerSource == PowerSource.ExternalUnknown);
        Assert.Equal(unapplied, viewModel.LightingDraft);

        client.Publish(initial with { Revision = initial.Revision + 2 });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.PluggedIn);

        Assert.Equal(unapplied, viewModel.LightingDraft);
    }

    [Fact]
    public async Task Status_RendersLiveReadingsAndActiveQuickChanges()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false) with
        {
            DesiredState = new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Performance,
                    SettingValueSource.PersistentOverride),
                new ResolvedSetting<FanMode>(
                    FanMode.Maximum,
                    SettingValueSource.TemporaryOverride),
                new ResolvedSetting<LightingSettings>(
                    ProfileDefaults.CreateSafeDefaults().Battery.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(BatteryPolicy.Create(80), true, false)),
            ActivePowerSource = PowerSource.Battery,
            LiveReadings = new LiveReadings(-24_300, null, null),
            PerformanceOverride = new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            FanOverride = new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.UntilPowerSourceChange),
            LightingOverride = new ManualOverride<LightingSettings>(
                ProfileDefaults.CreateSafeDefaults().Battery.Lighting,
                OverrideLifetime.Persistent),
            ConfirmedState = new ConfirmedDeviceState(
                null,
                null,
                new ConfirmedBatteryState(true, 80, true),
                null),
            FullChargeOverride = true,
        };
        client.Publish(status);
        await WaitUntilAsync(() =>
            viewModel.SelectedPerformance == PerformancePreset.Performance
            && viewModel.ActivePowerSource == PowerSource.Battery);

        Assert.Equal("Using 24.3 W", viewModel.BatteryPowerFlowLabel);
        Assert.Equal("Fan 1: unavailable", viewModel.Fan1RpmLabel);
        Assert.Equal("Fan 2: unavailable", viewModel.Fan2RpmLabel);
        Assert.Equal("Battery profile", viewModel.ActivePowerSourceLabel);
        Assert.Equal("Controls unavailable", viewModel.PerformanceSourceLabel);
        Assert.Equal("Controls unavailable", viewModel.FanSourceLabel);
        Assert.Equal("Controls unavailable", viewModel.LightingSourceLabel);
        Assert.Equal(status.Capabilities, viewModel.Capabilities);
        Assert.Equal(status.LiveReadings, viewModel.LiveReadings);
        Assert.Equal(status.PerformanceOverride, viewModel.PerformanceOverride);
        Assert.Equal(status.FanOverride, viewModel.FanOverride);
        Assert.Equal(status.LightingOverride, viewModel.LightingOverride);
        Assert.Contains(
            "Power mode: Performance until reset",
            viewModel.ManualOverrideStatus,
            StringComparison.Ordinal);
        Assert.Contains(
            "Fans: Maximum until the power source changes",
            viewModel.ManualOverrideStatus,
            StringComparison.Ordinal);
        Assert.True(viewModel.FullChargeOverride);
    }

    [Fact]
    public async Task UnsupportedLightingOnlyOverrideDoesNotCreateBlankActiveChangesCard()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline with
        {
            Capabilities = baseline.Capabilities! with
            {
                LightingEffects = new HashSet<LightingEffect>(),
            },
            LightingOverride = new ManualOverride<LightingSettings>(
                ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting,
                OverrideLifetime.Persistent),
        });
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.True(viewModel.IsLightingVisible);
        Assert.False(viewModel.HasLightingCapability);
        Assert.False(viewModel.CanControlLighting);
        Assert.False(viewModel.HasActiveChanges);
        Assert.Equal(string.Empty, viewModel.ActiveChangesText);
    }

    [Fact]
    public async Task Status_FormatsNetBatteryFlowAndIndependentFans()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());

        Assert.Equal("Unavailable", viewModel.BatteryPowerFlowLabel);
        Assert.Equal("Unavailable", viewModel.Fan1RpmLabel);
        Assert.Equal("Unavailable", viewModel.Fan2RpmLabel);
        Assert.Equal("Unavailable", viewModel.Fan1RpmValue);
        Assert.Equal("Unavailable", viewModel.Fan2RpmValue);
        Assert.Equal(
            "Unavailable",
            viewModel.VisibleDashboardTelemetryGroups.Single(
                item => item.Label == "Fans").Value);
        Assert.Equal(
            "Live readings are unavailable while the OpenBlade service is not running.",
            viewModel.LiveReadingsRefreshToolTip);

        var changed = new ConcurrentPropertySet();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ActivePowerSource = PowerSource.Battery,
            LiveReadings = new LiveReadings(-24_340, 3210, 2875, 47, 52),
        });
        await WaitUntilAsync(() => viewModel.Fan1RpmLabel.Contains("3", StringComparison.Ordinal));

        Assert.Equal("Using 24.3 W", viewModel.BatteryPowerFlowLabel);
        Assert.Contains("3,210 RPM", viewModel.Fan1RpmLabel, StringComparison.Ordinal);
        Assert.Contains("2,875 RPM", viewModel.Fan2RpmLabel, StringComparison.Ordinal);
        Assert.Equal("3,210 RPM", viewModel.Fan1RpmValue);
        Assert.Equal("2,875 RPM", viewModel.Fan2RpmValue);
        Assert.Equal("47 °C", viewModel.CpuTemperatureValue);
        Assert.Equal("52 °C", viewModel.GpuTemperatureValue);
        Assert.Equal(3, viewModel.VisibleDashboardTelemetryGroups.Count);
        Assert.Equal(
            ["Battery", "Temperatures", "Fans"],
            viewModel.VisibleDashboardTelemetryGroups.Select(item => item.Label));
        Assert.Equal(
            "3,210 RPM / 2,875 RPM",
            viewModel.VisibleDashboardTelemetryGroups.Single(
                item => item.Label == "Fans").Value);
        Assert.Contains(nameof(viewModel.BatteryPowerFlowLabel), changed);
        Assert.Contains(nameof(viewModel.Fan1RpmLabel), changed);
        Assert.Contains(nameof(viewModel.Fan2RpmLabel), changed);
        Assert.Contains(nameof(viewModel.Fan1RpmValue), changed);
        Assert.Contains(nameof(viewModel.Fan2RpmValue), changed);
        Assert.Contains(nameof(viewModel.CpuTemperatureValue), changed);
        Assert.Contains(nameof(viewModel.GpuTemperatureValue), changed);

        changed.Clear();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            Revision = 2,
            ActivePowerSource = PowerSource.PluggedIn,
            LiveReadings = new LiveReadings(31_840, null, 0),
        });
        await WaitUntilAsync(() => viewModel.BatteryPowerFlowLabel == "Charging at 31.8 W");

        Assert.Equal("Charging at 31.8 W", viewModel.BatteryPowerFlowLabel);
        Assert.Equal("Fan 1: unavailable", viewModel.Fan1RpmLabel);
        Assert.Contains("0 RPM", viewModel.Fan2RpmLabel, StringComparison.Ordinal);
        Assert.Equal("Unavailable", viewModel.Fan1RpmValue);
        Assert.Equal("0 RPM", viewModel.Fan2RpmValue);
        Assert.Equal(
            "Unavailable / 0 RPM",
            viewModel.VisibleDashboardTelemetryGroups.Single(
                item => item.Label == "Fans").Value);
        Assert.Contains(nameof(viewModel.BatteryPowerFlowLabel), changed);
        Assert.Contains(nameof(viewModel.Fan1RpmLabel), changed);
        Assert.Contains(nameof(viewModel.Fan2RpmLabel), changed);
        Assert.Contains(nameof(viewModel.Fan1RpmValue), changed);
        Assert.Contains(nameof(viewModel.Fan2RpmValue), changed);
    }

    [Fact]
    public async Task DashboardTelemetry_CollapsesUnavailableTemperatures()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            LiveReadings = new LiveReadings(null, 2200, 2100, null, null),
        });
        await WaitUntilAsync(() => viewModel.IsConnected);

        LiveReadingGroupDisplayItem temperatures = Assert.Single(
            viewModel.VisibleDashboardTelemetryGroups,
            item => item.Label == "Temperatures");
        Assert.Equal("CPU Unavailable", temperatures.Value);
    }

    [Fact]
    public async Task LiveReadingsOnlyStatus_DoesNotInvalidateUnchangedPagePresentation()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false) with
        {
            Revision = 10,
            LiveReadings = new LiveReadings(-18_000, 2400, 2300),
        };
        int dispatchesBeforeBaseline = dispatcher.CompletedCalls;
        client.Publish(baseline);
        await WaitUntilAsync(() => dispatcher.CompletedCalls > dispatchesBeforeBaseline);
        Assert.Equal(baseline.LiveReadings, viewModel.LiveReadings);

        var changed = new ConcurrentPropertySet();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        ServiceStatusSnapshot update = baseline with
        {
            Revision = 11,
            LiveReadings = new LiveReadings(-18_500, 2450, 2350),
        };

        client.Publish(update);
        await WaitUntilAsync(() => viewModel.LiveReadings == update.LiveReadings);
        await WaitUntilAsync(() => changed.Contains(nameof(viewModel.TrayHoverLabelText)));

        Assert.Contains(nameof(viewModel.LiveReadings), changed);
        Assert.Contains(nameof(viewModel.BatteryPowerFlowLabel), changed);
        Assert.Contains(nameof(viewModel.Fan1RpmValue), changed);
        Assert.Contains(nameof(viewModel.Fan2RpmValue), changed);
        Assert.Contains(nameof(viewModel.TrayHoverLabelText), changed);
        Assert.DoesNotContain(nameof(viewModel.ProfileStateLabel), changed);
        Assert.DoesNotContain(nameof(viewModel.TroubleshootingServiceStatus), changed);
        Assert.DoesNotContain(nameof(viewModel.AboutDetectedLaptopStatus), changed);
        Assert.DoesNotContain(nameof(viewModel.CanAdjustChargeLimit), changed);
    }

    [Fact]
    public async Task StatusWithoutLiveReadingsPreservesTheLastOnDemandSample()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false) with
        {
            Revision = 10,
            LiveReadings = new LiveReadings(-18_000, 2400, 2300),
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.LiveReadings == baseline.LiveReadings);

        int dispatchesBeforeUpdate = dispatcher.CompletedCalls;
        client.Publish(baseline with
        {
            Revision = 11,
            LiveReadings = null,
        });
        await WaitUntilAsync(() => dispatcher.CompletedCalls > dispatchesBeforeUpdate);

        Assert.Equal(baseline.LiveReadings, viewModel.LiveReadings);
    }

    [Fact]
    public async Task UnverifiedDeviceClearsTheLastOnDemandSample()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false) with
        {
            Revision = 10,
            LiveReadings = new LiveReadings(-18_000, 2400, 2300),
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.LiveReadings == baseline.LiveReadings);

        client.Publish(baseline with
        {
            Revision = 11,
            DeviceVerified = false,
            LiveReadings = null,
        });
        await WaitUntilAsync(() => viewModel.LiveReadings is null);

        Assert.True(viewModel.IsConnected);
        Assert.False(viewModel.CanControlHardware);
    }

    [Fact]
    public async Task PlaybackMuteIndicatorCapabilityOnlyStatus_RefreshesCapabilities()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(
            client,
            new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false) with
        {
            Revision = 10,
            Capabilities = DeviceCapabilities.SafeTargetDefaults() with
            {
                SupportsPlaybackMuteIndicator = false,
            },
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsConnected);
        Assert.False(viewModel.Capabilities.SupportsPlaybackMuteIndicator);

        client.Publish(baseline with
        {
            Revision = 11,
            Capabilities = baseline.Capabilities! with
            {
                SupportsPlaybackMuteIndicator = true,
            },
        });
        await WaitUntilAsync(
            () => viewModel.Capabilities.SupportsPlaybackMuteIndicator);

        Assert.True(viewModel.Capabilities.SupportsPlaybackMuteIndicator);
    }

    [Fact]
    public async Task HiddenDashboard_DoesNotRebuildTelemetryPresentation()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false) with
        {
            Revision = 10,
            LiveReadings = new LiveReadings(-18_000, 2400, 2300, 46, 51),
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.LiveReadings == baseline.LiveReadings);

        viewModel.SetDashboardTelemetryPresentationActive(false);
        var changed = new ConcurrentPropertySet();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        ServiceStatusSnapshot update = baseline with
        {
            Revision = 11,
            LiveReadings = new LiveReadings(-18_500, 2450, 2350, 47, 52),
        };

        client.Publish(update);
        await WaitUntilAsync(() => viewModel.LiveReadings == update.LiveReadings);
        await WaitUntilAsync(() => changed.Contains(nameof(viewModel.TrayHoverLabelText)));

        Assert.Contains(nameof(viewModel.LiveReadings), changed);
        Assert.Contains(nameof(viewModel.TrayHoverLabelText), changed);
        Assert.DoesNotContain(nameof(viewModel.BatteryPowerFlowLabel), changed);
        Assert.DoesNotContain(nameof(viewModel.Fan1RpmValue), changed);
        Assert.DoesNotContain(nameof(viewModel.Fan2RpmValue), changed);
        Assert.DoesNotContain(nameof(viewModel.CpuTemperatureValue), changed);
        Assert.DoesNotContain(nameof(viewModel.GpuTemperatureValue), changed);
        Assert.DoesNotContain(nameof(viewModel.VisibleDashboardTelemetryGroups), changed);

        changed.Clear();
        viewModel.SetDashboardTelemetryPresentationActive(true);

        Assert.Contains(nameof(viewModel.BatteryPowerFlowLabel), changed);
        Assert.Contains(nameof(viewModel.Fan1RpmValue), changed);
        Assert.Contains(nameof(viewModel.Fan2RpmValue), changed);
        Assert.Contains(nameof(viewModel.CpuTemperatureValue), changed);
        Assert.Contains(nameof(viewModel.GpuTemperatureValue), changed);
        Assert.Contains(nameof(viewModel.VisibleDashboardTelemetryGroups), changed);
    }

    [Fact]
    public async Task LightingRendererOwnershipOnlyStatus_RefreshesLightingPresentation()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false) with
        {
            Revision = 10,
            ConfirmedState = CreateStatus(true, false).ConfirmedState! with
            {
                Lighting = null,
            },
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsKeyboardEffectUnconfirmed);

        client.Publish(baseline with
        {
            Revision = 11,
            LightingRendererOwnership = LightingRendererOwnershipState.Spectrum,
        });
        await WaitUntilAsync(() => !viewModel.IsKeyboardEffectUnconfirmed);

        Assert.False(viewModel.IsKeyboardEffectUnconfirmed);
    }

    [Fact]
    public async Task FanControlOverrideOnlyStatus_RefreshesFanControlPresentation()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        var initialFanControl = new FanControlSettings(
            FanMode.Fixed,
            3100,
            ManualFanCurve.Default);
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false) with
        {
            Revision = 10,
            FanControlOverride = initialFanControl,
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.FixedFanSpeedRpm == 3100);

        var updatedFanControl = new FanControlSettings(
            FanMode.Fixed,
            4200,
            ManualFanCurve.Default);
        client.Publish(baseline with
        {
            Revision = 11,
            FanControlOverride = updatedFanControl,
        });
        await WaitUntilAsync(() => viewModel.FixedFanSpeedRpm == 4200);

        Assert.Equal(4200, viewModel.FixedFanSpeedRpm);
    }

    [Fact]
    public async Task TrayHoverLabel_TracksPreferenceProfileAndBatteryPower()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        var changed = new ConcurrentPropertySet();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        Assert.Equal(
            "OpenBlade · Service unavailable",
            viewModel.TrayHoverLabelText);
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ActivePowerSource = PowerSource.Battery,
            LiveReadings = new LiveReadings(-18_400, 2600, 2500),
        });
        await WaitUntilAsync(() => viewModel.TrayHoverLabelText.Contains(
            "Using 18.4 W",
            StringComparison.Ordinal));

        Assert.Equal(
            "OpenBlade · Battery · Balanced · Using 18.4 W",
            viewModel.TrayHoverLabelText);
        Assert.Contains(nameof(viewModel.TrayHoverLabelText), changed);

        viewModel.TrayHoverLabelMode = TrayHoverLabelMode.BatteryPowerFlow;
        Assert.Equal("OpenBlade · Using 18.4 W", viewModel.TrayHoverLabelText);
        Assert.Equal(
            "Battery power flow",
            viewModel.SelectedTrayHoverLabelOption.Label);
    }

    [Fact]
    public async Task TelemetryPreferences_FilterDashboardAndOrderTrayHoverContent()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ActivePowerSource = PowerSource.Battery,
            LiveReadings = new LiveReadings(-18_400, 2600, 2500, 46, 51),
        });
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.ApplyTelemetryPreferences(
            [LiveReadingItemKind.CpuTemperature, LiveReadingItemKind.Fan1Speed],
            [
                new(TrayHoverLabelItemKind.GpuTemperature, true),
                new(TrayHoverLabelItemKind.PowerSource, true),
                new(TrayHoverLabelItemKind.PowerMode, true),
                new(TrayHoverLabelItemKind.CpuTemperature, true),
            ]);

        Assert.Equal(
            ["Temperatures", "Fan 1"],
            viewModel.VisibleDashboardTelemetryGroups.Select(item => item.Label));
        Assert.Equal(
            "OpenBlade · GPU 51 °C · Battery · Balanced · CPU 46 °C",
            viewModel.TrayHoverLabelText);
        Assert.Equal(
            [
                TrayHoverLabelItemKind.AppName,
                TrayHoverLabelItemKind.GpuTemperature,
                TrayHoverLabelItemKind.PowerSource,
                TrayHoverLabelItemKind.PowerMode,
                TrayHoverLabelItemKind.CpuTemperature,
            ],
            viewModel.TrayHoverLabelPreferences
                .Where(item => item.IsEnabled)
                .Select(item => item.Kind));
    }

    [Fact]
    public async Task TrayHoverLabel_ExplainsWhenSelectedItemsDoNotFit()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ActivePowerSource = PowerSource.Battery,
            LiveReadings = new LiveReadings(-18_400, 2300, 2500, 46, 51),
        });
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.ApplyTelemetryPreferences(
            TelemetryPreferenceDefaults.DashboardItems,
            [
                new(TrayHoverLabelItemKind.AppName, true),
                new(TrayHoverLabelItemKind.PowerMode, true),
                new(TrayHoverLabelItemKind.BatteryPower, true),
                new(TrayHoverLabelItemKind.CpuTemperature, true),
                new(TrayHoverLabelItemKind.GpuTemperature, true),
                new(TrayHoverLabelItemKind.Fan1Speed, true),
                new(TrayHoverLabelItemKind.Fan2Speed, true),
            ]);

        Assert.True(viewModel.HasTrayHoverLabelOverflow);
        Assert.Equal(
            "OpenBlade · Balanced · Using 18.4 W · CPU 46 °C · GPU 51 °C",
            viewModel.TrayHoverLabelText);
        Assert.Equal(
            "Not shown: Fan 1 speed, Fan 2 speed. Windows limits tray hover text to 63 characters.",
            viewModel.TrayHoverLabelOverflowMessage);
    }

    [Fact]
    public async Task TrayIcon_UsesEffectiveProfileWhenFirmwareReadbackIsUnavailable()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        PowerProfile usbC = status.Profiles.UsbC;
        client.Publish(status with
        {
            ActivePowerSource = PowerSource.UsbC,
            ConfirmedState = status.ConfirmedState! with { Performance = null },
            DesiredState = status.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    usbC.Performance,
                    SettingValueSource.PowerProfile),
            },
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.UsbC);

        Assert.Equal(PerformancePreset.Balanced, viewModel.SelectedPerformance);
        Assert.Equal(TrayIconState.UsbCBalanced, viewModel.TrayIconState);
    }

    [Fact]
    public async Task TrayIcon_UsesQueuedPerformanceThenReturnsToConfirmedMode()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        Guid sessionId = Guid.NewGuid();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);

        client.Publish(status with
        {
            PerformanceShortcutPreview = new PerformanceShortcutPreview(
                sessionId,
                1,
                PerformancePreset.Performance),
        });
        await WaitUntilAsync(
            () => viewModel.SelectedPerformance == PerformancePreset.Performance);

        Assert.Equal(TrayIconState.AcPerformance, viewModel.TrayIconState);

        client.Publish(status with
        {
            Revision = status.Revision + 1,
            PerformanceShortcutPreview = null,
        });
        await WaitUntilAsync(
            () => viewModel.SelectedPerformance == PerformancePreset.Balanced);

        Assert.Equal(TrayIconState.AcBalanced, viewModel.TrayIconState);
    }

    [Fact]
    public async Task Status_FormatsZeroBatteryFlowForCurrentPowerSource()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ActivePowerSource = PowerSource.PluggedIn,
            LiveReadings = new LiveReadings(0, null, null),
        });
        await WaitUntilAsync(() => viewModel.IsConnected);
        Assert.Equal("Not charging", viewModel.BatteryPowerFlowLabel);

        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            Revision = 2,
            ActivePowerSource = PowerSource.Battery,
            LiveReadings = new LiveReadings(0, null, null),
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.Battery);
        Assert.Equal("Battery power unavailable", viewModel.BatteryPowerFlowLabel);
    }

    [Fact]
    public async Task Status_UsesConfirmedBatterySettingInProminentSummary()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());

        Assert.Equal("Unavailable", viewModel.CurrentBatterySettingLabel);
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);

        client.Publish(baseline with
        {
            ConfirmedState = baseline.ConfirmedState! with
            {
                Battery = new ConfirmedBatteryState(false, null, false),
            },
        });
        await WaitUntilAsync(() => viewModel.IsConnected);
        Assert.Equal("Battery protection off", viewModel.CurrentBatterySettingLabel);

        client.Publish(baseline with
        {
            Revision = 2,
            ConfirmedState = baseline.ConfirmedState! with
            {
                Battery = new ConfirmedBatteryState(true, 80, true),
            },
            FullChargeOverride = true,
        });
        await WaitUntilAsync(() => viewModel.FullChargeOverride);
        Assert.Equal("Charging to 100%", viewModel.CurrentBatterySettingLabel);
        Assert.Equal("Ready", viewModel.ProfileStateLabel);

        client.Publish(baseline with
        {
            Revision = 3,
            ConfirmedState = baseline.ConfirmedState! with { Battery = null },
            FullChargeOverride = false,
        });
        await WaitUntilAsync(() => !viewModel.FullChargeOverride);
        Assert.Equal("Battery setting unavailable", viewModel.CurrentBatterySettingLabel);
    }

    [Fact]
    public async Task ControlAccessStatus_DistinguishesEveryFailClosedState()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        Assert.Equal("OpenBlade service isn’t running", viewModel.ControlAccessStatus);
        Assert.True(viewModel.IsReadinessError);
        Assert.Equal("Unavailable", viewModel.TroubleshootingServiceStatus);
        Assert.Equal(
            "Unavailable",
            viewModel.TroubleshootingKeyboardDriverModeStatus);
        Assert.Equal(
            OpenBladeProductVersion.Read(typeof(MainViewModel).Assembly),
            viewModel.AboutUiVersionStatus);
        Assert.Equal("Unavailable", viewModel.AboutServiceVersionStatus);
        Assert.Equal("Unavailable", viewModel.AboutDetectedLaptopStatus);
        Assert.Equal("Unavailable", viewModel.TroubleshootingUsbDeviceStatus);
        Assert.False(viewModel.HasTroubleshootingRecentError);
        Assert.Empty(viewModel.TroubleshootingRecentErrorStatus);

        client.Publish(CreateStatus(verified: false, blocked: true));
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Laptop not found"
            && viewModel.PerformanceSourceLabel == "Laptop not found");
        Assert.Equal("Laptop not found", viewModel.ProfileStateLabel);
        Assert.True(viewModel.IsReadinessError);
        Assert.False(viewModel.IsReadinessWarning);
        Assert.Equal("Running", viewModel.TroubleshootingServiceStatus);
        Assert.Equal("0.1.0-test", viewModel.AboutServiceVersionStatus);
        Assert.Equal("Not detected", viewModel.AboutDetectedLaptopStatus);
        Assert.Equal("Laptop not found", viewModel.TroubleshootingControlsStatus);
        Assert.False(viewModel.HasAttention);
        Assert.Equal("Laptop not found", viewModel.PerformanceSourceLabel);

        client.Publish(CreateStatus(
            verified: true,
            blocked: true,
            conflicts: ["RazerAppEngine"]));
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Controls unavailable"
            && viewModel.PerformanceSourceLabel == "Controls unavailable");
        Assert.Equal("Controls unavailable", viewModel.ProfileStateLabel);
        Assert.True(viewModel.IsReadinessWarning);
        Assert.False(viewModel.IsReadinessError);
        Assert.Equal(
            "RazerAppEngine",
            viewModel.TroubleshootingOtherRazerSoftwareStatus);
        Assert.Equal("Another Razer app is running", viewModel.AttentionSummary);
        Assert.Equal("Razer app active", viewModel.AttentionBadgeLabel);
        Assert.Equal("Controls unavailable", viewModel.PerformanceSourceLabel);

        client.Publish(CreateStatus(verified: true, blocked: true));
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Controls unavailable");

        client.Publish(CreateStatus(verified: true, blocked: true) with
        {
            Revision = 2,
            ActivePowerSource = PowerSource.UsbC,
            Message = "Power adapter classification is temporarily unavailable; the last confirmed power source is retained and control writes are blocked.",
        });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.UsbC);
        Assert.Equal("Ready", viewModel.ControlAccessStatus);
        Assert.Equal("Ready", viewModel.ProfileStateLabel);
        Assert.True(viewModel.IsReadinessReady);
        Assert.False(viewModel.IsReadinessWarning);
        Assert.False(viewModel.CanControlHardware);
        Assert.Equal("USB-C", viewModel.TroubleshootingPowerSourceStatus);

        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            Revision = 3,
            Firmware = new FirmwareVersions(
                "3.01",
                "1.04",
                "Unavailable",
                "98.3.5c.40.3d",
                RazerBladeModelCatalog.OpenBladeTargetModelNumber),
            KeyboardDriverModeActive = true,
        });
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Ready"
            && viewModel.ActivePowerSource == PowerSource.PluggedIn);
        Assert.Equal("Ready", viewModel.ProfileStateLabel);
        Assert.True(viewModel.IsReadinessReady);
        Assert.False(viewModel.IsReadinessWarning);
        Assert.Equal("AC", viewModel.TroubleshootingPowerSourceStatus);
        Assert.Equal("None", viewModel.TroubleshootingOtherRazerSoftwareStatus);
        Assert.Equal("Loaded", viewModel.TroubleshootingKeyboardDriverModeStatus);
        Assert.Equal("Razer Blade 16 (2026), RZ09-0581", viewModel.AboutDetectedLaptopStatus);
        Assert.Equal("VID 1532 · PID 02E0", viewModel.TroubleshootingUsbDeviceStatus);
        Assert.Equal("3.01", viewModel.AboutBiosVersionStatus);
        Assert.Equal("1.04", viewModel.AboutEcVersionStatus);
        Assert.Equal("Unavailable", viewModel.AboutMcuVersionStatus);
        Assert.Equal("98.3.5c.40.3d", viewModel.AboutVbiosVersionStatus);

        var driverStatusChanges = new List<string?>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName
                == nameof(MainViewModel.TroubleshootingKeyboardDriverModeStatus))
            {
                driverStatusChanges.Add(args.PropertyName);
            }
        };
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            Revision = 4,
            KeyboardDriverModeActive = null,
        });
        await WaitUntilAsync(() =>
            viewModel.TroubleshootingKeyboardDriverModeStatus == "Unknown"
            && driverStatusChanges.Contains(
                nameof(MainViewModel.TroubleshootingKeyboardDriverModeStatus)));
        Assert.Contains(
            nameof(MainViewModel.TroubleshootingKeyboardDriverModeStatus),
            driverStatusChanges);

        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            Revision = 5,
            ActivePowerSource = PowerSource.ExternalUnknown,
        });
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Checking power source"
            && viewModel.ActivePowerSource == PowerSource.ExternalUnknown);
        Assert.Equal("Checking power source", viewModel.ProfileStateLabel);
        Assert.True(viewModel.IsReadinessChecking);
        Assert.False(viewModel.IsReadinessReady);
        Assert.Equal("Checking power source", viewModel.TroubleshootingPowerSourceStatus);
        Assert.False(viewModel.CanControlHardware);

        client.PublishReconnecting();
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "OpenBlade service isn’t running");
        Assert.Equal("Service unavailable", viewModel.ProfileStateLabel);
        Assert.Equal("Unavailable", viewModel.ActivePowerSourceLabel);
        Assert.Equal("Unavailable", viewModel.ActivePerformanceLabel);
        Assert.Equal("Unavailable", viewModel.ActiveFanModeLabel);
        Assert.Equal("Unavailable", viewModel.CurrentBatterySettingLabel);
        Assert.Equal("Unavailable", viewModel.BatteryPowerFlowLabel);
        Assert.Equal("Unavailable", viewModel.Fan1RpmValue);
        Assert.Equal("Unavailable", viewModel.Fan2RpmValue);
        Assert.Equal("Unavailable", viewModel.PerformanceSourceLabel);
        Assert.Equal("Unavailable", viewModel.FanSourceLabel);
        Assert.Equal("Unavailable", viewModel.LightingSourceLabel);
        Assert.Equal("Unavailable", viewModel.BatterySourceLabel);
        Assert.Null(viewModel.LiveReadings);
        Assert.True(viewModel.IsReadinessError);
        Assert.Equal("Unavailable", viewModel.TroubleshootingServiceStatus);
        Assert.Equal(
            "Unavailable",
            viewModel.TroubleshootingKeyboardDriverModeStatus);
    }

    [Fact]
    public async Task TemporaryDeviceProbeFailure_IsPresentedAsCheckingWithoutLosingValues()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        LightingSettings lighting = LightingSettingsFactory.Reactive(
            new RgbColor(0, 170, 255),
            64,
            ReactiveLightingDuration.Medium);
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        baseline = baseline with
        {
            ConfirmedState = baseline.ConfirmedState! with { Lighting = lighting },
            DesiredState = baseline.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    lighting,
                    SettingValueSource.TemporaryOverride),
            },
            LightingRendererOwnership = LightingRendererOwnershipState.Reactive,
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsConnected && viewModel.IsReadinessReady);
        string performance = viewModel.ActivePerformanceLabel;
        string fans = viewModel.ActiveFanModeLabel;
        Assert.Equal(LightingEffect.Reactive, viewModel.LightingEffectEditor);
        Assert.Equal(64, viewModel.LightingBrightnessEditor);
        Assert.False(viewModel.IsKeyboardEffectUnconfirmed);

        client.Publish(baseline with
        {
            Revision = 2,
            DeviceVerified = false,
            WritesBlocked = true,
            Capabilities = DeviceCapabilities.Unavailable(),
            ConfirmedState = baseline.ConfirmedState! with { Lighting = null },
            LightingRendererOwnership = LightingRendererOwnershipState.None,
            Message = "Checking the laptop connection after a temporary device-access failure.",
        });
        await WaitUntilAsync(() =>
            viewModel.ProfileStateLabel == "Checking laptop"
            && viewModel.PerformanceSourceLabel == "Checking laptop"
            && viewModel.FanSourceLabel == "Checking laptop"
            && viewModel.LightingSourceLabel == "Checking laptop"
            && viewModel.BatterySourceLabel == "Checking laptop");

        Assert.Equal("Checking laptop", viewModel.ControlAccessStatus);
        Assert.True(viewModel.IsReadinessChecking);
        Assert.False(viewModel.IsReadinessReady);
        Assert.False(viewModel.IsReadinessWarning);
        Assert.False(viewModel.IsReadinessError);
        Assert.False(viewModel.CanControlHardware);
        Assert.False(viewModel.CanControlLighting);
        Assert.False(viewModel.IsLightingControlUnavailable);
        Assert.False(viewModel.SetLightingCommand.CanExecute(viewModel.LightingDraft));
        Assert.Equal(performance, viewModel.ActivePerformanceLabel);
        Assert.Equal(fans, viewModel.ActiveFanModeLabel);
        Assert.Equal(LightingEffect.Reactive, viewModel.LightingEffectEditor);
        Assert.Equal(64, viewModel.LightingBrightnessEditor);
        Assert.False(viewModel.IsKeyboardEffectUnconfirmed);

        client.Publish(CreateStatus(verified: true, blocked: true) with
        {
            Revision = 3,
            Message = "Refreshing laptop controls after a temporary device-access failure. Last read failed.",
        });
        await WaitUntilAsync(() => viewModel.ControlAccessStatus == "Checking laptop");

        Assert.Equal("Checking laptop", viewModel.ProfileStateLabel);
        Assert.True(viewModel.IsReadinessChecking);
        Assert.False(viewModel.IsReadinessReady);
        Assert.False(viewModel.IsReadinessWarning);
        Assert.False(viewModel.IsReadinessError);
        Assert.False(viewModel.CanControlHardware);
        Assert.False(viewModel.CanControlLighting);
        Assert.False(viewModel.SetLightingCommand.CanExecute(viewModel.LightingDraft));
        Assert.Equal(performance, viewModel.ActivePerformanceLabel);
        Assert.Equal(fans, viewModel.ActiveFanModeLabel);

        client.Publish(CreateStatus(verified: false, blocked: true) with
        {
            Revision = 4,
            Message = "Checking the laptop connection after a temporary device-access failure.",
        });
        await WaitUntilAsync(() => viewModel.ControlAccessStatus == "Checking laptop");

        Assert.Equal("Checking laptop", viewModel.ProfileStateLabel);
        Assert.True(viewModel.IsReadinessChecking);
        Assert.False(viewModel.IsReadinessReady);
        Assert.False(viewModel.IsReadinessWarning);
        Assert.False(viewModel.IsReadinessError);
        Assert.False(viewModel.CanControlHardware);
        Assert.False(viewModel.CanControlLighting);
        Assert.False(viewModel.SetLightingCommand.CanExecute(viewModel.LightingDraft));
        Assert.Equal("Checking laptop", viewModel.PerformanceSourceLabel);
        Assert.Equal("Checking laptop", viewModel.FanSourceLabel);
        Assert.Equal("Checking laptop", viewModel.LightingSourceLabel);
        Assert.Equal("Checking laptop", viewModel.BatterySourceLabel);
        Assert.Equal(performance, viewModel.ActivePerformanceLabel);
        Assert.Equal(fans, viewModel.ActiveFanModeLabel);
    }

    [Fact]
    public async Task RetainedPowerSourceRecheck_KeepsKnownManualControlsAvailable()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        ServiceStatusSnapshot ready = CreateStatus(verified: true, blocked: false) with
        {
            ConfirmedState = CreateStatus(true, false).ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Automatic,
            },
        };
        client.Publish(ready);
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        client.Publish(ready with
        {
            Revision = 2,
            WritesBlocked = true,
            Message = "Power adapter classification is temporarily unavailable; " +
                "the last confirmed power source is retained and control writes are blocked.",
            Capabilities = DeviceCapabilities.Unavailable(),
        });
        await WaitUntilAsync(() => viewModel.IsReadinessReady && !viewModel.CanControlHardware);

        Assert.True(viewModel.CanRequestHardwareControl);
        Assert.True(viewModel.IsPerformanceVisible);
        Assert.True(viewModel.IsFanControlVisible);
        Assert.NotEmpty(viewModel.AvailablePerformancePresets);
        Assert.NotEmpty(viewModel.AvailableFanModes);
        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Balanced));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
        Assert.True(viewModel.CanChangeBatteryProtection);
        Assert.True(viewModel.CanAdjustChargeLimit);
        Assert.True(viewModel.ToggleFullChargeCommand.CanExecute(true));
    }

    [Theory]
    [InlineData("performance")]
    [InlineData("fans")]
    [InlineData("battery")]
    public async Task HardwareEditorAvailability_RemainsModelOwnedWhenAccessChangesMidWrite(
        string operation)
    {
        var client = new FakeServiceClient { BlockMutations = true };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        Task apply = operation switch
        {
            "performance" => Assert.IsType<AsyncRelayCommand>(
                    viewModel.SetPerformanceCommand)
                .ExecuteAsync(PerformancePreset.Performance),
            "fans" => Assert.IsType<AsyncRelayCommand>(
                    viewModel.SetFanModeCommand)
                .ExecuteAsync(FanMode.Automatic),
            "battery" => Assert.IsType<AsyncRelayCommand>(
                    viewModel.ToggleBatteryProtectionCommand)
                .ExecuteAsync(null),
            _ => throw new InvalidOperationException(),
        };
        await client.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        if (operation == "performance")
        {
            Assert.False(viewModel.CanEditPerformance);
        }
        if (operation == "fans")
        {
            Assert.False(viewModel.CanEditFanControl);
        }
        if (operation == "battery")
        {
            Assert.False(viewModel.CanChangeBatteryProtection);
        }

        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            WritesBlocked = true,
            Message = "Controls unavailable",
        });
        await WaitUntilAsync(() => !viewModel.CanControlHardware);
        client.CompleteMutation();
        await apply;

        Assert.False(viewModel.CanEditPerformance);
        Assert.False(viewModel.CanEditFanControl);
        Assert.False(viewModel.CanChangeBatteryProtection);
    }

    [Fact]
    public async Task GenericLaptopConnectionCheck_IsPresentedAsCheckingInsteadOfMissing()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.ProfileStateLabel == "Ready");

        client.Publish(status with
        {
            Revision = status.Revision + 1,
            DeviceVerified = false,
            WritesBlocked = true,
            Message = "Checking laptop connection.",
        });
        await WaitUntilAsync(() =>
            viewModel.ProfileStateLabel == "Checking laptop"
            && viewModel.PerformanceSourceLabel == "Checking laptop"
            && viewModel.FanSourceLabel == "Checking laptop"
            && viewModel.LightingSourceLabel == "Checking laptop"
            && viewModel.BatterySourceLabel == "Checking laptop");

        Assert.Equal("Checking laptop", viewModel.ControlAccessStatus);
        Assert.True(viewModel.IsReadinessChecking);
        Assert.False(viewModel.IsReadinessError);
        Assert.False(viewModel.CanControlHardware);
        Assert.False(viewModel.CanControlLighting);
        Assert.False(viewModel.SetLightingCommand.CanExecute(viewModel.LightingDraft));
        Assert.Equal("Checking laptop", viewModel.PerformanceSourceLabel);
        Assert.Equal("Checking laptop", viewModel.FanSourceLabel);
        Assert.Equal("Checking laptop", viewModel.LightingSourceLabel);
        Assert.Equal("Checking laptop", viewModel.BatterySourceLabel);
    }

    [Fact]
    public async Task AboutVersions_KeepUiVersionAndTrackConnectedServiceVersion()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(
            client,
            new RecordingDispatcher(),
            new FakeDiagnosticArchiveSaver(),
            new FakeStartupRegistration(false),
            uiVersion: "2.0.0",
            sessionAgentVersion: "1.8.0",
            installedServiceVersion: "1.6.0");
        await viewModel.StartAsync();

        Assert.Equal("2.0.0", viewModel.AboutUiVersionStatus);
        Assert.Equal("1.8.0", viewModel.AboutSessionAgentVersionStatus);
        Assert.Equal("1.6.0", viewModel.AboutServiceVersionStatus);

        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ServiceVersion = "1.9.0",
        });
        await WaitUntilAsync(() => viewModel.IsConnected);
        Assert.Equal("1.9.0", viewModel.AboutServiceVersionStatus);

        client.PublishReconnecting();
        await WaitUntilAsync(() => !viewModel.IsConnected);
        Assert.Equal("1.6.0", viewModel.AboutServiceVersionStatus);
    }

    [Fact]
    public void AboutVersions_UseUnavailableForMissingInstalledComponents()
    {
        var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new RecordingDispatcher(),
            new FakeDiagnosticArchiveSaver(),
            new FakeStartupRegistration(false),
            uiVersion: "2.0.0",
            sessionAgentVersion: " ");

        Assert.Equal("Unavailable", viewModel.AboutSessionAgentVersionStatus);
    }

    [Fact]
    public async Task AboutBatteryHealth_ShowsCapacityRatioAndTracksAvailability()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        int completedCallsBeforeBaseline = dispatcher.CompletedCalls;
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() =>
            viewModel.IsConnected
            && dispatcher.CompletedCalls > completedCallsBeforeBaseline);

        Assert.Equal("Unavailable", viewModel.AboutBatteryHealthStatus);

        int notificationCount = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.AboutBatteryHealthStatus))
            {
                Interlocked.Increment(ref notificationCount);
            }
        };
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            Revision = 2,
            BatteryHealth = new BatteryHealthStatus(95_200, 90_440),
        });
        await WaitUntilAsync(() => Volatile.Read(ref notificationCount) == 1);

        Assert.Equal("95% (90.4 Wh of 95.2 Wh)", viewModel.AboutBatteryHealthStatus);

        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            Revision = 3,
            BatteryHealth = null,
        });
        await WaitUntilAsync(() => Volatile.Read(ref notificationCount) == 2);

        Assert.Equal("Unavailable", viewModel.AboutBatteryHealthStatus);
    }

    [Fact]
    public async Task TestedPlatformWarning_TracksFirmwareAndHardwareMismatches()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        ServiceStatusSnapshot stable = CreateStatus(verified: true, blocked: false) with
        {
            FirmwareCompatibility = FirmwareCompatibilityStatus.Tested,
            Firmware = new FirmwareVersions(
                "3.01",
                "1.04",
                "1.6.0.0",
                "98.3.5c.40.3d",
                "RZ09-0581"),
        };
        client.Publish(stable);
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.False(viewModel.HasUntestedPlatformVersion);
        Assert.Equal("", viewModel.DashboardTestedPlatformWarningMessage);

        client.Publish(stable with
        {
            Revision = stable.Revision + 1,
            Firmware = stable.Firmware! with { McuVersion = "1.7.0.0" },
        });
        await WaitUntilAsync(() => viewModel.HasUntestedPlatformVersion);

        Assert.False(viewModel.HasIncompatibleFirmware);
        Assert.Equal(
            "Untested platform configuration",
            viewModel.PlatformCompatibilityWarningTitle);
        Assert.Equal(
            "OpenBlade has not tested this configuration: MCU 1.07.",
            viewModel.DashboardTestedPlatformWarningMessage);
        Assert.Contains(
            "This is a compatibility notice, not a hardware failure.",
            viewModel.AboutTestedPlatformWarningMessage,
            StringComparison.Ordinal);

        client.Publish(stable with
        {
            Revision = stable.Revision + 2,
            DeviceVerified = false,
            WritesBlocked = true,
            FirmwareCompatibility = FirmwareCompatibilityStatus.UntestedBlocked,
            Firmware = stable.Firmware! with { BiosVersion = "3.02" },
        });
        await WaitUntilAsync(() =>
            viewModel.BatterySourceLabel == "Incompatible firmware");

        Assert.Equal("Incompatible firmware", viewModel.PlatformCompatibilityWarningTitle);
        Assert.Equal("Incompatible firmware", viewModel.ProfileStateLabel);
        Assert.Equal("Incompatible firmware", viewModel.ControlAccessStatus);
        Assert.Equal("Incompatible firmware", viewModel.PerformanceSourceLabel);
        Assert.Equal("Incompatible firmware", viewModel.FanSourceLabel);
        Assert.Equal("Incompatible firmware", viewModel.LightingSourceLabel);
        Assert.Equal("Incompatible firmware", viewModel.BatterySourceLabel);
        Assert.Equal("Incompatible firmware", viewModel.TroubleshootingControlsStatus);
        Assert.Contains(
            "found a supported laptop with incompatible firmware",
            viewModel.LightingControlUnavailableMessage,
            StringComparison.Ordinal);
        Assert.True(viewModel.IsReadinessWarning);
        Assert.False(viewModel.IsReadinessError);
        Assert.DoesNotContain(
            "Laptop not found",
            string.Join(
                '|',
                viewModel.ProfileStateLabel,
                viewModel.ControlAccessStatus,
                viewModel.PerformanceSourceLabel,
                viewModel.FanSourceLabel,
                viewModel.LightingSourceLabel,
                viewModel.BatterySourceLabel),
            StringComparison.Ordinal);

        client.Publish(stable with
        {
            Revision = stable.Revision + 3,
            DeviceVerified = false,
            WritesBlocked = true,
            AllowUntestedFirmware = true,
            FirmwareCompatibility = FirmwareCompatibilityStatus.UntestedAllowed,
            Firmware = stable.Firmware! with { BiosVersion = "3.02" },
        });
        await WaitUntilAsync(() => viewModel.AllowUntestedFirmware);

        Assert.DoesNotContain(
            "unless you enable the firmware override",
            viewModel.LightingControlUnavailableMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            "find a supported laptop",
            viewModel.LightingControlUnavailableMessage,
            StringComparison.Ordinal);

        client.Publish(stable with
        {
            Revision = stable.Revision + 4,
            DeviceVerified = false,
            FirmwareCompatibility = FirmwareCompatibilityStatus.Unknown,
            Firmware = stable.Firmware! with { ModelNumber = "RZ09-9999ABC" },
        });
        await WaitUntilAsync(() =>
            viewModel.PlatformCompatibilityWarningTitle == "Incompatible hardware");

        Assert.Equal("RZ09-9999ABC", viewModel.AboutDetectedLaptopStatus);
        Assert.Equal(
            "OpenBlade has not tested this configuration: Model RZ09-9999ABC.",
            viewModel.DashboardTestedPlatformWarningMessage);

        client.Publish(stable with
        {
            Revision = stable.Revision + 5,
            Firmware = new FirmwareVersions(
                "2.02",
                "1.09",
                "1.9.0.0",
                "Unavailable",
                RazerBladeModelCatalog.Rz090528ModelNumber),
        });
        await WaitUntilAsync(() =>
            viewModel.AboutDetectedLaptopStatus == "Razer Blade 16 (2025), RZ09-0528");

        Assert.Equal("VID 1532 · PID 02C6", viewModel.TroubleshootingUsbDeviceStatus);
        Assert.False(viewModel.HasUntestedPlatformVersion);
        Assert.Equal(string.Empty, viewModel.DashboardTestedPlatformWarningMessage);
    }

    [Fact]
    public async Task ShutdownOpenBlade_RequestsTypedServiceShutdown()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());

        await viewModel.ShutdownOpenBladeAsync(CancellationToken.None);

        Assert.True(client.ShutdownRequested);
    }

    [Fact]
    public async Task Command_SendsTypedTemporaryOverrideAndDisplaysOperationResult()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Intent stored without hardware application."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);

        Assert.Equal(1, client.MutationCalls);
        Assert.Equal(["performance:Performance"], client.MutationOperations);
        Assert.Equal(PerformancePreset.Performance, client.PerformanceOverride!.Value);
        Assert.Equal(OverrideLifetime.UntilPowerSourceChange, client.PerformanceOverride.Lifetime);
        Assert.Equal("Intent stored without hardware application.", viewModel.OperationStatusMessage);
        Assert.Equal(
            "OpenBlade saved the change, but the laptop has not applied it.",
            viewModel.OperationFeedbackMessage);
        Assert.True(viewModel.HasOperationFeedback);
        Assert.False(viewModel.HasTroubleshootingRecentError);
        Assert.Empty(viewModel.TroubleshootingRecentErrorStatus);
    }

    [Fact]
    public async Task TroubleshootingRecentError_TracksFailuresAndClearsAfterSuccess()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(
                ControlOperationStatus.Failed,
                "Firmware rejected the request."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);

        Assert.True(viewModel.HasTroubleshootingRecentError);
        Assert.Equal("Firmware rejected the request", viewModel.TroubleshootingRecentErrorStatus);
        Assert.Contains(nameof(MainViewModel.TroubleshootingRecentErrorStatus), changed);
        Assert.Contains(nameof(MainViewModel.HasTroubleshootingRecentError), changed);

        client.NextResult = new ControlOperationResult(ControlOperationStatus.Applied, "Applied");
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Silent);

        Assert.False(viewModel.HasTroubleshootingRecentError);
        Assert.Empty(viewModel.TroubleshootingRecentErrorStatus);
    }

    [Fact]
    public async Task ActiveProfileCommand_AlwaysUsesPowerSourceScopedOverride()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);

        Assert.Equal(
            OverrideLifetime.UntilPowerSourceChange,
            client.PerformanceOverride!.Lifetime);
        Assert.Empty(viewModel.OperationFeedbackMessage);
        Assert.False(viewModel.HasOperationFeedback);
    }

    [Fact]
    public async Task ThermalCommands_KeepPerformanceIntentAndFanTargetsEditableDuringManualFans()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false) with
        {
            ConfirmedState = CreateStatus(true, false).ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Maximum,
            },
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.SelectedFanMode == FanMode.Maximum);

        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Silent));
        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Balanced));
        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Performance));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Maximum));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Fixed));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.ManualCurve));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
    }

    [Fact]
    public async Task GenericManualReadback_AllowsDirectFixedAndManualCurveChanges()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        var curve = new FanControlSettings(
            FanMode.ManualCurve,
            3700,
            ManualFanCurve.Default);
        client.Publish(baseline with
        {
            DesiredState = baseline.DesiredState with
            {
                Fans = new ResolvedSetting<FanMode>(
                    FanMode.ManualCurve,
                    SettingValueSource.TemporaryOverride),
                FanControl = curve,
            },
            FanOverride = new ManualOverride<FanMode>(
                FanMode.ManualCurve,
                OverrideLifetime.UntilPowerSourceChange),
            FanControlOverride = curve,
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = null,
                Fans = FanMode.Maximum,
            },
        });
        await WaitUntilAsync(() => viewModel.SelectedFanMode == FanMode.ManualCurve);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetFanModeCommand)
            .ExecuteAsync(FanMode.Fixed);

        Assert.Equal(FanMode.Fixed, viewModel.SelectedFanMode);
        Assert.Equal(FanMode.Fixed, client.LastFanControlSettings!.Mode);

        var fixedSettings = new FanControlSettings(
            FanMode.Fixed,
            3700,
            ManualFanCurve.Default);
        client.Publish(baseline with
        {
            Revision = baseline.Revision + 1,
            DesiredState = baseline.DesiredState with
            {
                Fans = new ResolvedSetting<FanMode>(
                    FanMode.Fixed,
                    SettingValueSource.TemporaryOverride),
                FanControl = fixedSettings,
            },
            FanOverride = new ManualOverride<FanMode>(
                FanMode.Fixed,
                OverrideLifetime.UntilPowerSourceChange),
            FanControlOverride = fixedSettings,
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = null,
                Fans = FanMode.Maximum,
            },
        });
        await WaitUntilAsync(() => viewModel.SelectedFanMode == FanMode.Fixed);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetFanModeCommand)
            .ExecuteAsync(FanMode.ManualCurve);

        Assert.Equal(FanMode.ManualCurve, viewModel.SelectedFanMode);
        Assert.Equal(FanMode.ManualCurve, client.LastFanControlSettings!.Mode);
    }

    [Fact]
    public async Task DeviceFanRangeConstrainsLoadedAndEditedManualCurvePoints()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            FanSpeedRange = new FanSpeedRange(2000, 5000, 100),
        };
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: capabilities));
        await WaitUntilAsync(() => viewModel.FixedFanSpeedMaximumRpm == 5000);

        Assert.All(
            viewModel.CpuFanCurveEditorPoints.Concat(
                viewModel.GpuFanCurveEditorPoints),
            point => Assert.InRange(point.SpeedRpm, 2000, 5000));

        FanCurveEditorPoint last = viewModel.CpuFanCurveEditorPoints[^1];
        viewModel.UpdateFanCurvePoint(last, 5400);

        Assert.Equal(5000, viewModel.CpuFanCurveEditorPoints[^1].SpeedRpm);
        Assert.Equal(5000, viewModel.CurrentFanControlSettings.ManualCurve.Cpu[^1].SpeedRpm);
    }

    [Fact]
    public async Task PerformanceIntent_RemainsEditableWhenMaximumIsEffectiveButNotConfirmed()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        client.Publish(status with
        {
            DesiredState = status.DesiredState with
            {
                Fans = new ResolvedSetting<FanMode>(
                    FanMode.Maximum,
                    SettingValueSource.TemporaryOverride),
            },
        });
        await WaitUntilAsync(() => viewModel.SelectedFanMode == FanMode.Maximum);

        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Performance));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
    }

    [Fact]
    public async Task MaximumFans_RemainAvailableAcrossAcPerformanceIntents()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false) with
        {
            ConfirmedState = CreateStatus(true, false).ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Automatic,
            },
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.SetFanModeCommand.CanExecute(FanMode.Maximum));

        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Maximum));

        client.Publish(status with
        {
            Revision = status.Revision + 1,
            Message = "Performance active",
            DesiredState = status.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Performance,
                    SettingValueSource.PowerProfile),
            },
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Performance active");

        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Maximum));
    }

    [Fact]
    public async Task MaximumFans_RemainAvailableWhenCachedThermalStateIsUnavailable()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        client.Publish(status with { ConfirmedState = null });
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Maximum));
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
    }

    [Fact]
    public async Task ClearManualOverrides_WithNoOverrides_IsIdempotent()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: false, blocked: true));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.True(viewModel.ClearManualOverridesCommand.CanExecute(null));
        await Assert.IsType<AsyncRelayCommand>(viewModel.ClearManualOverridesCommand)
            .ExecuteAsync(null);

        Assert.Equal(0, client.MutationCalls);
        Assert.Empty(client.MutationOperations);
        Assert.Null(client.PerformanceOverride);
        Assert.Null(client.FanOverride);
        Assert.Null(client.LightingOverride);
        Assert.Equal("Manual overrides cleared.", viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task ClearManualOverrides_ClearsOnlyActiveSubsystems()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            PerformanceOverride = new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            FanOverride = new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.UntilPowerSourceChange),
        });
        await WaitUntilAsync(() =>
            viewModel.PerformanceOverride is not null
            && viewModel.FanOverride is not null);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ClearManualOverridesCommand)
            .ExecuteAsync(null);

        Assert.Equal(2, client.MutationCalls);
        Assert.Equal(["fans:<clear>", "performance:<clear>"], client.MutationOperations);
        Assert.Null(client.PerformanceOverride);
        Assert.Null(client.FanOverride);
        Assert.Null(client.LightingOverride);
        Assert.Equal("Manual overrides cleared.", viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task ClearManualOverrides_ReportsUnavailableRestorationForActiveLighting()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Lighting unavailable."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: false, blocked: true) with
        {
            LightingOverride = new ManualOverride<LightingSettings>(
                ProfileDefaults.CreateSafeDefaults().Battery.Lighting,
                OverrideLifetime.Persistent),
        });
        await WaitUntilAsync(() => viewModel.LightingOverride is not null);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ClearManualOverridesCommand)
            .ExecuteAsync(null);

        Assert.Equal(1, client.MutationCalls);
        Assert.Equal(["lighting:<clear>"], client.MutationOperations);
        Assert.Null(client.LightingOverride);
        Assert.Equal(
            "Manual overrides cleared; hardware restoration was not applied for lighting.",
            viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task FullChargeCommand_TogglesActiveOverrideOff()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ConfirmedState = new ConfirmedDeviceState(
                null,
                null,
                new ConfirmedBatteryState(true, 80, true),
                null),
            FullChargeOverride = true,
        });
        await WaitUntilAsync(() => viewModel.FullChargeOverride);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ToggleFullChargeCommand)
            .ExecuteAsync(viewModel.EnableFullChargeOverride);

        Assert.False(client.FullChargeEnabled);
        Assert.Equal("Cancel charge to 100%", viewModel.FullChargeActionLabel);
    }

    [Fact]
    public async Task FullChargeCommand_CannotEnableWhileOnBattery()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ActivePowerSource = PowerSource.Battery,
        });
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.False(viewModel.ToggleFullChargeCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, true)]
    [InlineData(PowerSource.UsbC, true)]
    [InlineData(PowerSource.Battery, false)]
    [InlineData(PowerSource.ExternalUnknown, false)]
    public async Task FullChargeCommand_EnablesOnlyOnConfirmedExternalPower(
        PowerSource source,
        bool expected)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            ActivePowerSource = source,
        });
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.Equal(expected, viewModel.ToggleFullChargeCommand.CanExecute(true));
    }

    [Fact]
    public async Task FullChargeCommand_DisallowsCancellationWhenHardwareWritesAreBlocked()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: true) with
        {
            ConfirmedState = new ConfirmedDeviceState(
                null,
                null,
                new ConfirmedBatteryState(true, 80, true),
                null),
            FullChargeOverride = true,
        });
        await WaitUntilAsync(() => viewModel.FullChargeOverride);

        Assert.False(viewModel.ToggleFullChargeCommand.CanExecute(null));
        await Assert.IsType<AsyncRelayCommand>(viewModel.ToggleFullChargeCommand)
            .ExecuteAsync(null);

        Assert.Null(client.FullChargeEnabled);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task Command_DoesNotRunWhenUnavailableBlockedOrUnsupported(
        bool verified,
        bool writesBlocked,
        bool capabilityIncludesPerformance)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        DeviceCapabilities capabilities = capabilityIncludesPerformance
            ? DeviceCapabilities.SafeTargetDefaults()
            : new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(), true, true);
        client.Publish(CreateStatus(
            verified,
            writesBlocked,
            capabilities: capabilities));
        await WaitUntilAsync(() => viewModel.IsConnected);

        AsyncRelayCommand command = Assert.IsType<AsyncRelayCommand>(
            viewModel.SetPerformanceCommand);
        Assert.False(command.CanExecute(PerformancePreset.Performance));
        await command.ExecuteAsync(PerformancePreset.Performance);

        Assert.Equal(0, client.MutationCalls);
        Assert.NotEqual("Applied", viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task AsyncCommand_PreventsReentrancyUntilOperationCompletes()
    {
        var client = new FakeServiceClient { BlockMutations = true };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        client.Publish(status with
        {
            ConfirmedState = status.ConfirmedState! with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Automatic,
            },
        });
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        AsyncRelayCommand command = Assert.IsType<AsyncRelayCommand>(
            viewModel.SetFanModeCommand);

        Task first = command.ExecuteAsync(FanMode.Maximum);
        await client.MutationStarted.Task;
        Task second = command.ExecuteAsync(FanMode.Maximum);

        Assert.False(command.CanExecute(FanMode.Maximum));
        Assert.Equal(1, client.MutationCalls);
        client.CompleteMutation();
        await Task.WhenAll(first, second);
        Assert.True(command.CanExecute(FanMode.Maximum));
    }

    [Fact]
    public async Task DisposeAsync_CancelsWatchAndInFlightCommand()
    {
        var client = new FakeServiceClient { BlockMutations = true };
        var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        AsyncRelayCommand command = Assert.IsType<AsyncRelayCommand>(
            viewModel.SetPerformanceCommand);
        Task running = command.ExecuteAsync(PerformancePreset.Performance);
        await client.MutationStarted.Task;

        await viewModel.DisposeAsync();
        await running;

        Assert.True(client.WatchCanceled);
        Assert.True(client.MutationCanceled);
        Assert.False(command.CanExecute(PerformancePreset.Performance));
    }

    [Fact]
    public async Task StreamCompletion_RetainsLastSnapshotButDisablesCommands()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            DesiredState = CreateStatus(true, false).DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Performance,
                    SettingValueSource.PowerProfile),
            },
        });
        await WaitUntilAsync(() => viewModel.SelectedPerformance == PerformancePreset.Performance);

        client.CompleteWatch();
        await WaitUntilAsync(() => !viewModel.IsConnected);

        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);
        Assert.Equal("Service disconnected", viewModel.ConnectionStatus);
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Balanced));
    }

    [Fact]
    public async Task InactiveSessionStatusStaysConnectedAndRestoresControl()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false) with
        {
            Revision = 10,
            ControlAccess = SessionControlAccess.InactiveSession,
        };

        client.Publish(status);
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Another user is controlling OpenBlade");

        Assert.True(viewModel.IsConnected);
        Assert.False(viewModel.CanControlHardware);

        client.Publish(status with
        {
            Revision = 11,
            ControlAccess = SessionControlAccess.Active,
            Message = "Ready",
        });
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Ready"
            && viewModel.CanControlHardware);

        Assert.True(viewModel.IsConnected);
    }

    [Fact]
    public async Task InactiveSessionObservationRefreshesAccessBeforeHardwareObservation()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot inactive = CreateStatus(verified: true, blocked: false) with
        {
            Revision = 10,
            ControlAccess = SessionControlAccess.InactiveSession,
        };
        ServiceStatusSnapshot active = inactive with
        {
            ControlAccess = SessionControlAccess.Active,
            Message = "Ready",
        };
        client.Publish(inactive);
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Another user is controlling OpenBlade");
        client.GetStatusResponse = active;

        await viewModel.ObserveStateAsync(CancellationToken.None);

        Assert.Equal(1, client.GetStatusCalls);
        Assert.Equal(0, client.ObservationCalls);
        Assert.True(viewModel.CanControlHardware);
        Assert.Equal("Ready", viewModel.ControlAccessStatus);

        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.PendingObservation.SetResult(active with { Revision = 11 });

        await viewModel.ObserveStateAsync(CancellationToken.None);

        Assert.Equal(1, client.ObservationCalls);
    }

    [Fact]
    public async Task ReconnectingUpdate_RetainsLastSnapshotAndCommandsDuringGrace()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            DesiredState = CreateStatus(true, false).DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Performance,
                    SettingValueSource.PowerProfile),
            },
        });
        await WaitUntilAsync(() =>
            viewModel.IsConnected
            && viewModel.SelectedPerformance == PerformancePreset.Performance);

        client.PublishReconnecting();
        await WaitUntilAsync(() =>
            viewModel.IsConnected
            && viewModel.ConnectionStatus == "Reconnecting to service");

        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);
        Assert.Equal("Reconnecting to service", viewModel.ConnectionStatus);
        Assert.Equal("Ready", viewModel.ProfileStateLabel);
        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Balanced));
    }

    [Fact]
    public async Task ReconnectingUpdate_DisconnectsWhenGraceExpiresWithoutRecovery()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            DesiredState = CreateStatus(true, false).DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Performance,
                    SettingValueSource.PowerProfile),
            },
        });
        await WaitUntilAsync(() =>
            viewModel.IsConnected
            && viewModel.SelectedPerformance == PerformancePreset.Performance);

        client.PublishReconnecting();
        await WaitUntilAsync(() =>
            !viewModel.IsConnected
            && viewModel.ConnectionStatus == "Reconnecting to service");

        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Balanced));
    }

    [Fact]
    public async Task ReconnectingUpdate_DoesNotDisconnectWhenServiceRecoversDuringGrace()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false) with
        {
            DesiredState = CreateStatus(true, false).DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Performance,
                    SettingValueSource.PowerProfile),
            },
        };
        client.Publish(initial);
        await WaitUntilAsync(() =>
            viewModel.IsConnected
            && viewModel.SelectedPerformance == PerformancePreset.Performance);

        client.PublishReconnecting();
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Reconnecting to service");
        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Ready after reconnect.",
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Ready after reconnect.");
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.True(viewModel.IsConnected);
        Assert.Equal("Ready", viewModel.ProfileStateLabel);
        Assert.True(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Balanced));
    }

    [Fact]
    public async Task Theme_RetainsLightDarkAndWindowsFollowingBehavior()
    {
        await using var viewModel = new MainViewModel(
            new FakeServiceClient(),
            new RecordingDispatcher());

        viewModel.Theme = ThemePreference.Dark;

        Assert.Equal(ThemePreference.Dark, viewModel.Theme);
        Assert.Contains(ThemePreference.Light, viewModel.ThemeOptions);
        Assert.Contains(ThemePreference.FollowWindows, viewModel.ThemeOptions);
        Assert.Equal("Dark", viewModel.SelectedThemeOption.Label);
        Assert.Contains(
            viewModel.ThemeSelectionOptions,
            option => option.Label == "Follow Windows");
    }

    [Fact]
    public async Task ExportDiagnosticsCommand_IsConnectedOnlyAndWorksDuringReadOnlyConflict()
    {
        var client = new FakeServiceClient();
        var saver = new FakeDiagnosticArchiveSaver
        {
            NextResult = new DiagnosticArchiveSaveResult(
                DiagnosticArchiveSaveStatus.Saved,
                @"C:\Users\Test\Desktop\chosen-diagnostics.zip"),
        };
        await using var viewModel = new MainViewModel(
            client,
            new RecordingDispatcher(),
            saver);
        AsyncRelayCommand command = Assert.IsType<AsyncRelayCommand>(
            viewModel.ExportDiagnosticsCommand);

        Assert.False(command.CanExecute(null));
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: true,
            conflicts: ["RazerAppEngine"]));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.True(viewModel.IsReadOnly);
        Assert.True(command.CanExecute(null));
        viewModel.LiveReadingsEnabled = false;
        await command.ExecuteAsync(null);

        Assert.Equal(1, client.ExportCalls);
        Assert.False(client.LastExportLiveReadingsEnabled);
        Assert.Equal(0, client.MutationCalls);
        Assert.Equal(1, saver.Calls);
        Assert.Equal(client.NextArchive, saver.Archive);
        Assert.Equal(
            @"Diagnostics exported to C:\Users\Test\Desktop\chosen-diagnostics.zip.",
            viewModel.OperationStatusMessage);

        client.PublishReconnecting();
        await WaitUntilAsync(() => !viewModel.IsConnected);
        Assert.False(command.CanExecute(null));
    }

    [Fact]
    public async Task ExportDiagnosticsCommand_ReportsCancellationWithoutFailure()
    {
        var client = new FakeServiceClient();
        var saver = new FakeDiagnosticArchiveSaver
        {
            NextResult = new DiagnosticArchiveSaveResult(
                DiagnosticArchiveSaveStatus.Canceled,
                null),
        };
        await using var viewModel = new MainViewModel(
            client,
            new RecordingDispatcher(),
            saver);
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: false, blocked: true));
        await WaitUntilAsync(() => viewModel.IsConnected);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ExportDiagnosticsCommand)
            .ExecuteAsync(null);

        Assert.Equal("Diagnostic export canceled.", viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task ExportDiagnosticsCommand_ReportsSaveFailure()
    {
        var client = new FakeServiceClient();
        var saver = new FakeDiagnosticArchiveSaver
        {
            Failure = new IOException("disk full"),
        };
        await using var viewModel = new MainViewModel(
            client,
            new RecordingDispatcher(),
            saver);
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: false, blocked: true));
        await WaitUntilAsync(() => viewModel.IsConnected);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ExportDiagnosticsCommand)
            .ExecuteAsync(null);

        Assert.Equal(
            "Diagnostic export failed: disk full",
            viewModel.OperationStatusMessage);
        Assert.True(viewModel.HasTroubleshootingRecentError);
        Assert.Equal(
            "Diagnostic export failed: disk full",
            viewModel.TroubleshootingRecentErrorStatus);
    }

    [Fact]
    public async Task ExportDiagnosticsCommand_PreventsReentrancy()
    {
        var client = new FakeServiceClient { BlockExports = true };
        var saver = new FakeDiagnosticArchiveSaver();
        await using var viewModel = new MainViewModel(
            client,
            new RecordingDispatcher(),
            saver);
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: false, blocked: true));
        await WaitUntilAsync(() => viewModel.IsConnected);
        AsyncRelayCommand command = Assert.IsType<AsyncRelayCommand>(
            viewModel.ExportDiagnosticsCommand);

        Task first = command.ExecuteAsync(null);
        await client.ExportStarted.Task;
        Task second = command.ExecuteAsync(null);

        Assert.False(command.CanExecute(null));
        Assert.Equal(1, client.ExportCalls);
        client.CompleteExport();
        await Task.WhenAll(first, second);

        Assert.Equal(1, saver.Calls);
        Assert.True(command.CanExecute(null));
    }

    private static ServiceStatusSnapshot CreateStatus(
        bool verified,
        bool blocked,
        IReadOnlyList<string>? conflicts = null,
        string message = "Ready",
        DeviceCapabilities? capabilities = null)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            1,
            verified,
            blocked,
            conflicts ?? [],
            null,
            [],
            message,
            capabilities ?? (verified
                ? DeviceCapabilities.SafeTargetDefaults() with
                {
                    ChargeLimits = ChargeLimits(),
                    SupportsBatteryProtectionOff = true,
                    SupportsTemporaryFullCharge = true,
                }
                : DeviceCapabilities.Unavailable()),
            verified
                ? new ConfirmedDeviceState(
                    null,
                    null,
                    new ConfirmedBatteryState(true, 80, false),
                    null)
                : null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    profiles.PluggedIn.Performance,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    profiles.PluggedIn.Fans,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    profiles.PluggedIn.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn,
            null,
            null,
            null,
            null,
            false,
            Firmware: null,
            ServiceVersion: "0.1.0-test");
    }

    private static (
        ServiceStatusSnapshot Status,
        GpuClockOffsetSettings LegacyOffsets) CreateLegacyGpuTuningStatus(string message)
    {
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        var legacyOffsets = new GpuClockOffsetSettings(100, 200);
        return (
            baseline with
            {
                Message = message,
                Capabilities = baseline.Capabilities! with
                {
                    GpuOverclock = new GpuOverclockCapabilities(
                        new DeviceNumericRange(-1000, 400, 10),
                        new DeviceNumericRange(-1000, 3000, 10),
                        DeviceFeatureAccess.ReadWrite),
                },
                Profiles = baseline.Profiles with
                {
                    PluggedIn = baseline.Profiles.PluggedIn with
                    {
                        CustomPerformance = CustomPerformanceSettings.Default with
                        {
                            GpuClockOffsets = legacyOffsets,
                        },
                    },
                },
            },
            legacyOffsets);
    }

    private static HashSet<int> ChargeLimits() => [50, 55, 60, 65, 70, 75, 80];

    private sealed class ConcurrentPropertySet : IReadOnlyCollection<string>
    {
        private readonly ConcurrentDictionary<string, byte> values = new();

        public int Count => values.Count;

        public bool Add(string value) => values.TryAdd(value, 0);

        public bool Contains(string value) => values.ContainsKey(value);

        public void Clear() => values.Clear();

        public IEnumerator<string> GetEnumerator() => values.Keys.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        private int completedCalls;

        public int Calls { get; private set; }

        public int CompletedCalls => Volatile.Read(ref completedCalls);

        public Task InvokeAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            action();
            Interlocked.Increment(ref completedCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class StubFirmwareUpdateClient : IRazerFirmwareUpdateClient
    {
        public Task<RazerPublishedFirmwareVersions> GetPublishedVersionsAsync(
            RazerFirmwareSupportPage supportPage,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RazerPublishedFirmwareVersions(null, null, null));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class FakeServiceClient : OpenBlade.Testing.UnavailableServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> statuses = Channel.CreateUnbounded<ServiceWatchUpdate>();
        private readonly TaskCompletionSource mutationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource exportCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WatchCalls { get; private set; }
        public int MutationCalls { get; private set; }
        public List<string> MutationOperations { get; } = [];
        public int ExportCalls { get; private set; }
        public bool? LastExportLiveReadingsEnabled { get; private set; }
        public int ObservationCalls { get; private set; }
        public int GetStatusCalls { get; private set; }
        public List<bool> IncludeLiveReadingsRequests { get; } = [];
        public List<LiveReadingSensors> RequestedLiveReadings { get; } = [];
        public TaskCompletionSource<ServiceStatusSnapshot>? PendingObservation { get; set; }
        public TaskCompletionSource ObservationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockMutations { get; init; }
        public bool BlockExports { get; init; }
        public bool WatchCanceled { get; private set; }
        public bool MutationCanceled { get; private set; }
        public PerformanceOverride? PerformanceOverride { get; private set; }
        public ManualOverride<FanMode>? FanOverride { get; private set; }
        public FanControlSettings? LastFanControlSettings { get; private set; }
        public ManualOverride<LightingSettings>? LightingOverride { get; private set; }
        public bool? ExternalLightingOverridePolicy { get; private set; }
        public bool? FirmwareCompatibilityOverride { get; private set; }
        public int ReapplyChangedSettingsCalls { get; private set; }
        public bool? BatteryProtectionEnabled { get; private set; }
        public BatteryPolicy? BatteryPolicy { get; private set; }
        public bool? FullChargeEnabled { get; private set; }
        public bool ShutdownRequested { get; private set; }
        public ProfileSet? LastProfiles { get; private set; }
        public TaskCompletionSource MutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ControlOperationResult NextResult { get; set; } = new(ControlOperationStatus.Applied, "Applied");
        public Queue<ControlOperationResult> MutationResults { get; } = [];
        public ServiceStatusSnapshot? CurrentStatus { get; private set; }
        public ServiceStatusSnapshot? GetStatusResponse { get; set; }
        public DiagnosticArchive NextArchive { get; init; } = new(
            "OpenBlade-diagnostics.zip",
            [0x50, 0x4B, 0x03, 0x04]);
        public void Publish(ServiceStatusSnapshot status)
        {
            CurrentStatus = status;
            statuses.Writer.TryWrite(new ServiceWatchUpdate.Connected(status));
        }
        public void PublishReconnecting() => statuses.Writer.TryWrite(new ServiceWatchUpdate.Reconnecting());
        public void CompleteWatch() => statuses.Writer.TryComplete();
        public void CompleteMutation() => mutationCompletion.TrySetResult();
        public void CompleteExport() => exportCompletion.TrySetResult();

        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            WatchCalls++;
            try
            {
                await foreach (ServiceWatchUpdate status in statuses.Reader.ReadAllAsync(token))
                {
                    yield return status;
                }
            }
            finally
            {
                WatchCanceled = token.IsCancellationRequested;
            }
        }

        public override Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            GetStatusCalls++;
            return Task.FromResult(GetStatusResponse ?? CurrentStatus ?? throw new InvalidOperationException(
                "No status has been published."));
        }

        public override Task<ServiceStatusSnapshot> ObserveStateAsync(
            bool includeLiveReadings,
            CancellationToken cancellationToken)
        {
            return ObserveStateAsync(
                includeLiveReadings ? LiveReadingSensors.All : LiveReadingSensors.None,
                cancellationToken);
        }

        public override Task<ServiceStatusSnapshot> ObserveStateAsync(
            LiveReadingSensors requestedLiveReadings,
            CancellationToken cancellationToken)
        {
            ObservationCalls++;
            RequestedLiveReadings.Add(requestedLiveReadings);
            IncludeLiveReadingsRequests.Add(requestedLiveReadings != LiveReadingSensors.None);
            ObservationStarted.TrySetResult();
            return PendingObservation is not null
                ? PendingObservation.Task.WaitAsync(cancellationToken)
                : throw new NotSupportedException();
        }

        public override async Task<DiagnosticArchive> ExportDiagnosticsAsync(
            bool liveReadingsEnabled,
            CancellationToken token)
        {
            ExportCalls++;
            LastExportLiveReadingsEnabled = liveReadingsEnabled;
            ExportStarted.TrySetResult();
            if (BlockExports)
            {
                await exportCompletion.Task.WaitAsync(token);
            }
            return NextArchive;
        }

        public override Task<ControlOperationResult> UpdateProfilesAsync(
            ProfileSet profiles,
            CancellationToken token)
        {
            LastProfiles = profiles;
            if (CurrentStatus is not null)
            {
                PowerProfile active = CurrentStatus.ActivePowerSource switch
                {
                    PowerSource.Battery => profiles.Battery,
                    PowerSource.UsbC => profiles.UsbC,
                    _ => profiles.PluggedIn,
                };
                CurrentStatus = CurrentStatus with
                {
                    Profiles = profiles,
                    BatteryPolicyOverride = CurrentStatus.BatteryPolicyOverride
                        == profiles.BatteryPolicy
                            ? null
                            : CurrentStatus.BatteryPolicyOverride,
                    BatteryProtectionOverride = CurrentStatus.BatteryProtectionOverride
                        == active.EffectiveBatteryProtectionEnabled
                            ? null
                            : CurrentStatus.BatteryProtectionOverride,
                };
            }
            return MutateAsync(token);
        }

        public override Task<ControlOperationResult> SetPerformanceOverrideAsync(
            ManualOverride<PerformancePreset>? value,
            CancellationToken token)
            => SetPerformanceConfigurationOverrideAsync(value, token);

        public override Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
            PerformanceOverride? value,
            CancellationToken token)
        {
            PerformanceOverride = value;
            if (CurrentStatus is not null)
            {
                CurrentStatus = CurrentStatus with { PerformanceOverride = value };
            }
            MutationOperations.Add(value is null
                ? "performance:<clear>"
                : $"performance:{value.Value}");
            return MutateAsync(token);
        }

        public override Task<ControlOperationResult> SetFanOverrideAsync(
            ManualOverride<FanMode>? value,
            CancellationToken token)
        {
            FanOverride = value;
            if (CurrentStatus is not null)
            {
                CurrentStatus = CurrentStatus with { FanOverride = value };
            }
            MutationOperations.Add(value is null
                ? "fans:<clear>"
                : $"fans:{value.Value}");
            return MutateAsync(token);
        }

        public override Task<ControlOperationResult> SetFanControlOverrideAsync(
            ManualOverride<FanMode>? value,
            FanControlSettings? settings,
            CancellationToken token)
        {
            LastFanControlSettings = settings;
            return SetFanOverrideAsync(value, token);
        }

        public override Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken token)
        {
            LightingOverride = value;
            if (CurrentStatus is not null)
            {
                CurrentStatus = CurrentStatus with { LightingOverride = value };
            }
            MutationOperations.Add(value is null
                ? "lighting:<clear>"
                : $"lighting:{value.Value.Effect}");
            return MutateAsync(token);
        }

        public override Task<ControlOperationResult> SetExternalLightingOverridePolicyAsync(
            bool enabled,
            CancellationToken token)
        {
            ExternalLightingOverridePolicy = enabled;
            return MutateAsync(token);
        }

        public override Task<ControlOperationResult> SetFirmwareCompatibilityOverrideAsync(
            bool enabled,
            CancellationToken token)
        {
            FirmwareCompatibilityOverride = enabled;
            return MutateAsync(token);
        }

        public override Task<ControlOperationResult> ReapplyChangedSettingsAsync(
            CancellationToken token)
        {
            ReapplyChangedSettingsCalls++;
            return MutateAsync(token);
        }

        public override Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken token)
        {
            BatteryPolicy = value;
            if (CurrentStatus is not null)
            {
                CurrentStatus = CurrentStatus with
                {
                    BatteryPolicyOverride = value == CurrentStatus.Profiles.BatteryPolicy
                        ? null
                        : value,
                };
            }
            return MutateAsync(token);
        }

        public override Task<ControlOperationResult> SetBatteryProtectionAsync(
            bool enabled,
            CancellationToken token)
        {
            BatteryProtectionEnabled = enabled;
            if (CurrentStatus is not null)
            {
                PowerProfile active = CurrentStatus.ActivePowerSource switch
                {
                    PowerSource.Battery => CurrentStatus.Profiles.Battery,
                    PowerSource.UsbC => CurrentStatus.Profiles.UsbC,
                    _ => CurrentStatus.Profiles.PluggedIn,
                };
                CurrentStatus = CurrentStatus with
                {
                    BatteryProtectionOverride =
                        enabled == active.EffectiveBatteryProtectionEnabled
                            ? null
                            : enabled,
                };
            }
            return MutateAsync(token);
        }

        public override Task<ControlOperationResult> SetFullChargeOverrideAsync(
            bool enabled,
            CancellationToken token)
        {
            FullChargeEnabled = enabled;
            return MutateAsync(token);
        }

        public ApplyOneShotLaptopControl? OneShotLaptopControl { get; private set; }

        public override Task<ControlOperationResult> ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken token)
        {
            OneShotLaptopControl = value;
            return MutateAsync(token);
        }

        public override Task<ControlOperationResult> ShutdownOpenBladeAsync(
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ShutdownRequested = true;
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Stored,
                "OpenBlade is shutting down."));
        }

        private async Task<ControlOperationResult> MutateAsync(CancellationToken token)
        {
            MutationCalls++;
            MutationStarted.TrySetResult();
            if (BlockMutations)
            {
                try
                {
                    await mutationCompletion.Task.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    MutationCanceled = true;
                    throw;
                }
            }
            return MutationResults.Count > 0 ? MutationResults.Dequeue() : NextResult;
        }
    }

    private sealed class FakeDiagnosticArchiveSaver : IDiagnosticArchiveSaver
    {
        public int Calls { get; private set; }
        public DiagnosticArchive? Archive { get; private set; }
        public Exception? Failure { get; init; }
        public DiagnosticArchiveSaveResult NextResult { get; init; } = new(
            DiagnosticArchiveSaveStatus.Saved,
            @"C:\Users\Test\Desktop\OpenBlade-diagnostics.zip");

        public Task<DiagnosticArchiveSaveResult> SaveAsync(
            DiagnosticArchive archive,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            Archive = archive;
            if (Failure is not null)
            {
                throw Failure;
            }
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeStartupRegistration(bool enabled) : IStartupRegistration
    {
        public bool Enabled { get; private set; } = enabled;
        public Exception? Failure { get; init; }
        public List<bool> Requests { get; } = [];

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Enabled);
        }

        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(enabled);
            if (Failure is not null)
            {
                throw Failure;
            }

            Enabled = enabled;
            return Task.CompletedTask;
        }
    }
}
