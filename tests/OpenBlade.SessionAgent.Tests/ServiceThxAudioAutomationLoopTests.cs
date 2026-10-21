using System.Runtime.CompilerServices;
using OpenBlade.Contracts;
using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceThxAudioAutomationLoopTests
{
    [Fact]
    public async Task ForegroundFailurePropagatesToProcessSupervisor()
    {
        await using var coordinator = new ThxAudioAutomationCoordinator(new NoOpController());
        await using var reporter = new ApplicationProfileReporter(
            (_, _) => Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Stored,
                "stored")));
        var loop = new ServiceThxAudioAutomationLoop(
            new WaitingStatusWatcher(),
            new FailingForegroundSource(),
            coordinator,
            reporter);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("foreground observation failed", exception.Message);
    }

    private sealed class WaitingStatusWatcher : IServiceStatusWatcher
    {
        public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield break;
        }
    }

    private sealed class FailingForegroundSource : IThxForegroundSource
    {
        public async IAsyncEnumerable<ThxForegroundObservation> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return await Task.FromException<ThxForegroundObservation>(
                    new InvalidOperationException("foreground observation failed"))
                .ConfigureAwait(false);
        }
    }

    private sealed class NoOpController : IThxAudioAutomationController
    {
        public Task<ThxAudioOperationStatus> ApplyAsync(
            ThxAudioAutomationTarget target,
            CancellationToken cancellationToken) =>
            Task.FromResult(ThxAudioOperationStatus.Succeeded);
    }
}
