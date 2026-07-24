using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task StartupGate_AwaitsConfigurationBeforeBackgroundLoops()
    {
        var configurationLoaded = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int backgroundStarts = 0;

        Task running = Worker.RunAfterSuccessfulStartupAsync(
            _ => configurationLoaded.Task,
            _ =>
            {
                Interlocked.Increment(ref backgroundStarts);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref backgroundStarts));
        configurationLoaded.SetResult(true);
        await running;
        Assert.Equal(1, Volatile.Read(ref backgroundStarts));
    }

    [Fact]
    public async Task ShutdownCleanup_UsesFreshTokenAfterHostTokenIsCancelled()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        bool cleanupRan = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Worker.RunWithShutdownCleanupAsync(
                token => Task.FromCanceled(token),
                token =>
                {
                    Assert.False(token.IsCancellationRequested);
                    cleanupRan = true;
                    return Task.CompletedTask;
                },
                stopping.Token));

        Assert.True(cleanupRan);
    }
}
