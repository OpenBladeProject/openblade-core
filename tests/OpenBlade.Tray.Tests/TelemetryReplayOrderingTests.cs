using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task TelemetryReplay_SlowEarlierOpenCannotReplaceTheNewerReplay()
    {
        await using var viewModel = new MainViewModel(new FakeServiceClient(), new RecordingDispatcher());
        await viewModel.SetTelemetryEnabledAsync(true);
        var firstRead = new TaskCompletionSource<TelemetrySessionDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource<TelemetrySessionDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SetTelemetryReplayReaderForTests((path, _, _) => path == "first.csv" ? firstRead.Task : secondRead.Task);
        TelemetrySessionDocument first = ReplayOrderingDocument();
        TelemetrySessionDocument second = ReplayOrderingDocument();

        Task firstOpen = viewModel.OpenTelemetryReplayAsync("first.csv", CancellationToken.None);
        Task secondOpen = viewModel.OpenTelemetryReplayAsync("second.csv", CancellationToken.None);
        secondRead.TrySetResult(second);
        await secondOpen;
        firstRead.TrySetResult(first);
        await firstOpen;

        Assert.Same(second, viewModel.TelemetryReplaySession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TelemetryReplay_ReturnToLiveSupersedesAnInFlightOpen(bool replayAlreadyLoaded)
    {
        await using var viewModel = new MainViewModel(new FakeServiceClient(), new RecordingDispatcher());
        await viewModel.SetTelemetryEnabledAsync(true);
        if (replayAlreadyLoaded)
        {
            viewModel.SetTelemetryReplayReaderForTests((_, _, _) => Task.FromResult(ReplayOrderingDocument()));
            await viewModel.OpenTelemetryReplayAsync("loaded.csv", CancellationToken.None);
        }
        var pendingRead = new TaskCompletionSource<TelemetrySessionDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SetTelemetryReplayReaderForTests((_, _, _) => pendingRead.Task);

        Task pendingOpen = viewModel.OpenTelemetryReplayAsync("pending.csv", CancellationToken.None);
        viewModel.CloseTelemetryReplay();
        pendingRead.TrySetResult(ReplayOrderingDocument());
        await pendingOpen;

        Assert.Null(viewModel.TelemetryReplaySession);
        Assert.Equal(TelemetrySource.LiveService, viewModel.DisplayedTelemetrySource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TelemetryReplay_SupersededReadFailureCannotReplaceCurrentFeedback(bool returnToLive)
    {
        await using var viewModel = new MainViewModel(new FakeServiceClient(), new RecordingDispatcher());
        await viewModel.SetTelemetryEnabledAsync(true);
        var pendingRead = new TaskCompletionSource<TelemetrySessionDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        TelemetrySessionDocument current = ReplayOrderingDocument();
        viewModel.SetTelemetryReplayReaderForTests((path, _, _) => path == "pending.csv"
            ? pendingRead.Task : Task.FromResult(current));
        Task pendingOpen = viewModel.OpenTelemetryReplayAsync("pending.csv", CancellationToken.None);
        if (returnToLive)
        {
            viewModel.CloseTelemetryReplay();
        }
        else
        {
            await viewModel.OpenTelemetryReplayAsync("current.csv", CancellationToken.None);
        }

        pendingRead.TrySetException(new IOException("The old CSV could not be read."));
        await pendingOpen;

        Assert.Equal(returnToLive ? null : current, viewModel.TelemetryReplaySession);
        Assert.Empty(viewModel.TelemetryErrorMessage);
    }

    [Fact]
    public async Task TelemetryReplay_CurrentReadFailureStillReachesTheCaller()
    {
        await using var viewModel = new MainViewModel(new FakeServiceClient(), new RecordingDispatcher());
        await viewModel.SetTelemetryEnabledAsync(true);
        viewModel.SetTelemetryReplayReaderForTests((_, _, _) =>
            Task.FromException<TelemetrySessionDocument>(new IOException("The current CSV could not be read.")));

        await Assert.ThrowsAsync<IOException>(() => viewModel.OpenTelemetryReplayAsync("current.csv", CancellationToken.None));
    }

    private static TelemetrySessionDocument ReplayOrderingDocument() => new(
        TelemetrySessionMetadata.CreateLive(new(2026, 11, 2, 1, 0, 0, TimeSpan.Zero)), [], []);
}
