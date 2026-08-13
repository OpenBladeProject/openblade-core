namespace OpenBlade.SessionAgent.Tests;

public sealed class CoreAudioSubscriptionShutdownTests
{
    [Fact]
    public void CompleteOrDefer_CompletedWorkerCancelsUnusedTimeout()
    {
        var time = new TrackingTimeProvider();
        int releases = 0;

        CoreAudioSubscriptionShutdown.CompleteOrDefer(
            Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            time,
            () => releases++);

        Assert.Equal(1, releases);
        Assert.True(time.TimerDisposed);
    }

    [Fact]
    public void CompleteOrDefer_TimeoutDefersResourcesUntilWorkerCompletes()
    {
        var worker = new TaskCompletionSource();
        int releases = 0;

        CoreAudioSubscriptionShutdown.CompleteOrDefer(
            worker.Task,
            Task.CompletedTask,
            () => releases++);

        Assert.Equal(0, releases);

        worker.SetResult();

        Assert.Equal(1, releases);
    }

    [Fact]
    public void CompleteOrDefer_CompletedWorkerReleasesResourcesBeforeReturning()
    {
        var timeout = new TaskCompletionSource();
        int releases = 0;

        CoreAudioSubscriptionShutdown.CompleteOrDefer(
            Task.CompletedTask,
            timeout.Task,
            () => releases++);

        Assert.Equal(1, releases);
    }

    [Fact]
    public void CompleteOrDefer_FaultAfterTimeoutStillReleasesResources()
    {
        var worker = new TaskCompletionSource();
        int releases = 0;

        CoreAudioSubscriptionShutdown.CompleteOrDefer(
            worker.Task,
            Task.CompletedTask,
            () => releases++);

        worker.SetException(new InvalidOperationException("COM activation failed."));

        Assert.Equal(1, releases);
    }

    [Fact]
    public void CompleteOrDefer_ImmediateFaultReleasesResourcesAndPropagates()
    {
        var timeout = new TaskCompletionSource();
        int releases = 0;
        var failure = new InvalidOperationException("COM activation failed.");

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            CoreAudioSubscriptionShutdown.CompleteOrDefer(
                Task.FromException(failure),
                timeout.Task,
                () => releases++));

        Assert.Same(failure, thrown);
        Assert.Equal(1, releases);
    }

    private sealed class TrackingTimeProvider : TimeProvider
    {
        public bool TimerDisposed { get; private set; }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => new TrackingTimer(this);

        private sealed class TrackingTimer(TrackingTimeProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => owner.TimerDisposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
