using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class WindowsPowerModeAutomationCoordinatorTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn, WindowsPowerMode.BestPerformance)]
    [InlineData(PowerSource.UsbC, WindowsPowerMode.Balanced)]
    [InlineData(PowerSource.Battery, WindowsPowerMode.BestPowerEfficiency)]
    public async Task FirstConfirmedProfile_AppliesRequestedMode(
        PowerSource source,
        WindowsPowerMode mode)
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);

        WindowsPowerModeAutomationObservation observation = coordinator.Observe(
            Status(source, Profiles(source, mode)));
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.ApplyScheduled, observation);
        Assert.Equal((source, mode), Assert.Single(controller.Requests));
        Assert.True(coordinator.LastResult?.Result.Succeeded);
    }

    [Fact]
    public async Task LeaveUnchanged_DoesNotCallWindows()
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);

        WindowsPowerModeAutomationObservation observation = coordinator.Observe(
            Status(PowerSource.PluggedIn, Profiles(PowerSource.PluggedIn, null)));
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.LeftUnchanged, observation);
        Assert.Empty(controller.Requests);
    }

    [Fact]
    public async Task SameSourceProfileSave_AppliesChangedModeOnlyOnce()
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);
        coordinator.Observe(Status(
            PowerSource.Battery,
            Profiles(PowerSource.Battery, WindowsPowerMode.Balanced),
            revision: 1));
        await coordinator.WhenIdleAsync();

        coordinator.Observe(Status(
            PowerSource.Battery,
            Profiles(PowerSource.Battery, WindowsPowerMode.BestPowerEfficiency),
            revision: 2));
        await coordinator.WhenIdleAsync();
        WindowsPowerModeAutomationObservation duplicate = coordinator.Observe(Status(
            PowerSource.Battery,
            Profiles(PowerSource.Battery, WindowsPowerMode.BestPowerEfficiency),
            revision: 3));
        await coordinator.WhenIdleAsync();

        Assert.Equal(2, controller.Requests.Count);
        Assert.Equal(
            (PowerSource.Battery, WindowsPowerMode.BestPowerEfficiency),
            controller.Requests[1]);
        Assert.Equal(WindowsPowerModeAutomationObservation.AlreadyObserved, duplicate);
    }

    [Theory]
    [InlineData(SessionControlAccess.InactiveSession)]
    [InlineData(SessionControlAccess.NotInteractive)]
    public async Task NonControllingSession_NeverChangesPowerMode(SessionControlAccess access)
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);

        WindowsPowerModeAutomationObservation observation = coordinator.Observe(Status(
            PowerSource.PluggedIn,
            Profiles(PowerSource.PluggedIn, WindowsPowerMode.BestPerformance),
            controlAccess: access));
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.IgnoredUnavailable, observation);
        Assert.Empty(controller.Requests);
    }

    [Fact]
    public async Task Reconnect_ReconcilesConfiguredProfileAgain()
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);
        ServiceStatusSnapshot status = Status(
            PowerSource.UsbC,
            Profiles(PowerSource.UsbC, WindowsPowerMode.BestPerformance));
        coordinator.Observe(status);
        await coordinator.WhenIdleAsync();

        coordinator.MarkUnavailable();
        coordinator.Observe(status);
        await coordinator.WhenIdleAsync();

        Assert.Equal(2, controller.Requests.Count);
    }

    [Fact]
    public async Task FailedApply_RetriesOnceThenStopsUntilTargetChanges()
    {
        var controller = new FakeController();
        controller.Results.Enqueue(Failure("first"));
        controller.Results.Enqueue(Failure("second"));
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(
            controller,
            delay: static (_, _) => Task.CompletedTask);
        ServiceStatusSnapshot status = Status(
            PowerSource.PluggedIn,
            Profiles(PowerSource.PluggedIn, WindowsPowerMode.Balanced));

        coordinator.Observe(status);
        await coordinator.WhenIdleAsync();
        coordinator.Observe(status with { Revision = 2 });
        await coordinator.WhenIdleAsync();

        Assert.Equal(2, controller.Requests.Count);
        Assert.False(coordinator.LastResult?.Result.Succeeded);
    }

    private static ProfileSet Profiles(PowerSource source, WindowsPowerMode? mode)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return source switch
        {
            PowerSource.PluggedIn => profiles with
            {
                PluggedIn = profiles.PluggedIn with { WindowsPowerMode = mode },
            },
            PowerSource.Battery => profiles with
            {
                Battery = profiles.Battery with { WindowsPowerMode = mode },
            },
            PowerSource.UsbC => profiles with
            {
                UsbC = profiles.UsbC with { WindowsPowerMode = mode },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }

    private static ServiceStatusSnapshot Status(
        PowerSource source,
        ProfileSet profiles,
        long revision = 1,
        SessionControlAccess controlAccess = SessionControlAccess.Active) => new(
        Revision: revision,
        DeviceVerified: true,
        WritesBlocked: false,
        PotentialControllers: [],
        ControllerDetectionWarning: null,
        ExternalChanges: [],
        Message: "test",
        Capabilities: null,
        ConfirmedState: null,
        Profiles: profiles,
        DesiredState: new DesiredDeviceState(
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
        ActivePowerSource: source,
        LiveReadings: null,
        PerformanceOverride: null,
        FanOverride: null,
        LightingOverride: null,
        FullChargeOverride: false,
        Firmware: null,
        ServiceVersion: "test",
        ControlAccess: controlAccess);

    private sealed class FakeController : IWindowsPowerModeController
    {
        public List<(PowerSource Source, WindowsPowerMode Mode)> Requests { get; } = [];

        public Queue<WindowsPowerModeApplyResult> Results { get; } = [];

        public Task<WindowsPowerModeApplyResult> ApplyAsync(
            PowerSource powerSource,
            WindowsPowerMode mode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((powerSource, mode));
            return Task.FromResult(Results.TryDequeue(out WindowsPowerModeApplyResult? result)
                ? result
                : Success(mode));
        }
    }

    private static WindowsPowerModeApplyResult Success(WindowsPowerMode mode) => new(
        Succeeded: true,
        Changed: true,
        mode,
        Error: null);

    private static WindowsPowerModeApplyResult Failure(string error) => new(
        Succeeded: false,
        Changed: false,
        ConfiguredMode: null,
        error);
}
