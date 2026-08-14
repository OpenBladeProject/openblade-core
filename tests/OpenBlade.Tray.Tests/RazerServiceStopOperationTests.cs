using System.ServiceProcess;

namespace OpenBlade.Tray.Tests;

public sealed class RazerServiceStopOperationTests
{
    [Fact]
    public void StopConfirmationWaitKeepsItsReviewedBound()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), RazerServiceStopOperation.PollInterval);
        Assert.Equal(15, RazerServiceStopOperation.MaximumPolls);
    }

    [Fact]
    public void CreateInventoryMatchesExactKnownNamesAndExcludesSupportServices()
    {
        RazerServiceInventory inventory = RazerServiceStopOperation.CreateInventory(
        [
            Snapshot("RGS", "Unknown display name"),
            Snapshot("unexpected", "Razer Synapse Service"),
            Snapshot("Razer Elevation Service", "Razer Elevation Service"),
            Snapshot("Razer Game Manager Service 3", "Razer Game Manager Service 3"),
            Snapshot("RazerSynapseServiceHelper", "Razer Synapse Service Helper"),
        ]);

        Assert.True(inventory.Complete);
        Assert.Equal(
            ["unexpected", "RGS"],
            inventory.Services.Select(service => service.ServiceName));
    }

    [Fact]
    public void CreateInventoryDeduplicatesAndSortsMatchesDeterministically()
    {
        RazerServiceInventory inventory = RazerServiceStopOperation.CreateInventory(
        [
            Snapshot("RGS", "Razer Gaming Services Updater"),
            Snapshot("razerSynapseService", "Razer Synapse Service"),
            Snapshot("RAZERSYNAPSESERVICE", "duplicate"),
            Snapshot("Razer Chroma SDK Service", "Razer Chroma SDK Service"),
        ]);

        Assert.Equal(
            [
                "Razer Chroma SDK Service",
                "Razer Gaming Services Updater",
                "Razer Synapse Service",
            ],
            inventory.Services.Select(service => service.DisplayName));
    }

    [Fact]
    public void ActiveServicesExcludesOnlyConfirmedStoppedServices()
    {
        RazerServiceInventory inventory = RazerServiceStopOperation.CreateInventory(
        [
            Snapshot("RGS", "Razer Gaming Services Updater", ServiceControllerStatus.Stopped),
            Snapshot("RazerSynapseService", "Razer Synapse Service", ServiceControllerStatus.StopPending),
            Snapshot("Razer Chroma SDK Service", "Razer Chroma SDK Service", ServiceControllerStatus.Paused),
        ]);

        Assert.Equal(
            ["Razer Chroma SDK Service", "Razer Synapse Service"],
            RazerServiceStopOperation.ActiveServices(inventory)
                .Select(service => service.DisplayName));
    }

    [Fact]
    public void EvaluateReadbackIncludesAllowlistedServiceThatStartedAfterPreflight()
    {
        RazerServiceSnapshot initial = Snapshot(
            "RGS",
            "Razer Gaming Services Updater");
        RazerServiceInventory final = RazerServiceStopOperation.CreateInventory(
        [
            initial with { Status = ServiceControllerStatus.Stopped },
            Snapshot("RazerSynapseService", "Razer Synapse Service"),
        ]);

        RazerServiceStopReadback readback = RazerServiceStopOperation.EvaluateReadback(
            [initial],
            final);

        Assert.Equal(["Razer Gaming Services Updater"], readback.StoppedOrRemoved);
        RazerServiceSnapshot remaining = Assert.Single(readback.RemainingActive);
        Assert.Equal("RazerSynapseService", remaining.ServiceName);
    }

    [Fact]
    public async Task ExecuteFailsClosedWhenEnumerationFails()
    {
        int stopCalls = 0;

        RazerServiceStopExecutionResult result = await RazerServiceStopOperation.ExecuteAsync(
            () => new([], "enumeration failed"),
            (_, _) =>
            {
                stopCalls++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("enumeration failed", result.InventoryError);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.Failures);
        Assert.Equal(0, stopCalls);
    }

    [Fact]
    public async Task ExecuteContinuesAfterIndependentFailureAndPreservesOrder()
    {
        RazerServiceInventory inventory = RazerServiceStopOperation.CreateInventory(
        [
            Snapshot("RazerSynapseService", "Razer Synapse Service"),
            Snapshot("RGS", "Razer Gaming Services Updater"),
            Snapshot("Razer Chroma SDK Service", "Razer Chroma SDK Service"),
        ]);
        var attempts = new List<string>();

        RazerServiceStopExecutionResult result = await RazerServiceStopOperation.ExecuteAsync(
            () => inventory,
            (serviceName, _) =>
            {
                attempts.Add(serviceName);
                return serviceName switch
                {
                    "RGS" => throw new InvalidOperationException("access denied"),
                    "RazerSynapseService" => Task.FromResult(false),
                    _ => Task.FromResult(true),
                };
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(
            ["Razer Chroma SDK Service", "RGS", "RazerSynapseService"],
            attempts);
        Assert.Equal(2, result.Failures.Count);
        Assert.Contains(result.Failures, failure => failure.Message == "access denied");
        Assert.Contains(
            result.Failures,
            failure => failure.Message.Contains("bounded wait", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopOneAlreadyStoppedDoesNotRequestStopOrDelay()
    {
        int stopCalls = 0;
        int delayCalls = 0;

        bool result = await RazerServiceStopOperation.StopOneAsync(
            () => ServiceControllerStatus.Stopped,
            () => true,
            () => stopCalls++,
            (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            maximumPolls: 3,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(0, stopCalls);
        Assert.Equal(0, delayCalls);
    }

    [Fact]
    public async Task StopOneWaitsThroughPendingStartThenStopsOnce()
    {
        var states = new Queue<ServiceControllerStatus>(
        [
            ServiceControllerStatus.StartPending,
            ServiceControllerStatus.Running,
            ServiceControllerStatus.StopPending,
            ServiceControllerStatus.Stopped,
        ]);
        int stopCalls = 0;

        bool result = await RazerServiceStopOperation.StopOneAsync(
            () => states.Dequeue(),
            () => true,
            () => stopCalls++,
            (_, _) => Task.CompletedTask,
            maximumPolls: 4,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, stopCalls);
        Assert.Empty(states);
    }

    [Fact]
    public async Task StopOneWaitsForExistingStopWithoutIssuingAnotherRequest()
    {
        var states = new Queue<ServiceControllerStatus>(
        [
            ServiceControllerStatus.StopPending,
            ServiceControllerStatus.Stopped,
        ]);
        int stopCalls = 0;

        bool result = await RazerServiceStopOperation.StopOneAsync(
            () => states.Dequeue(),
            () => true,
            () => stopCalls++,
            (_, _) => Task.CompletedTask,
            maximumPolls: 2,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(0, stopCalls);
    }

    [Fact]
    public async Task StopOneTimesOutWithoutRetryingStop()
    {
        int stopCalls = 0;

        bool result = await RazerServiceStopOperation.StopOneAsync(
            () => ServiceControllerStatus.Running,
            () => true,
            () => stopCalls++,
            (_, _) => Task.CompletedTask,
            maximumPolls: 3,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, stopCalls);
    }

    [Fact]
    public async Task StopOneRejectsNonStoppableServiceWithoutWrite()
    {
        int stopCalls = 0;

        bool result = await RazerServiceStopOperation.StopOneAsync(
            () => ServiceControllerStatus.Running,
            () => false,
            () => stopCalls++,
            (_, _) => Task.CompletedTask,
            maximumPolls: 3,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, stopCalls);
    }

    [Fact]
    public async Task StopOneObservesCancellationBeforeWrite()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int stopCalls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RazerServiceStopOperation.StopOneAsync(
                () => ServiceControllerStatus.Running,
                () => true,
                () => stopCalls++,
                (_, _) => Task.CompletedTask,
                maximumPolls: 3,
                TimeSpan.FromMilliseconds(1),
                cancellation.Token));

        Assert.Equal(0, stopCalls);
    }

    private static RazerServiceSnapshot Snapshot(
        string serviceName,
        string displayName,
        ServiceControllerStatus status = ServiceControllerStatus.Running) =>
        new(serviceName, displayName, status, CanStop: true);
}
