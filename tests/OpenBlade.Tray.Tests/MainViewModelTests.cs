using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class MainViewModelTests
{
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
            "Another controller may change laptop settings: RazerAppEngine, Razer Synapse Service. Automatic profile changes pause for settings changed elsewhere; manual actions still apply.",
            viewModel.ControllerAdvisoryText);
        Assert.True(viewModel.HasControllerDetectionWarning);
        Assert.Equal("Controller detection was incomplete.", viewModel.ControllerDetectionWarningText);
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
        Assert.Equal("Confirmed device state", viewModel.PerformanceSourceLabel);
        Assert.Equal("Changed outside OpenBlade", viewModel.FanSourceLabel);
        Assert.Equal("Changed outside OpenBlade", viewModel.LightingSourceLabel);
        Assert.Equal(
            "Confirmed device state · Charge limit 65%",
            viewModel.BatterySourceLabel);
        Assert.True(viewModel.HasExternalChanges);
        Assert.Equal(
            "Settings changed outside OpenBlade: Fans, Lighting. Automatic profile changes for these controls are paused; manual actions still apply.",
            viewModel.ExternalChangesText);
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
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(),
                SupportsBatteryProtectionOff: true,
                SupportsTemporaryFullCharge: true)));
        await WaitUntilAsync(() => viewModel.IsConnected);

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
    public async Task Status_RendersUnavailableSensorsAndActiveOverrideSources()
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
            Telemetry = new TelemetrySnapshot(
                DateTimeOffset.UnixEpoch,
                PowerSource.Battery,
                61,
                null,
                null,
                null),
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
            && viewModel.TelemetryStatus.Contains("CPU unavailable", StringComparison.Ordinal));

        Assert.Contains("CPU unavailable", viewModel.TelemetryStatus, StringComparison.Ordinal);
        Assert.Contains("GPU unavailable", viewModel.TelemetryStatus, StringComparison.Ordinal);
        Assert.Contains("Fans unavailable", viewModel.TelemetryStatus, StringComparison.Ordinal);
        Assert.Contains("Battery 61%", viewModel.TelemetryStatus, StringComparison.Ordinal);
        Assert.Equal("Battery profile", viewModel.ActivePowerSourceLabel);
        Assert.Equal("Desired intent (not confirmed)", viewModel.PerformanceSourceLabel);
        Assert.Equal("Desired intent (not confirmed)", viewModel.FanSourceLabel);
        Assert.Equal("Desired intent (not confirmed)", viewModel.LightingSourceLabel);
        Assert.Equal(status.Capabilities, viewModel.Capabilities);
        Assert.Equal(status.Telemetry, viewModel.Telemetry);
        Assert.Equal(status.PerformanceOverride, viewModel.PerformanceOverride);
        Assert.Equal(status.FanOverride, viewModel.FanOverride);
        Assert.Equal(status.LightingOverride, viewModel.LightingOverride);
        Assert.Contains(
            "Performance: Performance (until cleared)",
            viewModel.ManualOverrideStatus,
            StringComparison.Ordinal);
        Assert.Contains(
            "Fans: Maximum (until power source changes)",
            viewModel.ManualOverrideStatus,
            StringComparison.Ordinal);
        Assert.True(viewModel.FullChargeOverride);
    }

    [Fact]
    public async Task Status_FormatsSplitTelemetryAndNotifiesEveryTelemetryPresentationProperty()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());

        Assert.Equal("Unavailable", viewModel.PowerAndBatteryTelemetryLabel);
        Assert.Equal("Unavailable", viewModel.CpuTelemetryLabel);
        Assert.Equal("Unavailable", viewModel.GpuTelemetryLabel);
        Assert.Equal("Unavailable", viewModel.FanTelemetryLabel);

        var changed = new HashSet<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            Telemetry = new TelemetrySnapshot(
                DateTimeOffset.UnixEpoch,
                PowerSource.Battery,
                61,
                null,
                71.25,
                3210),
        });
        await WaitUntilAsync(() => viewModel.FanTelemetryLabel == "3210 RPM");

        Assert.Equal("Battery · 61%", viewModel.PowerAndBatteryTelemetryLabel);
        Assert.Equal("Unavailable", viewModel.CpuTelemetryLabel);
        Assert.Equal("71.3 °C", viewModel.GpuTelemetryLabel);
        Assert.Equal("3210 RPM", viewModel.FanTelemetryLabel);
        Assert.Contains(nameof(viewModel.PowerAndBatteryTelemetryLabel), changed);
        Assert.Contains(nameof(viewModel.CpuTelemetryLabel), changed);
        Assert.Contains(nameof(viewModel.GpuTelemetryLabel), changed);
        Assert.Contains(nameof(viewModel.FanTelemetryLabel), changed);

        changed.Clear();
        client.Publish(CreateStatus(verified: true, blocked: false) with
        {
            Revision = 2,
            Telemetry = new TelemetrySnapshot(
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                PowerSource.PluggedIn,
                null,
                65,
                null,
                null),
        });
        await WaitUntilAsync(() => viewModel.CpuTelemetryLabel == "65 °C");

        Assert.Equal("Plugged in · Unavailable", viewModel.PowerAndBatteryTelemetryLabel);
        Assert.Equal("65 °C", viewModel.CpuTelemetryLabel);
        Assert.Equal("Unavailable", viewModel.GpuTelemetryLabel);
        Assert.Equal("Unavailable", viewModel.FanTelemetryLabel);
        Assert.Contains(nameof(viewModel.PowerAndBatteryTelemetryLabel), changed);
        Assert.Contains(nameof(viewModel.CpuTelemetryLabel), changed);
        Assert.Contains(nameof(viewModel.GpuTelemetryLabel), changed);
        Assert.Contains(nameof(viewModel.FanTelemetryLabel), changed);
    }

    [Fact]
    public async Task ControlAccessStatus_DistinguishesEveryFailClosedState()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        Assert.Equal("Service disconnected · hardware controls disabled", viewModel.ControlAccessStatus);

        client.Publish(CreateStatus(verified: false, blocked: true));
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Target not verified · hardware controls disabled");

        client.Publish(CreateStatus(
            verified: true,
            blocked: true,
            conflicts: ["RazerAppEngine"]));
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Read-only · hardware writes blocked");

        client.Publish(CreateStatus(verified: true, blocked: true));
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Read-only · hardware writes blocked");

        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Target verified · hardware controls available");

        client.PublishReconnecting();
        await WaitUntilAsync(() =>
            viewModel.ControlAccessStatus == "Service disconnected · hardware controls disabled");
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
        Assert.Equal(PerformancePreset.Performance, client.PerformanceOverride!.Value);
        Assert.Equal(OverrideLifetime.UntilPowerSourceChange, client.PerformanceOverride.Lifetime);
        Assert.Equal("Intent stored without hardware application.", viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task Command_UsesSelectedPersistentOverrideLifetime()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher())
        {
            KeepManualChangesUntilCleared = true,
        };
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);

        Assert.Equal(OverrideLifetime.Persistent, client.PerformanceOverride!.Lifetime);
    }

    [Fact]
    public async Task ClearManualOverrides_ClearsEverySubsystemWhileHardwareIsUnavailable()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: false, blocked: true));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.True(viewModel.ClearManualOverridesCommand.CanExecute(null));
        await Assert.IsType<AsyncRelayCommand>(viewModel.ClearManualOverridesCommand)
            .ExecuteAsync(null);

        Assert.Equal(3, client.MutationCalls);
        Assert.Null(client.PerformanceOverride);
        Assert.Null(client.FanOverride);
        Assert.Null(client.LightingOverride);
        Assert.Equal("Manual overrides cleared.", viewModel.OperationStatusMessage);
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
            Telemetry = CreateStatus(true, false).Telemetry with
            {
                PowerSource = PowerSource.Battery,
            },
        });
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.False(viewModel.ToggleFullChargeCommand.CanExecute(null));
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
        client.Publish(CreateStatus(verified: true, blocked: false));
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
    public async Task ReconnectingUpdate_RetainsLastSnapshotButImmediatelyDisablesCommands()
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

        client.PublishReconnecting();
        await WaitUntilAsync(() => !viewModel.IsConnected);

        Assert.Equal(PerformancePreset.Performance, viewModel.SelectedPerformance);
        Assert.Equal("Reconnecting to service", viewModel.ConnectionStatus);
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(PerformancePreset.Balanced));
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
        await command.ExecuteAsync(null);

        Assert.Equal(1, client.ExportCalls);
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
            new TelemetrySnapshot(
                DateTimeOffset.UnixEpoch,
                PowerSource.PluggedIn,
                80,
                null,
                null,
                null),
            null,
            null,
            null,
            false);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int index = 0; index < 2_000 && !predicate(); index++)
        {
            await Task.Delay(1);
        }
        Assert.True(predicate(), "The asynchronous condition was not reached.");
    }

    private static HashSet<int> ChargeLimits() => [50, 55, 60, 65, 70, 75, 80];

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        public int Calls { get; private set; }
        public Task InvokeAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServiceClient : IServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> statuses = Channel.CreateUnbounded<ServiceWatchUpdate>();
        private readonly TaskCompletionSource mutationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource exportCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WatchCalls { get; private set; }
        public int MutationCalls { get; private set; }
        public int ExportCalls { get; private set; }
        public int ObservationCalls { get; private set; }
        public TaskCompletionSource<ServiceStatusSnapshot>? PendingObservation { get; set; }
        public TaskCompletionSource ObservationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockMutations { get; init; }
        public bool BlockExports { get; init; }
        public bool WatchCanceled { get; private set; }
        public bool MutationCanceled { get; private set; }
        public ManualOverride<PerformancePreset>? PerformanceOverride { get; private set; }
        public ManualOverride<FanMode>? FanOverride { get; private set; }
        public ManualOverride<LightingSettings>? LightingOverride { get; private set; }
        public bool? BatteryProtectionEnabled { get; private set; }
        public bool? FullChargeEnabled { get; private set; }
        public TaskCompletionSource MutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ControlOperationResult NextResult { get; init; } = new(ControlOperationStatus.Applied, "Applied");
        public ServiceStatusSnapshot? CurrentStatus { get; private set; }
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

        public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
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

        public Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(CurrentStatus ?? throw new InvalidOperationException(
                "No status has been published."));
        }

        public Task<ServiceStatusSnapshot> ObserveStateAsync(CancellationToken cancellationToken)
        {
            ObservationCalls++;
            ObservationStarted.TrySetResult();
            return PendingObservation is not null
                ? PendingObservation.Task.WaitAsync(cancellationToken)
                : throw new NotSupportedException();
        }

        public async Task<DiagnosticArchive> ExportDiagnosticsAsync(CancellationToken token)
        {
            ExportCalls++;
            ExportStarted.TrySetResult();
            if (BlockExports)
            {
                await exportCompletion.Task.WaitAsync(token);
            }
            return NextArchive;
        }

        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) =>
            MutateAsync(token);

        public Task<ControlOperationResult> SetPerformanceOverrideAsync(
            ManualOverride<PerformancePreset>? value,
            CancellationToken token)
        {
            PerformanceOverride = value;
            return MutateAsync(token);
        }

        public Task<ControlOperationResult> SetFanOverrideAsync(
            ManualOverride<FanMode>? value,
            CancellationToken token)
        {
            FanOverride = value;
            return MutateAsync(token);
        }

        public Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken token)
        {
            LightingOverride = value;
            return MutateAsync(token);
        }

        public Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken token) => MutateAsync(token);

        public Task<ControlOperationResult> SetBatteryProtectionAsync(
            bool enabled,
            CancellationToken token)
        {
            BatteryProtectionEnabled = enabled;
            return MutateAsync(token);
        }

        public Task<ControlOperationResult> SetFullChargeOverrideAsync(
            bool enabled,
            CancellationToken token)
        {
            FullChargeEnabled = enabled;
            return MutateAsync(token);
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
            return NextResult;
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
