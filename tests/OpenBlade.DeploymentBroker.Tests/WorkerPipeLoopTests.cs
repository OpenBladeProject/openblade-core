using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class WorkerPipeLoopTests
{
    [Fact]
    public async Task PipeFailure_WaitsBeforeRetryAndCancellationEndsCleanly()
    {
        using CancellationTokenSource stopping = new();
        int serveAttempts = 0;
        int loggedFailures = 0;
        List<TimeSpan> delays = [];

        await Worker.RunPipeLoopAsync(
            _ =>
            {
                serveAttempts++;
                throw new IOException("persistent pipe failure");
            },
            (delay, cancellationToken) =>
            {
                delays.Add(delay);
                stopping.Cancel();
                return Task.FromCanceled(cancellationToken);
            },
            _ => loggedFailures++,
            stopping.Token);

        Assert.Equal(1, serveAttempts);
        Assert.Equal(1, loggedFailures);
        Assert.Equal(Worker.PipeFailureRetryDelay, Assert.Single(delays));
        Assert.Equal(TimeSpan.FromSeconds(1), delays[0]);
    }

    [Fact]
    public async Task SuccessfulRequest_DoesNotDelayBeforeServingNextRequest()
    {
        using CancellationTokenSource stopping = new();
        int serveAttempts = 0;
        int delayCalls = 0;

        await Worker.RunPipeLoopAsync(
            cancellationToken =>
            {
                serveAttempts++;
                if (serveAttempts == 1)
                {
                    return Task.CompletedTask;
                }

                stopping.Cancel();
                return Task.FromCanceled(cancellationToken);
            },
            (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            _ => throw new Xunit.Sdk.XunitException("No pipe failure should be logged."),
            stopping.Token);

        Assert.Equal(2, serveAttempts);
        Assert.Equal(0, delayCalls);
    }
}
