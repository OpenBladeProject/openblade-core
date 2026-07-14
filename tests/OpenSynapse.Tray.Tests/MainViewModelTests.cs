using System.Threading.Channels;
using OpenSynapse.Contracts;
using OpenSynapse.Tray;

namespace OpenSynapse.Tray.Tests;

public sealed class MainViewModelTests
{
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
        await WaitUntilAsync(() => dispatcher.Calls > 0);

        Assert.Equal(1, client.WatchCalls);
        Assert.True(viewModel.IsConnected);
        Assert.True(viewModel.IsReadOnly);
        Assert.Equal(
            ["Razer Central Service", "Razer Synapse Service"],
            viewModel.ConflictNames);
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
                50,
                80,
                5)));
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
                BatteryPolicy.Create(80)),
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
            FullChargeOverride = true,
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.SelectedPerformance == PerformancePreset.Performance);

        Assert.Contains("CPU unavailable", viewModel.TelemetryStatus, StringComparison.Ordinal);
        Assert.Contains("GPU unavailable", viewModel.TelemetryStatus, StringComparison.Ordinal);
        Assert.Contains("Fans unavailable", viewModel.TelemetryStatus, StringComparison.Ordinal);
        Assert.Contains("Battery 61%", viewModel.TelemetryStatus, StringComparison.Ordinal);
        Assert.Equal("Battery profile", viewModel.ActivePowerSourceLabel);
        Assert.Equal("Persistent override", viewModel.PerformanceSourceLabel);
        Assert.Equal("Until power change", viewModel.FanSourceLabel);
        Assert.Equal("Power profile", viewModel.LightingSourceLabel);
        Assert.Equal(status.Capabilities, viewModel.Capabilities);
        Assert.Equal(status.Telemetry, viewModel.Telemetry);
        Assert.Equal(status.PerformanceOverride, viewModel.PerformanceOverride);
        Assert.Equal(status.FanOverride, viewModel.FanOverride);
        Assert.Equal(status.LightingOverride, viewModel.LightingOverride);
        Assert.True(viewModel.FullChargeOverride);
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
                50, 80, 5);
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
            message,
            capabilities ?? (verified
                ? DeviceCapabilities.SafeTargetDefaults()
                : DeviceCapabilities.Unavailable()),
            null,
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
                profiles.BatteryPolicy),
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
        for (int index = 0; index < 10_000 && !predicate(); index++)
        {
            await Task.Yield();
        }
        Assert.True(predicate(), "The asynchronous condition was not reached.");
    }

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
        private readonly Channel<ServiceStatusSnapshot> statuses = Channel.CreateUnbounded<ServiceStatusSnapshot>();
        private readonly TaskCompletionSource mutationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WatchCalls { get; private set; }
        public int MutationCalls { get; private set; }
        public bool BlockMutations { get; init; }
        public bool WatchCanceled { get; private set; }
        public bool MutationCanceled { get; private set; }
        public ManualOverride<PerformancePreset>? PerformanceOverride { get; private set; }
        public TaskCompletionSource MutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ControlOperationResult NextResult { get; init; } = new(ControlOperationStatus.Applied, "Applied");
        public void Publish(ServiceStatusSnapshot status) => statuses.Writer.TryWrite(status);
        public void CompleteWatch() => statuses.Writer.TryComplete();
        public void CompleteMutation() => mutationCompletion.TrySetResult();

        public async IAsyncEnumerable<ServiceStatusSnapshot> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            WatchCalls++;
            try
            {
                await foreach (ServiceStatusSnapshot status in statuses.Reader.ReadAllAsync(token))
                {
                    yield return status;
                }
            }
            finally
            {
                WatchCanceled = token.IsCancellationRequested;
            }
        }

        public Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token) =>
            throw new NotSupportedException();

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
            CancellationToken token) => MutateAsync(token);

        public Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken token) => MutateAsync(token);

        public Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken token) => MutateAsync(token);

        public Task<ControlOperationResult> SetFullChargeOverrideAsync(
            bool enabled,
            CancellationToken token) => MutateAsync(token);

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
}
