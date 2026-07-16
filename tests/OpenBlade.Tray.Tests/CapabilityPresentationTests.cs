using System.Threading.Channels;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CapabilityPresentationTests
{
    [Fact]
    public void MainWindow_BindsEveryFunctionalSurfaceAndContainsCleanVisibleText()
    {
        string xaml = ReadMainWindowXaml();

        string[] required =
        [
            "Header=\"Dashboard\"",
            "Header=\"Lighting\"",
            "Header=\"Settings\"",
            "ViewModel.SetPerformanceCommand",
            "ViewModel.SetFanModeCommand",
            "ViewModel.SetChargeLimitCommand",
            "ViewModel.ToggleFullChargeCommand",
            "ViewModel.SetLightingCommand",
            "ViewModel.SaveProfilesCommand",
            "ViewModel.ExportDiagnosticsCommand",
            "ViewModel.HasControllerAdvisory",
            "ViewModel.ControllerAdvisoryText",
            "ViewModel.HasControllerDetectionWarning",
            "ViewModel.ControllerDetectionWarningText",
            "ViewModel.HasExternalChanges",
            "ViewModel.ExternalChangesText",
            "ViewModel.AvailablePerformancePresets",
            "ViewModel.AvailableFanModes",
            "ViewModel.AvailableLightingEffects",
            "ViewModel.AvailableLightingSettings",
            "ViewModel.PluggedInPerformanceEditor",
            "ViewModel.PluggedInFanModeEditor",
            "ViewModel.PluggedInLightingEditor",
            "ViewModel.BatteryPerformanceEditor",
            "ViewModel.BatteryFanModeEditor",
            "ViewModel.BatteryLightingEditor",
            "ViewModel.ProfileChargeLimitEditor",
            "ViewModel.ThemeOptions",
            "ViewModel.Theme",
            "ViewModel.ActivePowerSourceLabel",
            "ViewModel.PerformanceSourceLabel",
            "ViewModel.FanSourceLabel",
            "ViewModel.LightingSourceLabel",
            "ViewModel.BatterySourceLabel",
            "ViewModel.PowerAndBatteryTelemetryLabel",
            "ViewModel.CpuTelemetryLabel",
            "ViewModel.GpuTelemetryLabel",
            "ViewModel.FanTelemetryLabel",
            "ViewModel.ControlAccessStatus",
            "ViewModel.FullChargeActionLabel",
            "ViewModel.FullChargeOverride",
            "Hardware controls are fail-closed",
            "Razer Blade 16 · RZ09-0581",
            "No verified lighting controls are available for this device.",
        ];
        foreach (string fragment in required)
        {
            Assert.Contains(fragment, xaml, StringComparison.Ordinal);
        }

        Assert.Contains(
            "new MicaController",
            ReadTraySource("MicaBackdropHost.cs"),
            StringComparison.Ordinal);
        Assert.Contains("<Grid.Resources>", xaml, StringComparison.Ordinal);
        Assert.Contains("DashboardFanModeSelector.SelectedItem", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CommandParameter=\"{x:Bind ViewModel.SelectedFanMode",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("BooleanNegationToVisibility", xaml, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(
                xaml,
                "Text=\"{x:Bind ViewModel.OperationStatusMessage, Mode=OneWay}\"") >= 2,
            "Operation status must be visible from both Dashboard and Settings.");
        Assert.Equal(3, CountOccurrences(xaml, "<TabViewItem"));
        Assert.DoesNotContain("TabControl", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Static", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DynamicResource", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Window.Resources>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnabled=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Export diagnostics (unavailable)", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.ConflictNames", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemFillColorCriticalBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirmed source:", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{Binding ChargeLimitPercent, Mode=TwoWay, Converter={StaticResource Int32Double}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"{x:Bind ViewModel.ChargeLimitPercent, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(xaml, "ViewModel.ProfileChargeLimitEditor"));
        Assert.DoesNotContain("Open" + "Synapse", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\u00c2", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationLoadsNativeControlResources()
    {
        string xaml = ReadTraySource("App.xaml");

        Assert.Contains("XamlControlsResources", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DynamicResource", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void NumericEditorConverterDelegatesToTheTestedCorePolicy()
    {
        string source = ReadTraySource("Int32DoubleConverter.cs");

        Assert.Contains(": IValueConverter", source, StringComparison.Ordinal);
        Assert.Contains("IntegerEditorValue.ToDouble", source, StringComparison.Ordinal);
        Assert.Contains("IntegerEditorValue.ToInt32", source, StringComparison.Ordinal);
        Assert.Contains("throw new ArgumentException", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowExposesTypedPerformanceCommandParameters()
    {
        string source = ReadTraySource("MainWindow.xaml.cs");

        Assert.Contains("PerformancePreset SilentPreset", source, StringComparison.Ordinal);
        Assert.Contains("PerformancePreset BalancedPreset", source, StringComparison.Ordinal);
        Assert.Contains("PerformancePreset PerformancePreset", source, StringComparison.Ordinal);
        Assert.Contains("PerformancePreset.Silent", source, StringComparison.Ordinal);
        Assert.Contains("PerformancePreset.Balanced", source, StringComparison.Ordinal);
        Assert.Contains("PerformancePreset.Performance", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedCapabilities_OmitEveryLightingChoice()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateStatus(
            verified: false,
            blocked: true,
            capabilities: DeviceCapabilities.Unavailable()));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.Empty(viewModel.AvailableLightingEffects);
        Assert.Empty(viewModel.AvailableLightingSettings);
        Assert.False(viewModel.IsLightingVisible);
        Assert.True(viewModel.EnableFullChargeOverride);
    }

    [Fact]
    public async Task UnsupportedPerformanceAndFanValuesAreDisabledInSharedTrayMenu()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() =>
            viewModel.CanControlHardware && viewModel.AvailableLightingSettings.Count > 0);
        var controller = new TrayMenuController(viewModel, () => { }, () => { });

        Assert.False(controller.Find("Silent").IsEnabled);
        Assert.True(controller.Find("Balanced").IsEnabled);
        Assert.False(controller.Find("Performance").IsEnabled);
        Assert.True(controller.Find("Automatic").IsEnabled);
        Assert.False(controller.Find("Maximum").IsEnabled);
    }

    [Fact]
    public async Task ControllerAdvisoryDoesNotDisableAdmittedMutationOrOpenAndExit()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            conflicts: ["RazerAppEngine"],
            capabilities: DeviceCapabilities.SafeTargetDefaults()));
        await WaitUntilAsync(() => viewModel.IsConnected);
        var controller = new TrayMenuController(viewModel, () => { }, () => { });

        Assert.True(controller.Find("Automatic").IsEnabled);
        Assert.True(controller.Find("Open").IsEnabled);
        Assert.True(controller.Find("Exit").IsEnabled);
        Assert.True(viewModel.HasControllerAdvisory);
        Assert.Contains("RazerAppEngine", viewModel.ControllerAdvisoryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileEditorsBuildAndSaveBothPowerProfiles()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = ChargeLimits(),
            }));
        await WaitUntilAsync(() =>
            viewModel.CanControlHardware && viewModel.AvailableLightingSettings.Count > 0);
        viewModel.PluggedInPerformanceEditor = PerformancePreset.Performance;
        viewModel.PluggedInFanModeEditor = FanMode.Maximum;
        viewModel.BatteryPerformanceEditor = PerformancePreset.Silent;
        viewModel.BatteryFanModeEditor = FanMode.Automatic;
        viewModel.ProfileChargeLimitEditor = 75;

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand)
            .ExecuteAsync(null);

        Assert.NotNull(client.SavedProfiles);
        Assert.Equal(PerformancePreset.Performance, client.SavedProfiles.PluggedIn.Performance);
        Assert.Equal(FanMode.Maximum, client.SavedProfiles.PluggedIn.Fans);
        Assert.Equal(PerformancePreset.Silent, client.SavedProfiles.Battery.Performance);
        Assert.Equal(75, client.SavedProfiles.BatteryPolicy.ChargeLimitPercent);
    }

    [Fact]
    public async Task SaveProfiles_IsDisabledWhenEitherDraftUsesUnsupportedCapabilities()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced, PerformancePreset.Silent },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() =>
            viewModel.CanControlHardware && viewModel.AvailableLightingSettings.Count > 0);
        viewModel.PluggedInPerformanceEditor = PerformancePreset.Performance;
        AsyncRelayCommand save = Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand);

        Assert.False(save.CanExecute(null));
        await save.ExecuteAsync(null);

        Assert.Null(client.SavedProfiles);
    }

    [Fact]
    public async Task UndefinedPerformanceAndFanCapabilitiesAreNotExposedOrExecutable()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        var invalidPerformance = (PerformancePreset)999;
        var invalidFan = (FanMode)999;
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced, invalidPerformance },
                new HashSet<FanMode> { FanMode.Automatic, invalidFan },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() => viewModel.AvailableFanModes.Count > 0);

        Assert.Equal([PerformancePreset.Balanced], viewModel.AvailablePerformancePresets);
        Assert.Equal([FanMode.Automatic], viewModel.AvailableFanModes);
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(invalidPerformance));
        Assert.False(viewModel.SetFanModeCommand.CanExecute(invalidFan));
    }

    [Fact]
    public async Task UndefinedLightingCapabilityIsNotExposedOrExecutable()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        var invalidEffect = (LightingEffect)999;
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off, invalidEffect },
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() => viewModel.IsLightingVisible);

        Assert.Equal([LightingEffect.Off], viewModel.AvailableLightingEffects);
        Assert.All(
            viewModel.AvailableLightingSettings,
            setting => Assert.Equal(LightingEffect.Off, setting.Effect));
        Assert.False(viewModel.SetLightingCommand.CanExecute(
            new LightingSettings(invalidEffect, default, 0)));
    }

    [Fact]
    public async Task MissingBatteryCapabilityDisablesOnlyBatteryCommands()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                new HashSet<int>(), false, false)));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
        Assert.False(viewModel.SetChargeLimitCommand.CanExecute(75));
        Assert.False(viewModel.ToggleFullChargeCommand.CanExecute(true));
    }

    [Fact]
    public async Task InvalidBatteryCapabilitySetDisablesFullChargeOnly()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                new HashSet<int> { 50, 79 }, true, true)));
        await WaitUntilAsync(() => viewModel.Capabilities.ChargeLimits.Contains(79));

        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
        Assert.False(viewModel.ToggleFullChargeCommand.CanExecute(true));
    }

    [Fact]
    public async Task DashboardChargeLimitDoesNotMutateSettingsDraftAndSendsDashboardValue()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        viewModel.ProfileChargeLimitEditor = 70;
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = ChargeLimits(),
                SupportsTemporaryFullCharge = true,
            }));
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.ChargeLimitPercent = 75;
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetChargeLimitCommand)
            .ExecuteAsync(viewModel.ChargeLimitPercent);

        Assert.Equal(70, viewModel.ProfileChargeLimitEditor);
        Assert.Equal(75, client.SavedBatteryPolicy?.ChargeLimitPercent);
    }

    [Fact]
    public async Task LightingEditorBuildsTypedSettingsOnlyForAdmittedEffects()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Static },
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        viewModel.LightingEffectEditor = LightingEffect.Static;
        viewModel.LightingBrightnessEditor = 64;
        viewModel.LightingRedEditor = 12;
        viewModel.LightingGreenEditor = 34;
        viewModel.LightingBlueEditor = 56;

        Assert.Equal(
            new LightingSettings(LightingEffect.Static, new RgbColor(12, 34, 56), 64),
            viewModel.LightingDraft);
        Assert.True(viewModel.SetLightingCommand.CanExecute(viewModel.LightingDraft));

        viewModel.LightingEffectEditor = LightingEffect.Spectrum;
        Assert.False(viewModel.SetLightingCommand.CanExecute(viewModel.LightingDraft));
    }

    private static string ReadMainWindowXaml() => ReadTraySource("MainWindow.xaml");

    private static string ReadTraySource(
        string fileName,
        [CallerFilePath] string testPath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testPath)!,
            "..",
            "..",
            "src",
            "OpenBlade.Tray",
            fileName)));

    private static int CountOccurrences(string value, string fragment) =>
        (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length)
        / fragment.Length;

    private static ServiceStatusSnapshot CreateStatus(
        bool verified,
        bool blocked,
        DeviceCapabilities capabilities,
        IReadOnlyList<string>? conflicts = null)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            1, verified, blocked, conflicts ?? [], null, [], blocked ? "Blocked" : "Ready",
            capabilities, null, profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(profiles.PluggedIn.Performance, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(profiles.PluggedIn.Fans, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            new TelemetrySnapshot(DateTimeOffset.UnixEpoch, PowerSource.PluggedIn, 80, null, null, null),
            null, null, null, false);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int index = 0; index < 2_000 && !predicate(); index++)
        {
            await Task.Delay(1);
        }
        Assert.True(predicate());
    }

    private static HashSet<int> ChargeLimits() => [50, 55, 60, 65, 70, 75, 80];

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class PresentationClient : IServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> statuses = Channel.CreateUnbounded<ServiceWatchUpdate>();
        public ProfileSet? SavedProfiles { get; private set; }
        public BatteryPolicy? SavedBatteryPolicy { get; private set; }
        public void Publish(ServiceStatusSnapshot status) =>
            statuses.Writer.TryWrite(new ServiceWatchUpdate.Connected(status));
        public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (ServiceWatchUpdate status in statuses.Reader.ReadAllAsync(token)) yield return status;
        }
        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token)
        {
            SavedProfiles = profiles;
            return Result();
        }
        public Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ServiceStatusSnapshot> ObserveStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DiagnosticArchive> ExportDiagnosticsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token)
        {
            SavedBatteryPolicy = value;
            return Result();
        }
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) => Result();
        private static Task<ControlOperationResult> Result() => Task.FromResult(
            new ControlOperationResult(ControlOperationStatus.Applied, "Applied"));
    }
}
