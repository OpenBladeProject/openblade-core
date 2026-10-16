using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class ThxAudioAutomationCoordinatorTests
{
    [Fact]
    public async Task RequiresBothOptInsExactCapabilityActiveSessionAndKnownOutput()
    {
        var controller = new Controller();
        await using var coordinator = new ThxAudioAutomationCoordinator(controller);
        coordinator.Observe(Status(Settings()));
        coordinator.ObserveForeground(new("game.exe", ThxAudioEndpoint.Speakers));
        await coordinator.WhenIdleAsync();
        Assert.Empty(controller.Targets);
        coordinator.SetIntegrationEnabled(true);
        await coordinator.WhenIdleAsync();
        Assert.Single(controller.Targets);
        coordinator.Observe(Status(Settings()) with { DeviceVerified = false });
        coordinator.ObserveForeground(new("another.exe", ThxAudioEndpoint.Speakers));
        await coordinator.WhenIdleAsync();
        Assert.Single(controller.Targets);
        coordinator.Observe(Status(Settings()) with { Capabilities = null });
        coordinator.ObserveForeground(new("third.exe", ThxAudioEndpoint.Speakers));
        await coordinator.WhenIdleAsync();
        Assert.Single(controller.Targets);
        coordinator.Observe(Status(Settings()) with { ControlAccess = SessionControlAccess.InactiveSession });
        await coordinator.WhenIdleAsync();
        Assert.Single(controller.Targets);
        coordinator.Observe(Status(Settings(false)));
        await coordinator.WhenIdleAsync();
        Assert.Single(controller.Targets);
        coordinator.ObserveForeground(new("game.exe"));
        coordinator.Observe(Status(Settings()));
        await coordinator.WhenIdleAsync();
        Assert.Single(controller.Targets);
    }

    [Fact]
    public async Task KnownNoMatchRestoresGeneralAndDifferentExecutableReconciles()
    {
        var controller = new Controller();
        await using var coordinator = Ready(controller);
        coordinator.ObserveForeground(new("GAME.EXE", ThxAudioEndpoint.Headphones));
        await coordinator.WhenIdleAsync();
        Assert.Equal(ThxApplicationAudioOutput.Movie, Assert.Single(controller.Targets).ApplicationRule!.Output);
        coordinator.ObserveForeground(new("desktop.exe", ThxAudioEndpoint.Headphones));
        await coordinator.WhenIdleAsync();
        Assert.Null(controller.Targets[1].ApplicationRule);
        coordinator.ObserveForeground(new("other.exe", ThxAudioEndpoint.Headphones));
        await coordinator.WhenIdleAsync();
        Assert.Equal(3, controller.Targets.Count);
        coordinator.ObserveForeground(new("OTHER.EXE", ThxAudioEndpoint.Headphones));
        await coordinator.WhenIdleAsync();
        Assert.Equal(3, controller.Targets.Count);
        coordinator.ObserveForeground(new("invalid/path.exe", ThxAudioEndpoint.Headphones));
        await coordinator.WhenIdleAsync();
        Assert.Equal(3, controller.Targets.Count);
    }

    [Fact]
    public async Task InFlightApplicationCoalescesToLatestDestination()
    {
        var controller = new Controller { BlockFirst = true };
        await using var coordinator = Ready(controller);
        coordinator.ObserveForeground(new("game.exe", ThxAudioEndpoint.Speakers));
        await controller.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.ObserveForeground(new("stale.exe", ThxAudioEndpoint.Speakers));
        coordinator.ObserveForeground(new("latest.exe", ThxAudioEndpoint.Headphones));
        controller.Release.SetResult(ThxAudioOperationStatus.Succeeded);
        await coordinator.WhenIdleAsync();
        Assert.Equal(new[] { "game.exe", "latest.exe" }, controller.Targets.Select(target => target.ExecutableName));
        Assert.Equal(ThxAudioEndpoint.Headphones, controller.Targets[1].Endpoint);
    }

    [Fact]
    public async Task UnknownCancelsAndIndeterminateStaysPausedAcrossProfileEvents()
    {
        var controller = new Controller { BlockFirst = true };
        await using var coordinator = Ready(controller);
        coordinator.ObserveForeground(new("game.exe", ThxAudioEndpoint.Speakers));
        await controller.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.ObserveForeground(ThxForegroundObservation.Unknown);
        Assert.True(controller.Token.IsCancellationRequested);
        controller.Release.SetResult(ThxAudioOperationStatus.Indeterminate);
        await coordinator.WhenIdleAsync();
        coordinator.Observe(Status(Settings()));
        coordinator.ObserveForeground(new("other.exe", ThxAudioEndpoint.Speakers));
        await coordinator.WhenIdleAsync();
        Assert.True(coordinator.IsPaused);
        Assert.Single(controller.Targets);
        await coordinator.ResetAfterExplicitReadAsync();
        await coordinator.WhenIdleAsync();
        Assert.False(coordinator.IsPaused);
        Assert.Single(controller.Targets);
        coordinator.ObserveForeground(new("next.exe", ThxAudioEndpoint.Speakers));
        await coordinator.WhenIdleAsync();
        Assert.Equal(2, controller.Targets.Count);
    }

    [Fact]
    public async Task ExplicitReadWaitsForLateIndeterminateAndDoesNotReplay()
    {
        var controller = new Controller { BlockFirst = true };
        await using var coordinator = Ready(controller);
        coordinator.ObserveForeground(new("game.exe", ThxAudioEndpoint.Speakers));
        await controller.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.PauseForManualOperation();
        Task reset = coordinator.ResetAfterExplicitReadAsync();
        Assert.False(reset.IsCompleted);
        controller.Release.SetResult(ThxAudioOperationStatus.Indeterminate);
        await reset;
        Assert.False(coordinator.IsPaused);
        Assert.Single(controller.Targets);
    }

    [Fact]
    public async Task LaterManualOperationCannotBeClearedByEarlierRead()
    {
        var controller = new Controller { BlockFirst = true };
        await using var coordinator = Ready(controller);
        coordinator.ObserveForeground(new("game.exe", ThxAudioEndpoint.Speakers));
        await controller.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.PauseForManualOperation();
        Task reset = coordinator.ResetAfterExplicitReadAsync();
        coordinator.PauseForManualOperation();
        controller.Release.SetResult(ThxAudioOperationStatus.Indeterminate);
        await reset;
        Assert.True(coordinator.IsPaused);
    }

    [Fact]
    public async Task DisposalCancelsInFlightAndPreventsPendingDestination()
    {
        var controller = new Controller { BlockFirst = true };
        var coordinator = Ready(controller);
        coordinator.ObserveForeground(new("game.exe", ThxAudioEndpoint.Speakers));
        await controller.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.ObserveForeground(new("later.exe", ThxAudioEndpoint.Speakers));
        Task disposal = coordinator.DisposeAsync().AsTask();
        Assert.True(controller.Token.IsCancellationRequested);
        Assert.False(disposal.IsCompleted);
        controller.Release.SetResult(ThxAudioOperationStatus.Indeterminate);
        await disposal;
        Assert.Single(controller.Targets);
    }

    [Fact]
    public async Task ReenablingOptInReconcilesSameDestinationWithoutClearingUncertainty()
    {
        var controller = new Controller();
        await using var coordinator = Ready(controller);
        coordinator.ObserveForeground(new("game.exe", ThxAudioEndpoint.Speakers));
        await coordinator.WhenIdleAsync();
        coordinator.SetIntegrationEnabled(false);
        await coordinator.WhenIdleAsync();
        Assert.Single(controller.Targets);
        coordinator.SetIntegrationEnabled(true);
        await coordinator.WhenIdleAsync();
        Assert.Equal(2, controller.Targets.Count);
        coordinator.PauseForManualOperation();
        coordinator.SetIntegrationEnabled(false);
        coordinator.SetIntegrationEnabled(true);
        await coordinator.WhenIdleAsync();
        Assert.True(coordinator.IsPaused);
        Assert.Equal(2, controller.Targets.Count);
    }
    private static ThxAudioAutomationCoordinator Ready(Controller controller)
    {
        var coordinator = new ThxAudioAutomationCoordinator(controller);
        coordinator.SetIntegrationEnabled(true);
        coordinator.Observe(Status(Settings()));
        return coordinator;
    }

    private static ThxAudioProfileSettings Settings(bool routing = true) => new(
        routing, Endpoint(7, true), Endpoint(10, false),
        [new ThxApplicationAudioRule("game.exe", ThxApplicationAudioOutput.Movie)]);

    private static ThxAudioEndpointSettings Endpoint(int count, bool normalization)
    {
        double[] gains = new double[count];
        return new(false, false, ThxEqualizerPreset.Music,
            new(gains, gains, gains, gains, gains), new(false, 50),
            normalization ? new(false, 50) : null);
    }

    private static ServiceStatusSnapshot Status(ThxAudioProfileSettings settings)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with { PluggedIn = defaults.PluggedIn with { ThxAudio = settings } };
        return new(1, true, false, [], null, [], "test",
            new DeviceCapabilities(new HashSet<PerformancePreset>(), new HashSet<FanMode>(),
                new HashSet<LightingEffect>(), new HashSet<int>(), false, false)
            { ThxAudio = new(DeviceFeatureAccess.ReadWrite, ThxAudioProductIds.Rznb12) },
            null, profiles,
            new DesiredDeviceState(
                new(profiles.PluggedIn.Performance, SettingValueSource.PowerProfile),
                new(profiles.PluggedIn.Fans, SettingValueSource.PowerProfile),
                new(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn, null, null, null, null, false);
    }

    private sealed class Controller : IThxAudioAutomationController
    {
        internal List<ThxAudioAutomationTarget> Targets { get; } = [];
        internal bool BlockFirst { get; init; }
        internal CancellationToken Token { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ThxAudioOperationStatus> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ThxAudioOperationStatus> ApplyAsync(ThxAudioAutomationTarget target, CancellationToken cancellationToken)
        {
            Targets.Add(target);
            Token = cancellationToken;
            Started.TrySetResult();
            return BlockFirst && Targets.Count == 1 ? Release.Task : Task.FromResult(ThxAudioOperationStatus.Succeeded);
        }
    }
}
