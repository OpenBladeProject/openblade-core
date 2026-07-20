using OpenBlade.Contracts;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class UiAccessInputAgentBridgeTests
{
    [Fact]
    public void ReadyBroker_OwnsInputWithoutCreatingLocalRawInputSource()
    {
        var fallback = new RecordingKeyboardSourceFactory();
        var session = new RecordingInputAgentSession(ready: true);
        var factory = new UiAccessInputKeyboardSourceFactory(
            fallback,
            new RecordingInputAgentLauncher(session),
            TimeSpan.FromMilliseconds(50));

        using IReactiveKeyboardSource source = factory.Create(_ => { }, _ => { });

        Assert.Same(session.Completion, source.Completion);
        Assert.Equal(TimeSpan.FromMilliseconds(50), session.ObservedTimeout);
        Assert.Equal(0, fallback.CreateCount);
        Assert.False(session.Disposed);

        source.Dispose();
        Assert.True(session.Disposed);
    }

    [Fact]
    public void MissingBroker_UsesExistingSessionLocalRawInputSource()
    {
        var fallback = new RecordingKeyboardSourceFactory();
        var factory = new UiAccessInputKeyboardSourceFactory(
            fallback,
            new RecordingInputAgentLauncher(null));

        using IReactiveKeyboardSource source = factory.Create(_ => { }, _ => { });

        Assert.Same(fallback.Source, source);
        Assert.Equal(1, fallback.CreateCount);
    }

    [Fact]
    public void BrokerWithoutReadySignal_IsStoppedBeforeFallbackTakesOwnership()
    {
        var fallback = new RecordingKeyboardSourceFactory();
        var session = new RecordingInputAgentSession(ready: false);
        var factory = new UiAccessInputKeyboardSourceFactory(
            fallback,
            new RecordingInputAgentLauncher(session),
            TimeSpan.FromMilliseconds(25));

        using IReactiveKeyboardSource source = factory.Create(_ => { }, _ => { });

        Assert.Same(fallback.Source, source);
        Assert.True(session.Disposed);
        Assert.Equal(1, fallback.CreateCount);
    }

    [Fact]
    public void BrokerLaunchFailure_IsReportedAndFallsBack()
    {
        var fallback = new RecordingKeyboardSourceFactory();
        var expected = new IOException("not signed");
        Exception? reported = null;
        var factory = new UiAccessInputKeyboardSourceFactory(
            fallback,
            new ThrowingInputAgentLauncher(expected),
            errorHandler: exception => reported = exception);

        using IReactiveKeyboardSource source = factory.Create(_ => { }, _ => { });

        Assert.Same(fallback.Source, source);
        Assert.Same(expected, reported);
        Assert.Equal(1, fallback.CreateCount);
    }

    private sealed class RecordingInputAgentLauncher(IUiAccessInputAgentSession? session)
        : IUiAccessInputAgentLauncher
    {
        public IUiAccessInputAgentSession? TryStart() => session;
    }

    private sealed class ThrowingInputAgentLauncher(Exception exception)
        : IUiAccessInputAgentLauncher
    {
        public IUiAccessInputAgentSession? TryStart() => throw exception;
    }

    private sealed class RecordingInputAgentSession(bool ready)
        : IUiAccessInputAgentSession
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => completion.Task;

        public TimeSpan? ObservedTimeout { get; private set; }

        public bool Disposed { get; private set; }

        public bool WaitUntilReady(TimeSpan timeout)
        {
            ObservedTimeout = timeout;
            return ready;
        }

        public void Dispose()
        {
            Disposed = true;
            completion.TrySetResult();
        }
    }

    private sealed class RecordingKeyboardSourceFactory : IReactiveKeyboardSourceFactory
    {
        public RecordingKeyboardSource Source { get; } = new();

        public int CreateCount { get; private set; }

        public IReactiveKeyboardSource Create(Action<ReactiveKeyPulse> publish)
        {
            CreateCount++;
            return Source;
        }

        public IReactiveKeyboardSource Create(
            Action<ReactiveKeyPulse> publish,
            Action<PerformanceShortcutSource> publishPerformanceShortcut)
        {
            CreateCount++;
            return Source;
        }
    }

    private sealed class RecordingKeyboardSource : IReactiveKeyboardSource
    {
        public Task Completion { get; } = Task.Delay(Timeout.InfiniteTimeSpan);

        public void Dispose()
        {
        }
    }
}
