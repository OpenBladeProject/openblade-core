using Microsoft.Extensions.Hosting;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceShutdownSignalTests
{
    [Fact]
    public async Task RequestShutdownAfterResponse_StopsHostOnceAfterGracePeriod()
    {
        var lifetime = new RecordingHostApplicationLifetime();
        var signal = new HostServiceShutdownSignal(lifetime);

        signal.RequestShutdownAfterResponse();
        signal.RequestShutdownAfterResponse();

        await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
