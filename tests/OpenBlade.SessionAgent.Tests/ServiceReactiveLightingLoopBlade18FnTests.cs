using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed partial class ServiceReactiveLightingLoopTests
{
    [Fact]
    public async Task Blade18PowerLossCancelsActionsAndFunctionRowInputUntilFreshAdmission()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var failures = new ConcurrentQueue<Exception>();
        var actions = new ConcurrentQueue<Blade18FnShortcut>();
        var refreshStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int functionRowResets = 0;
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            new ServiceReactiveLightingLoopOptions(
                ErrorHandler: failures.Enqueue,
                ExecuteFunctionRowInputAsync: (_, _) => Task.CompletedTask,
                ResetFunctionRowInputAsync: _ =>
                {
                    Interlocked.Increment(ref functionRowResets);
                    return Task.CompletedTask;
                },
                ExecuteBlade18FnShortcutAsync: async (shortcut, token) =>
                {
                    actions.Enqueue(shortcut);
                    if (shortcut != Blade18FnShortcut.RefreshRate)
                    {
                        return;
                    }

                    refreshStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        refreshCanceled.TrySetResult();
                        throw;
                    }
                }));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Blade18Status());
        await WaitUntilAsync(() => factory.Last?.IsBlade18FnInputAdmitted() is true);
        factory.Last!.PublishBlade18FnShortcut(Blade18FnShortcut.RefreshRate);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Last.PublishBlade18FnShortcut(Blade18FnShortcut.Touchpad);

        client.Publish(Blade18Status(revision: 2) with
        {
            ActivePowerSource = PowerSource.ExternalUnknown,
        });
        await refreshCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => factory.Last?.IsBlade18FnInputAdmitted() is false);
        int resetsAfterPowerLoss = Volatile.Read(ref functionRowResets);
        Assert.True(resetsAfterPowerLoss > 0);

        factory.Last.PublishBlade18FnShortcut(Blade18FnShortcut.Touchpad);
        await Task.Delay(50);
        Assert.Equal([Blade18FnShortcut.RefreshRate], actions);
        Assert.Empty(failures);

        client.Publish(Blade18Status(revision: 3, powerSource: PowerSource.Battery));
        await WaitUntilAsync(() => factory.Last.IsBlade18FnInputAdmitted());
        factory.Last.PublishBlade18FnShortcut(Blade18FnShortcut.Touchpad);
        await WaitUntilAsync(() => actions.Count == 2);
        Assert.Equal(
            [Blade18FnShortcut.RefreshRate, Blade18FnShortcut.Touchpad],
            actions);

        factory.Last.ResetFunctionRowInput();
        await WaitUntilAsync(
            () => Volatile.Read(ref functionRowResets) > resetsAfterPowerLoss);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Blade18NullDriverModeStatusPreservesFnAdmission()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            new ServiceReactiveLightingLoopOptions(
                ExecuteBlade18FnShortcutAsync: (_, _) => Task.CompletedTask));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Blade18Status());
        await WaitUntilAsync(() => factory.Last?.IsBlade18FnInputAdmitted() is true);
        await WaitUntilAsync(() => client.DeliveryCount >= 1);
        int resets = Volatile.Read(ref factory.Last!.FunctionPResetCount);

        client.Publish(Blade18Status(revision: 2));
        await WaitUntilAsync(() => client.DeliveryCount >= 2);

        Assert.True(factory.Last.IsBlade18FnInputAdmitted());
        Assert.Equal(resets, Volatile.Read(ref factory.Last.FunctionPResetCount));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task OtherModelCannotPublishBlade18FnAction()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var actions = new ConcurrentQueue<Blade18FnShortcut>();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            new ServiceReactiveLightingLoopOptions(
                ExecuteBlade18FnShortcutAsync: (shortcut, _) =>
                {
                    actions.Enqueue(shortcut);
                    return Task.CompletedTask;
                }));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            Firmware = Firmware(RazerBladeModelCatalog.OpenBladeTargetModelNumber),
            ControlAccess = SessionControlAccess.Active,
        });
        await WaitUntilAsync(
            () => factory.Last?.ReadVerifiedInputDeviceKind()
                == RazerBladeInputDeviceKind.Rz09_0581);

        Assert.False(factory.Last!.IsBlade18FnInputAdmitted());
        factory.Last.PublishBlade18FnShortcut(Blade18FnShortcut.RefreshRate);
        await Task.Delay(50);
        Assert.Empty(actions);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Blade18ResetDiscardsPendingFunctionPShortcut()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            new ServiceReactiveLightingLoopOptions(
                PerformanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(100),
                ExecuteBlade18FnShortcutAsync: (_, _) => Task.CompletedTask));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Blade18Status());
        await WaitUntilAsync(() => factory.Last?.IsBlade18FnInputAdmitted() is true);
        factory.Last!.PublishPerformanceShortcut(PerformanceShortcutSource.FunctionP);
        await WaitUntilAsync(() => client.PerformancePreviews.Count > 0);

        loop.ResetFunctionPInput();
        await Task.Delay(200);

        Assert.Empty(client.PerformanceSelections);
        Assert.Empty(client.PerformanceShortcuts);
        Assert.Contains(client.PerformancePreviews, preview => preview.Preset is null);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task NonBlade18ResetPreservesDedicatedPerformanceShortcut()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            new ServiceReactiveLightingLoopOptions(
                PerformanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25)));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            Firmware = Firmware(RazerBladeModelCatalog.OpenBladeTargetModelNumber),
            ControlAccess = SessionControlAccess.Active,
        });
        await WaitUntilAsync(
            () => factory.Last?.ReadVerifiedInputDeviceKind()
                == RazerBladeInputDeviceKind.Rz09_0581);
        factory.Last!.PublishPerformanceShortcut(PerformanceShortcutSource.DedicatedButton);

        loop.ResetFunctionPInput();
        await WaitUntilAsync(
            () => client.PerformanceSelections.Count + client.PerformanceShortcuts.Count == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    private static ServiceStatusSnapshot Blade18Status(
        long revision = 1,
        PowerSource powerSource = PowerSource.PluggedIn) =>
        Status(LightingRendererOwnershipState.Static) with
        {
            Revision = revision,
            ActivePowerSource = powerSource,
            Firmware = Firmware(RazerBladeModelCatalog.Rz090484ModelNumber),
            ControlAccess = SessionControlAccess.Active,
        };

    private static FirmwareVersions Firmware(string modelNumber) => new(
        "2.02",
        "1.09",
        "Unavailable",
        "Unavailable",
        modelNumber);
}
