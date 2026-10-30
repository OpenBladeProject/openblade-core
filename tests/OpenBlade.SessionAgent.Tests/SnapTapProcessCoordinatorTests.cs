using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapProcessCoordinatorTests
{
    [Fact]
    public async Task OffDefault_DoesNotLaunch()
    {
        var source = new Preference();
        int launches = 0;
        using var cancel = new CancellationTokenSource();
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        Task run = new SnapTapProcessCoordinator(source, () => { launches++; return new Child(); }, statuses.Enqueue).RunAsync(cancel.Token);
        await WaitUntilAsync(() => !statuses.IsEmpty);
        Assert.Equal(0, launches);
        Assert.Equal(SnapTapRuntimeState.Off, statuses.Last().State);
        cancel.Cancel();
        await run;
    }

    [Fact]
    public async Task Enable_IsSingleLaunch_StartingPrecedesFactory()
    {
        var source = new Preference();
        var child = new Child();
        int launches = 0;
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(source, () =>
        {
            Assert.Equal(SnapTapRuntimeState.Starting, statuses.Last().State);
            Assert.Equal(Environment.ProcessId, statuses.Last().ProcessId);
            launches++;
            return child;
        }, statuses.Enqueue).RunAsync(cancel.Token);
        source.Set(true);
        await WaitUntilAsync(() => launches == 1);
        source.Set(true);
        source.Set(false);
        await WaitUntilAsync(() => child.Disposed);
        Assert.Equal(1, launches);
        Assert.True(child.StopRequested);
        cancel.Cancel();
        await run;
    }

    [Fact]
    public async Task UnexpectedExit_LatchesUntilExplicitOffOn()
    {
        var source = new Preference(true);
        var children = new ConcurrentQueue<Child>();
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(source, () =>
        {
            var child = new Child(); children.Enqueue(child); return child;
        }, statuses.Enqueue).RunAsync(cancel.Token);
        await WaitUntilAsync(() => children.Count == 1);
        children.First().Exit(0);
        await WaitUntilAsync(() => statuses.Last().State == SnapTapRuntimeState.Failed);
        source.Set(true);
        source.Set(false);
        source.Set(true);
        await WaitUntilAsync(() => children.Count == 2);
        cancel.Cancel();
        await run;
        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.True(child.Disposed));
    }

    [Fact]
    public async Task SlowStop_DoesNotLaunchReplacementUntilOldChildExits()
    {
        var source = new Preference(true);
        var first = new Child { ExitOnStop = false };
        int launches = 0;
        using var cancel = new CancellationTokenSource();
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        Task run = new SnapTapProcessCoordinator(source, () => ++launches == 1 ? first : new Child(), statuses.Enqueue).RunAsync(cancel.Token);
        await WaitUntilAsync(() => launches == 1);
        source.Set(false);
        await WaitUntilAsync(() => first.StopRequested);
        source.Set(true);
        await WaitUntilAsync(() => statuses.Last().State == SnapTapRuntimeState.Failed, TimeSpan.FromSeconds(4));
        Assert.Equal("StopTimedOut", statuses.Last().Failure?.Reason);
        Assert.Equal("WaitForExit", statuses.Last().Failure?.Stage);
        Assert.Equal(1, launches);
        Assert.False(first.Disposed);
        first.Exit(0);
        await WaitUntilAsync(() => launches == 2);
        Assert.True(first.Disposed);
        cancel.Cancel();
        await run;
    }

    [Fact]
    public async Task Cancellation_StopsAndDisposesChild()
    {
        var child = new Child();
        using var cancel = new CancellationTokenSource();
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        Task run = new SnapTapProcessCoordinator(new Preference(true), () => child, statuses.Enqueue).RunAsync(cancel.Token);
        await WaitUntilAsync(() => !statuses.IsEmpty);
        cancel.Cancel();
        await run;
        Assert.True(child.StopRequested);
        Assert.True(child.Disposed);
        Assert.Equal(SnapTapRuntimeState.Off, statuses.Last().State);
    }

    [Fact]
    public async Task CancellationTimeout_RetainsHandleUntilActualExit()
    {
        var child = new Child { ExitOnStop = false };
        using var cancel = new CancellationTokenSource();
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        Task run = new SnapTapProcessCoordinator(new Preference(true), () => child, statuses.Enqueue).RunAsync(cancel.Token);
        cancel.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(child.StopRequested);
        Assert.False(child.Disposed);
        Assert.Equal(SnapTapRuntimeState.Failed, statuses.Last().State);
        Assert.Equal("StopTimedOut", statuses.Last().Failure?.Reason);
        child.Exit(0);
        await WaitUntilAsync(() => child.Disposed);
    }

    [Fact]
    public async Task CancellationStopFailurePreservesFreshTerminalDiagnostic()
    {
        var child = new Child { StopExitCode = 99 };
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        SnapTapRuntimeStatus? terminal = null;
        var failure = new DiagnosticFailure(
            "SnapTapSupervisor", "ReconcileHooks", "Reconciliation", "RecoveryRequired", true);
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(new Preference(true), () =>
        {
            terminal = new(SnapTapRuntimeState.RecoveryRequired, DateTimeOffset.UtcNow,
                child.Id, Failure: failure);
            return child;
        }, statuses.Enqueue, () => terminal).RunAsync(cancel.Token);
        await WaitUntilAsync(() => terminal is not null);

        cancel.Cancel();
        await run;

        Assert.Equal(failure, statuses.Last().Failure);
        Assert.Equal(SnapTapRuntimeState.RecoveryRequired, statuses.Last().State);
    }

    [Fact]
    public async Task NonzeroExitDuringStop_RemainsFailed()
    {
        var source = new Preference(true);
        var child = new Child { StopExitCode = 99 };
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(source, () => child, statuses.Enqueue).RunAsync(cancel.Token);
        source.Set(false);
        await WaitUntilAsync(() => child.Disposed);
        cancel.Cancel();
        await run;
        Assert.Equal(SnapTapRuntimeState.Failed, statuses.Last().State);
    }

    [Fact]
    public async Task StartupFailure_IsLatchedUntilOffOn()
    {
        var source = new Preference(true);
        int launches = 0;
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(source, () => { launches++; throw new IOException(); }, statuses.Enqueue).RunAsync(cancel.Token);
        await WaitUntilAsync(() => statuses.Last().State == SnapTapRuntimeState.Failed);
        source.Set(true);
        source.Set(false);
        source.Set(true);
        await WaitUntilAsync(() => launches == 2);
        cancel.Cancel();
        await run;
        Assert.Equal(2, launches);
    }

    [Fact]
    public async Task ExitObservationFailure_DoesNotCompleteSessionTaskBeforeCancellation()
    {
        var child = new Child { FailExitObservation = true };
        var source = new Preference(true);
        int launches = 0;
        using var cancel = new CancellationTokenSource();
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        Task run = new SnapTapProcessCoordinator(source, () => { launches++; return child; }, statuses.Enqueue).RunAsync(cancel.Token);
        await WaitUntilAsync(() => child.StopRequested);
        Assert.False(run.IsCompleted);
        Assert.True(child.Disposed);
        Assert.Equal(SnapTapRuntimeState.Failed, statuses.Last().State);
        Assert.Equal("ExitObservationFailed", statuses.Last().Failure?.Reason);
        Assert.Equal(nameof(IOException), statuses.Last().Failure?.ExceptionType);
        source.Set(false);
        source.Set(true);
        Assert.Equal(1, launches);
        cancel.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task StatusSinkFailure_PreventsInvisibleLaunch()
    {
        int launches = 0;
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(new Preference(true), () => { launches++; return new Child(); }, _ => throw new IOException()).RunAsync(cancel.Token);
        cancel.Cancel();
        await run;
        Assert.Equal(0, launches);
    }

    [Theory]
    [InlineData(3, SnapTapRuntimeState.Unavailable)]
    [InlineData(4, SnapTapRuntimeState.RecoveryRequired)]
    [InlineData(5, SnapTapRuntimeState.DriverMissing)]
    [InlineData(6, SnapTapRuntimeState.UnsupportedModel)]
    [InlineData(7, SnapTapRuntimeState.ManualDriverSetupRequired)]
    public async Task DriverExitCondition_IsPublishedAndNeverAutomaticallyRetried(int code, SnapTapRuntimeState expected)
    {
        var source = new Preference(true);
        var child = new Child();
        int launches = 0;
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(source, () => { launches++; return child; }, statuses.Enqueue).RunAsync(cancel.Token);
        await WaitUntilAsync(() => launches == 1);
        child.Exit(code);
        await WaitUntilAsync(() => statuses.Last().State == expected);
        source.Set(true);
        cancel.Cancel();
        await run;
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task UnexpectedExitPreservesFreshTerminalSupervisorFailure()
    {
        var source = new Preference(true);
        var child = new Child();
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        SnapTapRuntimeStatus? terminal = null;
        var failure = new DiagnosticFailure(
            "SnapTapSupervisor", "ReconcileHooks", "Reconciliation", "RecoveryRequired", true,
            Cause: new("SnapTapWorker", "HookSession", "HookMutation", "SetHookFailed", true));
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(source, () =>
        {
            terminal = new(SnapTapRuntimeState.RecoveryRequired, DateTimeOffset.UtcNow,
                child.Id, Failure: failure);
            return child;
        }, statuses.Enqueue, () => terminal).RunAsync(cancel.Token);
        await WaitUntilAsync(() => terminal is not null);

        child.Exit(4);

        await WaitUntilAsync(() => statuses.Last().Failure is not null);
        Assert.Equal(failure, statuses.Last().Failure);
        Assert.Equal(SnapTapRuntimeState.RecoveryRequired, statuses.Last().State);
        cancel.Cancel();
        await run;
    }

    [Fact]
    public void TerminalStatusMatchRejectsStaleWrongProcessAndNonterminalRecords()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var failure = new DiagnosticFailure("SnapTapSupervisor", "Run", "WorkerExit", "UnexpectedExit");
        var status = new SnapTapRuntimeStatus(
            SnapTapRuntimeState.Failed, startedAt.AddMilliseconds(1), 123, Failure: failure);

        Assert.Same(status, SnapTapProcessCoordinator.MatchingTerminalStatus(status, 123, startedAt));
        Assert.Null(SnapTapProcessCoordinator.MatchingTerminalStatus(status, 124, startedAt));
        Assert.Null(SnapTapProcessCoordinator.MatchingTerminalStatus(
            status with { UpdatedAt = startedAt.AddMilliseconds(-1) }, 123, startedAt));
        Assert.Null(SnapTapProcessCoordinator.MatchingTerminalStatus(
            status with { State = SnapTapRuntimeState.Active, Failure = null }, 123, startedAt));
        Assert.Null(SnapTapProcessCoordinator.MatchingTerminalStatus(
            status with { Failure = null }, 123, startedAt));
    }

    [Fact]
    public async Task BackendSwitchWaitsForOldHelperExitBeforeStartingSelectedOwner()
    {
        var source = new BackendPreference();
        var first = new Child { ExitOnStop = false };
        var second = new Child();
        var launches = new ConcurrentQueue<KeyboardInputBackend>();
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(source, () => throw new InvalidOperationException("legacy launch"),
            statuses.Enqueue, startBackendChild: backend =>
            {
                launches.Enqueue(backend);
                Assert.Equal(backend, statuses.Last().Backend);
                if (launches.Count == 1) { return first; }
                Assert.True(first.HasExited);
                Assert.True(first.Disposed);
                return second;
            }).RunAsync(cancel.Token);
        await WaitUntilAsync(() => launches.Count == 1);
        source.SetBackend(KeyboardInputBackend.WindowsHook);
        await WaitUntilAsync(() => first.StopRequested);
        Assert.Single(launches);
        first.Exit(0);
        await WaitUntilAsync(() => launches.Count == 2);
        Assert.Equal(new[] { KeyboardInputBackend.RazerDriver, KeyboardInputBackend.WindowsHook }, launches);
        cancel.Cancel();
        await run;
        Assert.True(second.Disposed);
        Assert.Equal(KeyboardInputBackend.WindowsHook, statuses.Last().Backend);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task BackendSwitchCanRetryNoWriteAdmissionRejection(int exitCode)
    {
        var source = new BackendPreference();
        var first = new Child();
        int launches = 0;
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(source, () => first, statuses.Enqueue,
            startBackendChild: _ => ++launches == 1 ? first : new Child()).RunAsync(cancel.Token);
        await WaitUntilAsync(() => launches == 1);
        first.Exit(exitCode);
        await WaitUntilAsync(() => statuses.Last().Failure is not null);
        source.SetBackend(KeyboardInputBackend.WindowsHook);
        await WaitUntilAsync(() => launches == 2);
        cancel.Cancel();
        await run;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task BackendSwitchDoesNotBypassFailedOrUncertainCleanup(int exitCode)
    {
        var source = new BackendPreference();
        var first = new Child { StopExitCode = exitCode };
        int launches = 0;
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(source, () => first, statuses.Enqueue,
            startBackendChild: _ => { launches++; return first; }).RunAsync(cancel.Token);
        await WaitUntilAsync(() => launches == 1);
        source.SetBackend(KeyboardInputBackend.WindowsHook);
        await WaitUntilAsync(() => first.Disposed && statuses.Last().Failure is not null);
        source.SetBackend(KeyboardInputBackend.RazerDriver);
        source.SetBackend(KeyboardInputBackend.WindowsHook);
        cancel.Cancel();
        await run;
        Assert.Equal(1, launches);
        Assert.Equal(KeyboardInputBackend.RazerDriver, statuses.Last().Backend);
    }

    [Fact]
    public async Task UnknownBackendPausesWithoutFallbackAndExplicitSelectionCanResume()
    {
        var source = new BackendPreference();
        source.SetBackend((KeyboardInputBackend)999);
        int launches = 0;
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(source, () => throw new InvalidOperationException("fallback"),
            statuses.Enqueue, startBackendChild: _ => { launches++; return new Child(); }).RunAsync(cancel.Token);
        await WaitUntilAsync(() => !statuses.IsEmpty);
        Assert.Equal(SnapTapRuntimeState.Unavailable, statuses.Last().State);
        Assert.Equal(KeyboardInputBackend.Unknown, statuses.Last().Backend);
        Assert.Equal(0, launches);
        source.SetBackend(KeyboardInputBackend.WindowsHook);
        await WaitUntilAsync(() => launches == 1);
        cancel.Cancel();
        await run;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctFeaturesCoexistButSnapTapOwnershipTransfersOnlyAfterExit(bool functionRowOnly)
    {
        var source = new BackendPreference();
        if (functionRowOnly) { source.SetBackend(KeyboardInputBackend.WindowsHook); }
        using var windowsDemand = new WindowsSnapTapPreferenceSource(source);
        using var driverDemand = new RazerKeyboardDriverPreferenceSource();
        KeyboardDriverFeatures activeFeatures = functionRowOnly ? KeyboardDriverFeatures.FunctionRow : KeyboardDriverFeatures.SnapTap;
        driverDemand.SetFeatures(activeFeatures);
        var driverChild = new Child { ExitOnStop = functionRowOnly };
        var windowsChild = new Child();
        int windowsStarts = 0;
        SnapTapProcessCoordinator? driver = null;
        var windows = new SnapTapProcessCoordinator(windowsDemand, () => windowsChild, _ => { },
            startBackendChild: _ =>
            {
                Assert.True(functionRowOnly || driver?.HasChild != true);
                windowsStarts++;
                return windowsChild;
            }, canStartChild: () => driver?.CanTransferOwnership != false);
        driver = new SnapTapProcessCoordinator(driverDemand, () => driverChild, _ => { },
            canStartChild: () => activeFeatures == KeyboardDriverFeatures.FunctionRow || windows.CanTransferOwnership,
            ownsSharedInput: () => activeFeatures != KeyboardDriverFeatures.FunctionRow);
        using var cancel = new CancellationTokenSource();
        Task windowsRun = windows.RunAsync(cancel.Token);
        Task driverRun = driver.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => driver.HasChild);
            if (functionRowOnly)
            {
                await WaitUntilAsync(() => windows.HasChild);
                Assert.False(driverChild.StopRequested);
                Assert.Equal(1, windowsStarts);
            }
            else
            {
                source.SetBackend(KeyboardInputBackend.WindowsHook);
                driverDemand.SetFeatures(KeyboardDriverFeatures.None);
                await WaitUntilAsync(() => driverChild.StopRequested);
                Assert.Equal(0, windowsStarts);
                driverChild.Exit(0);
                await WaitUntilAsync(() => windowsStarts == 1);
                Assert.False(driver.HasChild);
            }
        }
        finally
        {
            driverChild.Exit(0);
            cancel.Cancel();
            await Task.WhenAll(windowsRun, driverRun);
        }
    }

    [Fact]
    public async Task LaunchGateDrainsExistingHoldWithoutNewPreferenceEvent()
    {
        bool released = false;
        int starts = 0;
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        Task run = new SnapTapProcessCoordinator(new Preference(true), () => { starts++; return new Child(); },
            statuses.Enqueue, canStartChild: () => Volatile.Read(ref released)).RunAsync(cancel.Token);
        await WaitUntilAsync(() => !statuses.IsEmpty);
        Assert.Equal(SnapTapRuntimeState.WaitingForRelease, statuses.Last().State);
        Assert.Equal(0, starts);
        Volatile.Write(ref released, true);
        await WaitUntilAsync(() => starts == 1);
        cancel.Cancel();
        await run;
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 3)]
    [InlineData(false, 3)]
    public async Task ProductionDualOwnerGatesPreserveUncertaintyAcrossAdapterOffGeneration(bool windowsFirst, int exitCode)
    {
        var source = new BackendPreference();
        if (windowsFirst) { source.SetBackend(KeyboardInputBackend.WindowsHook); }
        using var windowsDemand = new WindowsSnapTapPreferenceSource(source);
        using var driverDemand = new RazerKeyboardDriverPreferenceSource();
        driverDemand.SetFeatures(windowsFirst ? KeyboardDriverFeatures.None : KeyboardDriverFeatures.SnapTap);
        var first = new Child();
        int nextStarts = 0;
        int nextGateChecks = 0;
        SnapTapProcessCoordinator? driver = null;
        var windows = new SnapTapProcessCoordinator(windowsDemand, () => first, _ => { },
            startBackendChild: _ =>
            {
                if (windowsFirst) { return first; }
                Interlocked.Increment(ref nextStarts);
                return new Child();
            }, canStartChild: () =>
            {
                if (!windowsFirst) { Interlocked.Increment(ref nextGateChecks); }
                return driver?.CanTransferOwnership != false;
            });
        driver = new SnapTapProcessCoordinator(driverDemand, () => first, _ => { },
            startBackendChild: _ =>
            {
                if (!windowsFirst) { return first; }
                Interlocked.Increment(ref nextStarts);
                return new Child();
            }, canStartChild: () =>
            {
                if (windowsFirst) { Interlocked.Increment(ref nextGateChecks); }
                return windows.CanTransferOwnership;
            });
        using var cancel = new CancellationTokenSource();
        Task windowsRun = windows.RunAsync(cancel.Token);
        Task driverRun = driver.RunAsync(cancel.Token);
        try
        {
            SnapTapProcessCoordinator original = windowsFirst ? windows : driver;
            await WaitUntilAsync(() => original.HasChild);
            first.Exit(exitCode);
            await WaitUntilAsync(() => !original.HasChild);
            Assert.Equal(exitCode == 3, original.CanTransferOwnership);
            source.SetBackend(windowsFirst ? KeyboardInputBackend.RazerDriver : KeyboardInputBackend.WindowsHook);
            driverDemand.SetFeatures(windowsFirst ? KeyboardDriverFeatures.SnapTap : KeyboardDriverFeatures.None);
            await WaitUntilAsync(() => Volatile.Read(ref nextGateChecks) != 0);
            if (exitCode == 3) { await WaitUntilAsync(() => Volatile.Read(ref nextStarts) == 1); }
            else { Assert.Equal(0, Volatile.Read(ref nextStarts)); }
        }
        finally { cancel.Cancel(); await Task.WhenAll(windowsRun, driverRun); }
    }

    [Fact]
    public async Task FunctionRowOnlyOwnerRemainsIndependentOfWindowsAdRecoveryBarrier()
    {
        var source = new BackendPreference();
        source.SetBackend(KeyboardInputBackend.WindowsHook);
        using var windowsDemand = new WindowsSnapTapPreferenceSource(source);
        using var driverDemand = new RazerKeyboardDriverPreferenceSource();
        var failed = new Child();
        var windows = new SnapTapProcessCoordinator(windowsDemand, () => failed, _ => { },
            startBackendChild: _ => failed);
        var driver = new SnapTapProcessCoordinator(driverDemand, () => new Child(), _ => { },
            canStartChild: () => (driverDemand.RequestedFeatures & KeyboardDriverFeatures.SnapTap) == 0 || windows.CanTransferOwnership,
            ownsSharedInput: () => false);
        using var cancel = new CancellationTokenSource();
        Task windowsRun = windows.RunAsync(cancel.Token);
        Task driverRun = driver.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => windows.HasChild);
            failed.Exit(1);
            await WaitUntilAsync(() => !windows.HasChild);
            Assert.False(windows.CanTransferOwnership);
            // This production condition bypasses the A/D barrier for Fn-only demand.
            driverDemand.SetFeatures(KeyboardDriverFeatures.FunctionRow);
            await WaitUntilAsync(() => driver.HasChild);
            Assert.True(driver.CanTransferOwnership);
        }
        finally { cancel.Cancel(); await Task.WhenAll(windowsRun, driverRun); }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    public async Task DriverFunctionRowTransferRequiresCleanOrNoWriteExitWithoutBlockingAd(int exitCode, bool allowed)
    {
        var source = new Preference(true);
        var child = new Child { StopExitCode = exitCode };
        using var cancel = new CancellationTokenSource();
        var driver = new SnapTapProcessCoordinator(source, () => child, _ => { },
            ownsSharedInput: () => false, ownsFunctionRowInput: () => true);
        Task run = driver.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => driver.HasChild);
            Assert.False(driver.CanTransferFunctionRowOwnership);
            Assert.True(driver.CanTransferOwnership);
            Assert.Equal(KeyboardInputBackend.Unknown, WindowsFunctionRowOwnershipGuard.SelectBackend(
                KeyboardInputBackend.WindowsHook, driver.CanTransferFunctionRowOwnership, null));
            source.Set(false);
            await WaitUntilAsync(() => !driver.HasChild);
            Assert.Equal(allowed, driver.CanTransferFunctionRowOwnership);
            Assert.True(driver.CanTransferOwnership);
            Assert.Equal(allowed ? KeyboardInputBackend.WindowsHook : KeyboardInputBackend.Unknown,
                WindowsFunctionRowOwnershipGuard.SelectBackend(KeyboardInputBackend.WindowsHook,
                    driver.CanTransferFunctionRowOwnership, null));
        }
        finally { cancel.Cancel(); await run; }
    }

    [Fact]
    public async Task PreparedScopeBindsPublicationOwnershipAndChildDespitePreferenceRace()
    {
        using var demand = new RazerKeyboardDriverPreferenceSource();
        demand.SetFeatures(KeyboardDriverFeatures.FunctionRow);
        KeyboardDriverFeatures prepared = KeyboardDriverFeatures.None;
        KeyboardDriverFeatures launched = KeyboardDriverFeatures.None;
        var child = new Child { ExitOnStop = false };
        using var cancel = new CancellationTokenSource();
        var coordinator = new SnapTapProcessCoordinator(demand, () => child, status =>
        {
            if (status.State == SnapTapRuntimeState.Starting) { Assert.Equal(KeyboardDriverFeatures.FunctionRow, prepared); }
        }, startBackendChild: _ => { launched = prepared; return child; },
            ownsSharedInput: () =>
            {
                demand.SetFeatures(KeyboardDriverFeatures.SnapTap); // changed after prepare, before launch
                return (prepared & KeyboardDriverFeatures.SnapTap) != 0;
            },
            ownsFunctionRowInput: () => (prepared & KeyboardDriverFeatures.FunctionRow) != 0,
            prepareChild: () => prepared = demand.RequestedFeatures);
        Task run = coordinator.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => coordinator.HasChild);
            Assert.Equal(KeyboardDriverFeatures.FunctionRow, launched);
            Assert.Equal(KeyboardDriverFeatures.SnapTap, demand.RequestedFeatures);
            child.Exit(1);
            await WaitUntilAsync(() => !coordinator.HasChild);
            Assert.False(coordinator.CanTransferFunctionRowOwnership);
            Assert.True(coordinator.CanTransferOwnership);
        }
        finally { child.Exit(1); cancel.Cancel(); await run; }
    }

    [Fact]
    public async Task CleanTransferPublishesFinalOffBeforeNotificationAndNeverClobbersNewOwner()
    {
        var firstPreference = new Preference(true);
        var firstChild = new Child();
        var secondChild = new Child();
        SnapTapRuntimeStatus? visible = null;
        using var firstCancel = new CancellationTokenSource();
        using var secondCancel = new CancellationTokenSource();
        Task? secondRun = null;
        var secondPreference = new BackendPreference();
        secondPreference.SetBackend(KeyboardInputBackend.WindowsHook);
        var first = new SnapTapProcessCoordinator(firstPreference, () => firstChild,
            status => visible = status, publicationFeatures: () => KeyboardDriverFeatures.SnapTap);
        var second = new SnapTapProcessCoordinator(secondPreference, () => secondChild, status => visible = status,
            startBackendChild: _ =>
            {
                visible = new(SnapTapRuntimeState.Active, DateTimeOffset.UtcNow, secondChild.Id)
                { Backend = KeyboardInputBackend.WindowsHook };
                return secondChild;
            }, canStartChild: () => first.CanTransferOwnership);
        first.OwnershipChanged += clean =>
        {
            if (!clean) { return; }
            Assert.False(first.HasChild);
            Assert.True(first.CanTransferOwnership);
            Assert.Equal(SnapTapRuntimeState.Off, visible?.State);
            secondRun = second.RunAsync(secondCancel.Token);
            // The old coordinator must finish its current disabled iteration
            // without writing Off over the new owner's synchronous Active status.
            firstCancel.Cancel();
        };
        Task firstRun = first.RunAsync(firstCancel.Token);
        try
        {
            await WaitUntilAsync(() => first.HasChild);
            firstPreference.Set(false);
            await firstRun;
            Assert.NotNull(secondRun);
            Assert.Equal(SnapTapRuntimeState.Active, visible?.State);
            Assert.Equal(KeyboardInputBackend.WindowsHook, visible?.Backend);
        }
        finally
        {
            firstCancel.Cancel();
            secondCancel.Cancel();
            await firstRun;
            if (secondRun is not null) { await secondRun; }
        }
    }

    [Fact]
    public async Task StableBlockedLaunchWritesOncePerStateOwnerAndScope()
    {
        int attempts = 0;
        KeyboardDriverFeatures scope = KeyboardDriverFeatures.FunctionRow;
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        using var cancel = new CancellationTokenSource();
        var coordinator = new SnapTapProcessCoordinator(new Preference(true),
            () => throw new InvalidOperationException("blocked launch"), statuses.Enqueue,
            canStartChild: () => false, prepareChild: () => Interlocked.Increment(ref attempts),
            publicationFeatures: () => scope);
        Task run = coordinator.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 5);
            Assert.Single(statuses);
            Assert.Equal(SnapTapRuntimeState.WaitingForRelease, statuses.Single().State);
            scope |= KeyboardDriverFeatures.SnapTap;
            int before = Volatile.Read(ref attempts);
            await WaitUntilAsync(() => Volatile.Read(ref attempts) >= before + 3);
            Assert.Equal(2, statuses.Count);
        }
        finally { cancel.Cancel(); await run; }
    }

    [Fact]
    public async Task NormalDriverFnHandoffWaitsWithoutClaimingRecoveryAndRefreshesOnCleanCompletion()
    {
        var source = new Preference(true);
        var child = new Child { ExitOnStop = false };
        using var cancel = new CancellationTokenSource();
        var coordinator = new SnapTapProcessCoordinator(source, () => child, _ => { },
            ownsSharedInput: () => false, ownsFunctionRowInput: () => true);
        bool cleanNotification = false;
        coordinator.OwnershipChanged += clean => { if (clean) { cleanNotification = true; } };
        Task run = coordinator.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => coordinator.HasChild);
            var liveJournal = new DiagnosticFailure("FunctionRowWindowsOwnership", "InspectStartup", "OwnershipJournal", "RecoveryRequired", true);
            SnapTapRuntimeStatus? pending = coordinator.FunctionRowWindowsTransferStatus(KeyboardInputBackend.WindowsHook, liveJournal);
            Assert.Equal(SnapTapRuntimeState.WaitingForRelease, pending?.State);
            Assert.Null(pending?.Failure);
            source.Set(false);
            await WaitUntilAsync(() => child.StopRequested);
            Assert.Equal(SnapTapRuntimeState.WaitingForRelease,
                coordinator.FunctionRowWindowsTransferStatus(KeyboardInputBackend.WindowsHook, liveJournal)?.State);
            child.Exit(0);
            await WaitUntilAsync(() => cleanNotification);
            Assert.Null(coordinator.FunctionRowWindowsTransferStatus(KeyboardInputBackend.WindowsHook, null));
        }
        finally { child.Exit(0); cancel.Cancel(); await run; }
    }

    [Fact]
    public async Task DisabledHostPreferenceIsNoWriteAdmissionRatherThanCleanLiveOwnerCompletion()
    {
        int exitCode = SnapTapInputHost.PreferenceRejection(new UiInputPreferences(), KeyboardInputBackend.WindowsHook,
            KeyboardDriverFeatures.SnapTap)!.Value;
        Assert.Equal(3, exitCode);
        var child = new Child();
        bool cleanCompletion = false;
        var coordinator = new SnapTapProcessCoordinator(new Preference(true), () => child, _ => { });
        coordinator.OwnershipChanged += clean => cleanCompletion |= clean;
        using var cancel = new CancellationTokenSource();
        Task run = coordinator.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => coordinator.HasChild);
            child.Exit(exitCode);
            await WaitUntilAsync(() => !coordinator.HasChild);
            Assert.True(coordinator.CanTransferOwnership);
            Assert.False(cleanCompletion);
        }
        finally { cancel.Cancel(); await run; }
    }

    [Fact]
    public async Task RetiredAdmissionFailureCannotPublishIdleOffOverNewOwner()
    {
        var firstPreference = new Preference(true);
        var firstChild = new Child();
        SnapTapRuntimeStatus? visible = null;
        bool selected = true;
        int idleChecks = 0;
        using var firstCancel = new CancellationTokenSource();
        using var secondCancel = new CancellationTokenSource();
        Task? secondRun = null;
        var nextPreference = new BackendPreference();
        nextPreference.SetBackend(KeyboardInputBackend.WindowsHook);
        var first = new SnapTapProcessCoordinator(firstPreference, () => firstChild, status => visible = status,
            canPublishIdleStatus: () => { Interlocked.Increment(ref idleChecks); return selected; });
        var second = new SnapTapProcessCoordinator(nextPreference, () => new Child(), status => visible = status,
            startBackendChild: _ =>
            {
                visible = new(SnapTapRuntimeState.Active, DateTimeOffset.UtcNow)
                { Backend = KeyboardInputBackend.WindowsHook };
                return new Child();
            }, canStartChild: () => first.CanTransferOwnership);
        first.OwnershipChanged += _ =>
        {
            if (first.HasChild) { return; }
            selected = false;
            secondRun = second.RunAsync(secondCancel.Token);
            firstPreference.Set(false);
        };
        Task firstRun = first.RunAsync(firstCancel.Token);
        try
        {
            await WaitUntilAsync(() => first.HasChild);
            firstChild.Exit(3);
            await WaitUntilAsync(() => Volatile.Read(ref idleChecks) != 0);
            firstCancel.Cancel();
            await firstRun;
            Assert.Equal(SnapTapRuntimeState.Active, visible?.State);
            Assert.Equal(KeyboardInputBackend.WindowsHook, visible?.Backend);
        }
        finally
        {
            firstCancel.Cancel();
            secondCancel.Cancel();
            await firstRun;
            if (secondRun is not null) { await secondRun; }
        }
    }

    private sealed class BackendPreference : ISnapTapBackendPreferenceSource
    {
        public bool SnapTapEnabled => true;
        public event Action<bool>? SnapTapEnabledChanged { add { } remove { } }
        public KeyboardInputBackend SnapTapBackend { get; private set; } = KeyboardInputBackend.RazerDriver;
        public event Action<KeyboardInputBackend>? SnapTapBackendChanged;
        public void SetBackend(KeyboardInputBackend backend)
        {
            SnapTapBackend = backend;
            SnapTapBackendChanged?.Invoke(backend);
        }
    }

    private sealed class Preference(bool initial = false) : ISnapTapPreferenceSource
    {
        public bool SnapTapEnabled { get; private set; } = initial;
        public event Action<bool>? SnapTapEnabledChanged;
        public void Set(bool value) { SnapTapEnabled = value; SnapTapEnabledChanged?.Invoke(value); }
    }

    private sealed class Child : ISnapTapChildProcess
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 123;
        public bool HasExited => completion.Task.IsCompleted;
        public int ExitCode { get; private set; }
        public bool ExitOnStop { get; init; } = true;
        public int StopExitCode { get; init; }
        public bool FailExitObservation { get; init; }
        public bool StopRequested { get; private set; }
        public bool Disposed { get; private set; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => FailExitObservation
            ? Task.FromException(new IOException("Exit observation failed."))
            : completion.Task.WaitAsync(cancellationToken);
        public void RequestStop()
        {
            StopRequested = true;
            if (ExitOnStop)
            {
                Exit(StopExitCode);
            }
        }
        public void Exit(int code) { ExitCode = code; completion.TrySetResult(); }
        public void Dispose() => Disposed = true;
    }
}
