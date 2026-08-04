using Microsoft.Extensions.Hosting;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceShutdownSignalTests
{
    [Fact]
    public async Task RequestShutdownAfterResponse_StopsHostOnceAfterGracePeriod()
    {
        var lifetime = new RecordingHostApplicationLifetime();
        var delayRequested = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int delayCalls = 0;
        var signal = new HostServiceShutdownSignal(
            lifetime,
            gracePeriod =>
            {
                Interlocked.Increment(ref delayCalls);
                delayRequested.TrySetResult(gracePeriod);
                return releaseDelay.Task;
            });

        signal.RequestShutdownAfterResponse();
        signal.RequestShutdownAfterResponse();

        TimeSpan requestedGracePeriod = await delayRequested.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.Equal(HostServiceShutdownSignal.ResponseGracePeriod, requestedGracePeriod);
        Assert.Equal(1, Volatile.Read(ref delayCalls));
        Assert.Equal(0, lifetime.StopCalls);

        releaseDelay.TrySetResult();
        await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, lifetime.StopCalls);
    }

    private sealed class RecordingHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public TaskCompletionSource StopRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCalls { get; private set; }

        public CancellationToken ApplicationStarted => started.Token;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => stopped.Token;

        public void StopApplication()
        {
            StopCalls++;
            stopping.Cancel();
            StopRequested.TrySetResult();
        }
    }
}
