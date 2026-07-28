namespace OpenBlade.Tray.Tests;

public sealed class UpdateCheckViewModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AutomaticCheckDoesNotRunBeforeDailyIntervalIsDue()
    {
        var client = new FakeReleaseUpdateClient(
            new ReleaseUpdateInfo(new Version(1, 1, 0), "v1.1.0", ReleaseUri));
        await using var viewModel = new UpdateCheckViewModel(
            client,
            "1.0.0",
            new FixedTimeProvider(Now));
        viewModel.ApplyPreferences(UpdateCheckInterval.Daily, Now.AddHours(-12));

        viewModel.StartAutomaticCheck();
        await Task.Yield();

        Assert.Equal(0, client.CallCount);
        Assert.Equal("Updates have not been checked.", viewModel.StatusText);
    }

    [Fact]
    public async Task DueAutomaticCheckReportsAvailableReleaseAndRecordsAttempt()
    {
        var client = new FakeReleaseUpdateClient(
            new ReleaseUpdateInfo(new Version(1, 1, 0), "v1.1.0", ReleaseUri));
        await using var viewModel = new UpdateCheckViewModel(
            client,
            "1.0.0+abcdef",
            new FixedTimeProvider(Now));
        viewModel.ApplyPreferences(UpdateCheckInterval.Weekly, Now.AddDays(-7));
        int preferenceChanges = 0;
        viewModel.PreferencesChanged += (_, _) => preferenceChanges++;

        viewModel.StartAutomaticCheck();
        await WaitUntilAsync(() => client.CallCount == 1 && !viewModel.IsChecking);

        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Equal(ReleaseUri, viewModel.ReleasePageUri);
        Assert.Equal("OpenBlade 1.1.0 is available.", viewModel.StatusText);
        Assert.Equal(Now, viewModel.LastCheckAt);
        Assert.Equal(1, preferenceChanges);
    }

    [Fact]
    public async Task ManualOnlyStillAllowsCheckNow()
    {
        var client = new FakeReleaseUpdateClient(
            new ReleaseUpdateInfo(new Version(1, 0, 0), "v1.0.0", ReleaseUri));
        await using var viewModel = new UpdateCheckViewModel(
            client,
            "1.0.0",
            new FixedTimeProvider(Now));
        viewModel.ApplyPreferences(UpdateCheckInterval.ManualOnly, null);
        viewModel.StartAutomaticCheck();
        await Task.Yield();
        Assert.Equal(0, client.CallCount);

        await Assert.IsType<AsyncRelayCommand>(viewModel.CheckForUpdatesCommand)
            .ExecuteAsync(null);

        Assert.Equal(1, client.CallCount);
        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Equal("OpenBlade is up to date.", viewModel.StatusText);
        Assert.Equal(Now, viewModel.LastCheckAt);
    }

    [Fact]
    public async Task FailedCheckKeepsTheAttemptTimestampAndShowsRetryMessage()
    {
        var client = new FakeReleaseUpdateClient(
            new ReleaseUpdateException("Unavailable."));
        await using var viewModel = new UpdateCheckViewModel(
            client,
            "1.0.0",
            new FixedTimeProvider(Now));

        await Assert.IsType<AsyncRelayCommand>(viewModel.CheckForUpdatesCommand)
            .ExecuteAsync(null);

        Assert.Equal(Now, viewModel.LastCheckAt);
        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Null(viewModel.ReleasePageUri);
        Assert.Equal(
            "Couldn't check for updates. Try again later.",
            viewModel.StatusText);
    }

    [Fact]
    public async Task ChoosingIntervalRaisesPreferenceChange()
    {
        var client = new FakeReleaseUpdateClient(
            new ReleaseUpdateInfo(new Version(1, 0, 0), "v1.0.0", ReleaseUri));
        await using var viewModel = new UpdateCheckViewModel(client, "1.0.0");
        int preferenceChanges = 0;
        viewModel.PreferencesChanged += (_, _) => preferenceChanges++;

        viewModel.SelectedIntervalOption = viewModel.IntervalOptions.Single(
            option => option.Interval == UpdateCheckInterval.Monthly);

        Assert.Equal(UpdateCheckInterval.Monthly, viewModel.Interval);
        Assert.Equal(1, preferenceChanges);
    }

    private static Uri ReleaseUri { get; } = new(
        "https://github.com/OSSBlade/openblade-core/releases/tag/v1.1.0");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeReleaseUpdateClient : IReleaseUpdateClient
    {
        private readonly ReleaseUpdateInfo? release;
        private readonly Exception? exception;
        private int callCount;

        public FakeReleaseUpdateClient(ReleaseUpdateInfo release)
        {
            this.release = release;
        }

        public FakeReleaseUpdateClient(Exception exception)
        {
            this.exception = exception;
        }

        public int CallCount => Volatile.Read(ref callCount);

        public Task<ReleaseUpdateInfo> GetLatestReleaseAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            if (exception is not null)
            {
                return Task.FromException<ReleaseUpdateInfo>(exception);
            }

            return Task.FromResult(release!);
        }
    }
}
