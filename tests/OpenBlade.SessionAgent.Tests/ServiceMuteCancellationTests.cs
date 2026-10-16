using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceMuteCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GracefulStreamCompletionAfterCancellationIsNormal(bool microphone)
    {
        var client = new GracefulClient();
        var source = new GracefulMuteSource();
        var errors = new ConcurrentQueue<Exception>();
        using var cancellation = new CancellationTokenSource();
        Task running = Run(microphone, client, source, errors.Enqueue, cancellation.Token);
        await Task.WhenAll(client.Started.Task, source.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedStatusCompletionRemainsFatal(bool microphone)
    {
        var client = new GracefulClient();
        var source = new GracefulMuteSource();
        using var cancellation = new CancellationTokenSource();
        Task running = Run(microphone, client, source, _ => { }, cancellation.Token);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        client.Complete.TrySetResult();
        IOException error = await Assert.ThrowsAsync<IOException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("status subscription ended unexpectedly", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedMuteCompletionStillReportsFailure(bool microphone)
    {
        var client = new GracefulClient();
        var source = new GracefulMuteSource();
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task running = Run(microphone, client, source, e => observed.TrySetResult(e), cancellation.Token);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.Complete.TrySetResult();
        Exception error = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<IOException>(error);
        Assert.Contains("mute subscription ended unexpectedly", error.Message, StringComparison.Ordinal);
        await cancellation.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task Run(bool microphone, GracefulClient client, GracefulMuteSource source,
        Action<Exception> errors, CancellationToken token) => microphone
        ? new OpenBlade.SessionAgent.ServiceMicrophoneMuteIndicatorLoop(client, source, errors).RunAsync(token)
        : new OpenBlade.SessionAgent.ServicePlaybackMuteIndicatorLoop(client, source, errors).RunAsync(token);

    private sealed class GracefulClient : OpenBlade.Testing.UnavailableServiceClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [EnumeratorCancellation] CancellationToken token)
        {
            using var registration = token.Register(() => Complete.TrySetResult());
            Started.TrySetResult();
            await Complete.Task;
            yield break;
        }
    }

    private sealed class GracefulMuteSource : OpenBlade.SessionAgent.IPlaybackMuteStateSource,
        OpenBlade.SessionAgent.IMicrophoneMuteStateSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<bool?> WatchMutedAsync([EnumeratorCancellation] CancellationToken token)
        {
            using var registration = token.Register(() => Complete.TrySetResult());
            Started.TrySetResult();
            await Complete.Task;
            yield break;
        }
    }
}
