using System.Collections.Concurrent;
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
        child.Exit(0);
        await WaitUntilAsync(() => child.Disposed);
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
        var child = new Child { ExitOnStop = false, FailExitObservation = true };
        var source = new Preference(true);
        int launches = 0;
        using var cancel = new CancellationTokenSource();
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        Task run = new SnapTapProcessCoordinator(source, () => { launches++; return child; }, statuses.Enqueue).RunAsync(cancel.Token);
        await WaitUntilAsync(() => child.StopRequested);
        Assert.False(run.IsCompleted);
        Assert.False(child.Disposed);
        Assert.Equal(SnapTapRuntimeState.Failed, statuses.Last().State);
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
