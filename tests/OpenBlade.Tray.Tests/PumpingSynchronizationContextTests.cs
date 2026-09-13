using Xunit.Sdk;

namespace OpenBlade.Tray.Tests;

public sealed class PumpingSynchronizationContextTests
{
    [Fact]
    public void Run_TimeoutAllowsLatePostsAndCompletionSignalsAfterDisposal()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        PumpingSynchronizationContext? capturedContext = null;
        var completion = new TaskCompletionSource();

        TrueException exception = Assert.Throws<TrueException>(() =>
            PumpingSynchronizationContext.Run(
                context =>
                {
                    capturedContext = context;
                    return completion.Task;
                },
                TimeSpan.Zero));

        Assert.Contains("Timed out waiting for work posted", exception.Message);
        Assert.Same(previous, SynchronizationContext.Current);
        Assert.NotNull(capturedContext);
        capturedContext.Post(_ => throw new InvalidOperationException("Late work ran."), null);
        capturedContext.SignalWork();
        completion.SetResult();
        capturedContext.Dispose();
    }

    [Fact]
    public async Task Send_TimeoutAllowsTheQueuedCallbackToCompleteLater()
    {
        bool callbackRan = false;
        using var context = new PumpingSynchronizationContext(TimeSpan.Zero);

        TrueException exception = await Assert.ThrowsAsync<TrueException>(() =>
            Task.Factory.StartNew(
                () => context.Send(_ => callbackRan = true, null),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default));

        Assert.Contains("Timed out waiting for synchronous context work", exception.Message);
        Assert.False(callbackRan);
        Assert.True(context.TryRunOne());
        Assert.True(callbackRan);
    }
}
