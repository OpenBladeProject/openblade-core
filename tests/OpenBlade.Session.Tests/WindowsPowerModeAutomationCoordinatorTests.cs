using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class WindowsPowerModeAutomationCoordinatorTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Silent, WindowsPowerMode.BestPowerEfficiency)]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Balanced, WindowsPowerMode.Balanced)]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Performance, WindowsPowerMode.BestPerformance)]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Custom, WindowsPowerMode.BestPerformance)]
    [InlineData(PowerSource.Battery, PerformancePreset.BatterySaver, WindowsPowerMode.BestPowerEfficiency)]
    [InlineData(PowerSource.Battery, PerformancePreset.BatteryBalanced, WindowsPowerMode.Balanced)]
    public async Task FirstConfirmedPerformance_AppliesAssociatedMode(
        PowerSource source,
        PerformancePreset performance,
        WindowsPowerMode mode)
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);

        WindowsPowerModeAutomationObservation observation = coordinator.Observe(
            Status(source, Profiles(performance, mode), performance));
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
            Status(
                PowerSource.PluggedIn,
                Profiles(PerformancePreset.Balanced, null),
                PerformancePreset.Balanced));
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.LeftUnchanged, observation);
        Assert.Empty(controller.Requests);
    }

    [Fact]
    public async Task ConfirmedPerformanceChange_AppliesAssociatedModeOnlyOnce()
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);
        coordinator.Observe(Status(
            PowerSource.Battery,
            Profiles(PerformancePreset.BatterySaver, WindowsPowerMode.Balanced),
            PerformancePreset.BatterySaver,
            revision: 1));
        await coordinator.WhenIdleAsync();

        coordinator.Observe(Status(
            PowerSource.Battery,
            Profiles(PerformancePreset.BatteryBalanced, WindowsPowerMode.BestPowerEfficiency),
            PerformancePreset.BatteryBalanced,
            revision: 2));
        await coordinator.WhenIdleAsync();
        WindowsPowerModeAutomationObservation duplicate = coordinator.Observe(Status(
            PowerSource.Battery,
            Profiles(PerformancePreset.BatteryBalanced, WindowsPowerMode.BestPowerEfficiency),
            PerformancePreset.BatteryBalanced,
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
            Profiles(PerformancePreset.Performance, WindowsPowerMode.BestPerformance),
            PerformancePreset.Performance,
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
            Profiles(PerformancePreset.Balanced, WindowsPowerMode.BestPerformance),
            PerformancePreset.Balanced);
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
            Profiles(PerformancePreset.Balanced, WindowsPowerMode.Balanced),
            PerformancePreset.Balanced);

        coordinator.Observe(status);
        await coordinator.WhenIdleAsync();
        coordinator.Observe(status with { Revision = 2 });
        await coordinator.WhenIdleAsync();

        Assert.Equal(2, controller.Requests.Count);
        Assert.False(coordinator.LastResult?.Result.Succeeded);
    }

    [Fact]
    public async Task FailedApply_RetriesForDifferentConfirmedPresetWithSameMode()
    {
        var controller = new FakeController();
        controller.Results.Enqueue(Failure("first"));
        controller.Results.Enqueue(Failure("second"));
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(
            controller,
            delay: static (_, _) => Task.CompletedTask);
        ProfileSet profiles = Profiles(
            PerformancePreset.Balanced,
            WindowsPowerMode.Balanced) with
        {
            WindowsPowerModes = WindowsPowerModeAssociations.LeaveUnchanged
                .WithPerformance(PerformancePreset.Balanced, WindowsPowerMode.Balanced)
                .WithPerformance(PerformancePreset.Silent, WindowsPowerMode.Balanced),
        };

        coordinator.Observe(Status(
            PowerSource.PluggedIn,
            profiles,
            PerformancePreset.Balanced));
        await coordinator.WhenIdleAsync();
        WindowsPowerModeAutomationObservation observation = coordinator.Observe(Status(
            PowerSource.PluggedIn,
            profiles,
            PerformancePreset.Silent,
            revision: 2));
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.ApplyScheduled, observation);
        Assert.Equal(3, controller.Requests.Count);
        Assert.True(coordinator.LastResult?.Result.Succeeded);
    }

    [Fact]
    public async Task UnrelatedUnconfirmedWrite_DoesNotBlockConfirmedPerformance()
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);
        ServiceStatusSnapshot status = Status(
            PowerSource.PluggedIn,
            Profiles(PerformancePreset.Performance, WindowsPowerMode.BestPerformance),
            PerformancePreset.Performance) with
        {
            WritesBlocked = true,
            UnconfirmedWrites = [DeviceSubsystem.Lighting],
        };

        WindowsPowerModeAutomationObservation observation = coordinator.Observe(status);
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.ApplyScheduled, observation);
        Assert.Single(controller.Requests);
    }

    [Fact]
    public async Task UnconfirmedPerformanceWrite_BlocksWindowsMode()
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);
        ServiceStatusSnapshot status = Status(
            PowerSource.PluggedIn,
            Profiles(PerformancePreset.Performance, WindowsPowerMode.BestPerformance),
            PerformancePreset.Performance) with
        {
            UnconfirmedWrites = [DeviceSubsystem.Performance],
        };

        WindowsPowerModeAutomationObservation observation = coordinator.Observe(status);
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.IgnoredUnavailable, observation);
        Assert.Empty(controller.Requests);
    }

    [Fact]
    public async Task UnverifiedDevice_DoesNotChangeWindowsMode()
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);

        WindowsPowerModeAutomationObservation observation = coordinator.Observe(Status(
            PowerSource.PluggedIn,
            Profiles(PerformancePreset.Performance, WindowsPowerMode.BestPerformance),
            PerformancePreset.Performance,
            deviceVerified: false));
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.IgnoredUnavailable, observation);
        Assert.Empty(controller.Requests);
    }

    [Fact]
    public async Task MaximumFans_WaitsForNextConfirmedPerformancePreset()
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);
        ProfileSet profiles = Profiles(
            PerformancePreset.Balanced,
            WindowsPowerMode.Balanced) with
        {
            WindowsPowerModes = WindowsPowerModeAssociations.LeaveUnchanged
                .WithPerformance(PerformancePreset.Balanced, WindowsPowerMode.Balanced)
                .WithPerformance(
                    PerformancePreset.Performance,
                    WindowsPowerMode.BestPerformance),
        };
        ServiceStatusSnapshot initial = Status(
            PowerSource.PluggedIn,
            profiles,
            PerformancePreset.Balanced);
        coordinator.Observe(initial);
        await coordinator.WhenIdleAsync();

        ServiceStatusSnapshot maximum = initial with
        {
            Revision = 2,
            ConfirmedState = initial.ConfirmedState! with
            {
                Performance = null,
                Fans = FanMode.Maximum,
            },
            DesiredState = initial.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Performance,
                    SettingValueSource.PowerProfile),
            },
        };
        WindowsPowerModeAutomationObservation unavailable = coordinator.Observe(maximum);
        await coordinator.WhenIdleAsync();

        WindowsPowerModeAutomationObservation reconciled = coordinator.Observe(maximum with
        {
            Revision = 3,
            ConfirmedState = maximum.ConfirmedState! with
            {
                Performance = PerformancePreset.Performance,
                Fans = FanMode.Automatic,
            },
        });
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.IgnoredUnavailable, unavailable);
        Assert.Equal(WindowsPowerModeAutomationObservation.ApplyScheduled, reconciled);
        Assert.Equal(2, controller.Requests.Count);
        Assert.Equal(
            (PowerSource.PluggedIn, WindowsPowerMode.BestPerformance),
            controller.Requests[1]);
    }

    [Fact]
    public async Task DesiredPerformanceWithoutConfirmedReadback_DoesNotRetargetWindows()
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);
        ProfileSet profiles = Profiles(
            PerformancePreset.Balanced,
            WindowsPowerMode.Balanced) with
        {
            WindowsPowerModes = WindowsPowerModeAssociations.LeaveUnchanged
                .WithPerformance(
                    PerformancePreset.Balanced,
                    WindowsPowerMode.Balanced)
                .WithPerformance(
                    PerformancePreset.Performance,
                    WindowsPowerMode.BestPerformance),
        };
        ServiceStatusSnapshot initial = Status(
            PowerSource.PluggedIn,
            profiles,
            PerformancePreset.Balanced);
        coordinator.Observe(initial);
        await coordinator.WhenIdleAsync();

        WindowsPowerModeAutomationObservation observation = coordinator.Observe(initial with
        {
            Revision = 2,
            DesiredState = initial.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Performance,
                    SettingValueSource.TemporaryOverride),
            },
        });
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.AlreadyObserved, observation);
        Assert.Single(controller.Requests);
    }

    [Fact]
    public async Task SameMode_ReappliesForDcButNotAnotherAcSource()
    {
        var controller = new FakeController();
        await using var coordinator = new WindowsPowerModeAutomationCoordinator(controller);
        ProfileSet profiles = Profiles(
            PerformancePreset.Balanced,
            WindowsPowerMode.Balanced);

        coordinator.Observe(Status(
            PowerSource.PluggedIn,
            profiles,
            PerformancePreset.Balanced));
        await coordinator.WhenIdleAsync();
        WindowsPowerModeAutomationObservation usbC = coordinator.Observe(Status(
            PowerSource.UsbC,
            profiles,
            PerformancePreset.Balanced,
            revision: 2));
        WindowsPowerModeAutomationObservation battery = coordinator.Observe(Status(
            PowerSource.Battery,
            profiles,
            PerformancePreset.Balanced,
            revision: 3));
        await coordinator.WhenIdleAsync();

        Assert.Equal(WindowsPowerModeAutomationObservation.AlreadyObserved, usbC);
        Assert.Equal(WindowsPowerModeAutomationObservation.ApplyScheduled, battery);
        Assert.Equal(2, controller.Requests.Count);
        Assert.Equal(PowerSource.Battery, controller.Requests[1].Source);
    }

    private static ProfileSet Profiles(
        PerformancePreset performance,
        WindowsPowerMode? mode)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return profiles with
        {
            WindowsPowerModes = WindowsPowerModeAssociations.LeaveUnchanged
                .WithPerformance(performance, mode),
        };
    }

    private static ServiceStatusSnapshot Status(
        PowerSource source,
        ProfileSet profiles,
        PerformancePreset? confirmedPerformance,
        long revision = 1,
        SessionControlAccess controlAccess = SessionControlAccess.Active,
        bool deviceVerified = true) => new(
        Revision: revision,
        DeviceVerified: deviceVerified,
        WritesBlocked: false,
        PotentialControllers: [],
        ControllerDetectionWarning: null,
        ExternalChanges: [],
        Message: "test",
        Capabilities: null,
        ConfirmedState: new ConfirmedDeviceState(
            confirmedPerformance,
            FanMode.Automatic,
            Battery: null,
            Lighting: null),
        Profiles: profiles,
        DesiredState: new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(
                confirmedPerformance ?? profiles.PluggedIn.Performance,
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
