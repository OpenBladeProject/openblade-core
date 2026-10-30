using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed partial class ServiceReactiveLightingLoopTests
{
    [Fact]
    public async Task PreferenceAndOwnershipRefreshPublishWithoutAnotherServiceSnapshot()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var statuses = new ConcurrentQueue<SnapTapRuntimeStatus>();
        SnapTapRuntimeStatus? blocked = new(SnapTapRuntimeState.WaitingForRelease, DateTimeOffset.UtcNow)
        { Backend = KeyboardInputBackend.WindowsHook };
        await using var loop = new ServiceReactiveLightingLoop(client, factory, new(
            ReadFunctionRowBackend: () => blocked is null ? KeyboardInputBackend.WindowsHook : KeyboardInputBackend.Unknown,
            ReadFunctionRowStatusOverride: () => blocked,
            PublishFunctionRowStatus: statuses.Enqueue));
        using var cancel = new CancellationTokenSource();
        Task run = loop.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => statuses.Any(status => status.State == SnapTapRuntimeState.WaitingForRelease));
            blocked = null;
            loop.InvalidateFunctionRowBackendStatus();
            Assert.Equal(SnapTapRuntimeState.Paused, statuses.Last().State);
            Assert.Equal(KeyboardInputBackend.WindowsHook, statuses.Last().Backend);
            blocked = new(SnapTapRuntimeState.RecoveryRequired, DateTimeOffset.UtcNow,
                Failure: new DiagnosticFailure("FunctionRowWindowsOwnership", "Transfer", "OwnershipJournal", "RecoveryRequired", true))
            { Backend = KeyboardInputBackend.WindowsHook };
            loop.InvalidateFunctionRowBackendStatus();
            Assert.Equal(SnapTapRuntimeState.RecoveryRequired, statuses.Last().State);
        }
        finally { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }

    [Fact]
    public async Task ConcurrentBackendStatusRefreshesSerializeTheStatusSink()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        int writers = 0;
        int overlaps = 0;
        await using var loop = new ServiceReactiveLightingLoop(client, factory, new(
            PublishFunctionRowStatus: _ =>
            {
                if (Interlocked.Increment(ref writers) != 1) { Interlocked.Increment(ref overlaps); }
                Thread.SpinWait(10000);
                Interlocked.Decrement(ref writers);
            }));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(loop.InvalidateFunctionRowBackendStatus)));
        Assert.Equal(0, overlaps);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrFailedSourceUsesFactoryOwnershipProof(bool provenClean)
    {
        var client = new WatchClient();
        var factory = new FailedOwnershipSourceFactory(provenClean);
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        Assert.Equal(provenClean, loop.CanTransferFunctionRowInput);
        using var cancel = new CancellationTokenSource();
        Task run = loop.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => factory.Attempts != 0);
            Assert.Equal(provenClean, loop.CanTransferFunctionRowInput);
        }
        finally { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }

    private sealed class FailedOwnershipSourceFactory(bool provenClean) : IReactiveKeyboardSourceFactory
    {
        public int Attempts;
        public bool CanTransferFunctionRowOwnership => provenClean;
        public IReactiveKeyboardSource Create(ReactiveKeyboardSourceOptions options)
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("Offline source creation failure.");
        }
    }

    [Fact]
    public async Task DisposedSourceWithAnUnreleasedKeyCannotTransferOwnership()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancel = new CancellationTokenSource();
        Task run = loop.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => factory.Last is not null);
            factory.Last!.HasActiveFunctionRowKeys = true;
            await loop.DisposeAsync();
            Assert.False(loop.CanTransferFunctionRowInput);
        }
        finally { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }
}
