namespace OpenBlade.Tray.Tests;

public sealed class UpdateCheckViewModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AutomaticCheckWaitsForFirstRunConsent()
    {
        var client = SuccessfulClient(NewerRelease);
        await using var viewModel = Create(client);
        Apply(viewModel, promptShown: false, lastSuccessfulCheckAt: null);

        viewModel.StartAutomaticCheck();
        await Task.Yield();

        Assert.Equal(0, client.CallCount);
        Assert.True(viewModel.NeedsConsentPrompt);
    }

    [Fact]
    public async Task DailyConsentAllowsDueAutomaticCheck()
    {
        var client = SuccessfulClient(NewerRelease);
        await using var viewModel = Create(client);
        Apply(viewModel, promptShown: false, lastSuccessfulCheckAt: null);
        int preferenceChanges = 0;
        viewModel.PreferencesChanged += (_, _) => preferenceChanges++;

        viewModel.RecordConsent(UpdateCheckInterval.Daily);
        viewModel.StartAutomaticCheck();
        await WaitUntilAsync(() => client.CallCount == 1 && !viewModel.IsChecking);

        Assert.False(viewModel.NeedsConsentPrompt);
        Assert.Equal(UpdateCheckInterval.Daily, viewModel.Interval);
        Assert.Equal(Now, viewModel.LastSuccessfulCheckAt);
        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Equal(2, preferenceChanges);
    }

    [Fact]
    public async Task AutomaticCheckDoesNotRunBeforeSuccessfulIntervalIsDue()
    {
        var client = SuccessfulClient(NewerRelease);
        await using var viewModel = Create(client);
        Apply(
            viewModel,
            promptShown: true,
            lastSuccessfulCheckAt: Now.AddHours(-12));

        viewModel.StartAutomaticCheck();
        await Task.Yield();

        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task FailedCheckUsesShortBackoffWithoutRecordingSuccess()
    {
        var firstClient = new FakeReleaseUpdateClient(
            new ReleaseUpdateException("Unavailable."));
        DateTimeOffset? retryAfter;
        await using (var first = Create(firstClient))
        {
            Apply(first, promptShown: true, lastSuccessfulCheckAt: null);
            first.StartAutomaticCheck();
            await WaitUntilAsync(() => firstClient.CallCount == 1 && !first.IsChecking);

            Assert.Null(first.LastSuccessfulCheckAt);
            retryAfter = first.RetryAfter;
            Assert.Equal(
                Now.AddMinutes(5),
                retryAfter);
            Assert.Equal(
                "Couldn't check for updates. Try again later.",
                first.StatusText);
        }

        var secondClient = SuccessfulClient(NewerRelease);
        await using var second = Create(secondClient);
        Apply(
            second,
            promptShown: true,
            lastSuccessfulCheckAt: null,
            retryAfter: retryAfter);
        second.StartAutomaticCheck();
        await Task.Yield();

        Assert.Equal(0, secondClient.CallCount);

        var laterClient = SuccessfulClient(NewerRelease);
        await using var later = new UpdateCheckViewModel(
            laterClient,
            "1.0.0",
            new FixedTimeProvider(retryAfter!.Value));
        Apply(
            later,
            promptShown: true,
            lastSuccessfulCheckAt: null,
            retryAfter: retryAfter);
        later.StartAutomaticCheck();
        await WaitUntilAsync(() => laterClient.CallCount == 1 && !later.IsChecking);

        Assert.Equal(retryAfter, later.LastSuccessfulCheckAt);
    }

    [Fact]
    public async Task UnexpectedClientFailureIsContained()
    {
        var client = new FakeReleaseUpdateClient(
            new InvalidOperationException("handler failed"));
        await using var viewModel = Create(client);

        await Command(viewModel).ExecuteAsync(null);

        Assert.Null(viewModel.LastSuccessfulCheckAt);
        Assert.Equal(
            Now.AddMinutes(5),
            viewModel.RetryAfter);
        Assert.Equal(
            "Couldn't check for updates. Try again later.",
            viewModel.StatusText);
    }

    [Fact]
    public async Task AutomaticOfflineCheckIsContainedAndReportsNonfatalStatus()
    {
        var client = new FakeReleaseUpdateClient(
            new ReleaseUpdateConnectivityException(
                new HttpRequestException("No network route is available.")));
        var viewModel = Create(client);
        Apply(viewModel, promptShown: true, lastSuccessfulCheckAt: null);

        viewModel.StartAutomaticCheck();
        await viewModel.DisposeAsync();

        Assert.Null(viewModel.LastSuccessfulCheckAt);
        Assert.Equal(
            Now.AddMinutes(5),
            viewModel.RetryAfter);
        Assert.Equal(
            "No internet connection. Check your connection and try again.",
            viewModel.StatusText);
    }

    [Fact]
    public async Task ManualOnlyStillAllowsCheckNow()
    {
        var client = SuccessfulClient(CurrentRelease);
        await using var viewModel = Create(client);
        Apply(
            viewModel,
            promptShown: true,
            lastSuccessfulCheckAt: null,
            interval: UpdateCheckInterval.ManualOnly);
        viewModel.StartAutomaticCheck();
        await Task.Yield();
        Assert.Equal(0, client.CallCount);

        await Command(viewModel).ExecuteAsync(null);

        Assert.Equal(1, client.CallCount);
        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Equal("OpenBlade is up to date.", viewModel.StatusText);
        Assert.Equal(Now, viewModel.LastSuccessfulCheckAt);
    }

    [Fact]
    public async Task OlderRemoteReleaseIsUpToDate()
    {
        var client = SuccessfulClient(
            new(new Version(0, 9, 0, 0), "v0.9.0", ReleaseUri));
        await using var viewModel = Create(client);

        await Command(viewModel).ExecuteAsync(null);

        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Equal("OpenBlade is up to date.", viewModel.StatusText);
    }

    [Fact]
    public async Task MissingLocalVersionDoesNotCrashTrayOrHideRelease()
    {
        var client = SuccessfulClient(NewerRelease);
        await using var viewModel = new UpdateCheckViewModel(
            client,
            "Unavailable",
            new FixedTimeProvider(Now));

        await Command(viewModel).ExecuteAsync(null);

        Assert.False(viewModel.IsUpdateAvailable);
        Assert.True(viewModel.HasReleasePage);
        Assert.Equal(ReleaseUri, viewModel.ReleasePageUri);
        Assert.Contains(
            "could not be compared",
            viewModel.StatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimitPersistsRetryBoundaryWithoutRecordingSuccess()
    {
        var retryAfter = Now.AddMinutes(10);
        var client = new FakeReleaseUpdateClient(
            new ReleaseUpdateRateLimitException(retryAfter));
        await using var viewModel = Create(client);
        int preferenceChanges = 0;
        viewModel.PreferencesChanged += (_, _) => preferenceChanges++;

        await Command(viewModel).ExecuteAsync(null);

        Assert.Null(viewModel.LastSuccessfulCheckAt);
        Assert.Equal(retryAfter, viewModel.RetryAfter);
        Assert.Equal(1, preferenceChanges);
        Assert.Contains("rate limit", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerBackoffPersistsRetryBoundaryWithoutRecordingSuccess()
    {
        var retryAfter = Now.AddMinutes(3);
        var client = new FakeReleaseUpdateClient(
            new ReleaseUpdateRetryException("Unavailable.", retryAfter));
        await using var viewModel = Create(client);

        await Command(viewModel).ExecuteAsync(null);

        Assert.Null(viewModel.LastSuccessfulCheckAt);
        Assert.Equal(retryAfter, viewModel.RetryAfter);
        Assert.Contains(
            "temporarily unavailable",
            viewModel.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotModifiedUsesPersistedReleaseCacheAndRefreshesSuccessTime()
    {
        var client = new FakeReleaseUpdateClient(
            new ReleaseUpdateResult(
                ReleaseUpdateDisposition.NotModified,
                Release: null,
                "\"etag\""));
        await using var viewModel = Create(client);
        Apply(
            viewModel,
            promptShown: true,
            lastSuccessfulCheckAt: Now.AddDays(-2),
            entityTag: "\"etag\"",
            cachedReleaseTag: "v1.1.0",
            cachedReleaseUrl: ReleaseUri.AbsoluteUri);

        await Command(viewModel).ExecuteAsync(null);

        Assert.Equal("\"etag\"", client.LastEntityTag);
        Assert.Equal(Now, viewModel.LastSuccessfulCheckAt);
        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Equal(ReleaseUri, viewModel.ReleasePageUri);
    }

    [Fact]
    public async Task NoEligibleReleaseIsSuccessfulAndClearsCache()
    {
        var client = new FakeReleaseUpdateClient(
            new ReleaseUpdateResult(
                ReleaseUpdateDisposition.NoEligibleRelease,
                Release: null,
                "\"new\""));
        await using var viewModel = Create(client);
        Apply(
            viewModel,
            promptShown: true,
            lastSuccessfulCheckAt: null,
            cachedReleaseTag: "v1.1.0",
            cachedReleaseUrl: ReleaseUri.AbsoluteUri);

        await Command(viewModel).ExecuteAsync(null);

        Assert.Equal(Now, viewModel.LastSuccessfulCheckAt);
        Assert.Null(viewModel.CachedReleaseTag);
        Assert.Equal(
            "No eligible published release is available.",
            viewModel.StatusText);
    }

    [Fact]
    public async Task DisposeCancelsAndWaitsForInFlightAutomaticCheck()
    {
        var client = new BlockingReleaseUpdateClient();
        var viewModel = Create(client);
        Apply(viewModel, promptShown: true, lastSuccessfulCheckAt: null);
        viewModel.StartAutomaticCheck();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.DisposeAsync();

        Assert.True(client.CancellationObserved);
        Assert.Equal("Update check canceled.", viewModel.StatusText);
    }

    [Fact]
    public async Task ChoosingIntervalRaisesPreferenceChange()
    {
        var client = SuccessfulClient(CurrentRelease);
        await using var viewModel = Create(client);
        int preferenceChanges = 0;
        viewModel.PreferencesChanged += (_, _) => preferenceChanges++;

        viewModel.SelectedIntervalOption = viewModel.IntervalOptions.Single(
            option => option.Interval == UpdateCheckInterval.Monthly);

        Assert.Equal(UpdateCheckInterval.Monthly, viewModel.Interval);
        Assert.Equal(1, preferenceChanges);
    }

    [Fact]
    public async Task ChoosingNightlyChannelClearsStableCacheBeforeChecking()
    {
        var client = SuccessfulClient(
            new(
                new Version(1, 0, 0, 42),
                "v1.0.0-nightly.42",
                ReleaseUri));
        await using var viewModel = Create(client);
        Apply(
            viewModel,
            promptShown: true,
            lastSuccessfulCheckAt: Now,
            entityTag: "\"stable\"",
            cachedReleaseTag: "v1.1.0",
            cachedReleaseUrl: ReleaseUri.AbsoluteUri);
        int preferenceChanges = 0;
        viewModel.PreferencesChanged += (_, _) => preferenceChanges++;

        viewModel.SelectedChannelOption = viewModel.ChannelOptions.Single(
            option => option.Channel == UpdateChannel.Nightly);

        Assert.Equal(UpdateChannel.Nightly, viewModel.Channel);
        Assert.Null(viewModel.EntityTag);
        Assert.Null(viewModel.CachedReleaseTag);
        Assert.Null(viewModel.LastSuccessfulCheckAt);
        Assert.Equal(1, preferenceChanges);

        await Command(viewModel).ExecuteAsync(null);

        Assert.Equal(UpdateChannel.Nightly, client.LastChannel);
        Assert.Null(client.LastEntityTag);
        Assert.True(viewModel.IsUpdateAvailable);
    }

    [Fact]
    public async Task ChangingChannelDiscardsAnInFlightResultFromThePreviousChannel()
    {
        var client = new ControlledReleaseUpdateClient();
        await using var viewModel = Create(client);
        Apply(viewModel, promptShown: true, lastSuccessfulCheckAt: null);
        Task check = Command(viewModel).ExecuteAsync(null);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.SelectedChannelOption = viewModel.ChannelOptions.Single(
            option => option.Channel == UpdateChannel.Nightly);
        client.Complete(new(
            ReleaseUpdateDisposition.PublishedRelease,
            NewerRelease,
            "\"stable\""));
        await check;

        Assert.Equal(UpdateChannel.Nightly, viewModel.Channel);
        Assert.Null(viewModel.LastSuccessfulCheckAt);
        Assert.Null(viewModel.EntityTag);
        Assert.Null(viewModel.CachedReleaseTag);
        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Equal(
            "Updates have not been checked for the Nightly channel.",
            viewModel.StatusText);
    }

    private static UpdateCheckViewModel Create(IReleaseUpdateClient client) =>
        new(client, "1.0.0+abcdef", new FixedTimeProvider(Now));

    private static void Apply(
        UpdateCheckViewModel viewModel,
        bool promptShown,
        DateTimeOffset? lastSuccessfulCheckAt,
        UpdateCheckInterval interval = UpdateCheckInterval.Daily,
        DateTimeOffset? retryAfter = null,
        UpdateChannel channel = UpdateChannel.Stable,
        string? entityTag = null,
        string? cachedReleaseTag = null,
        string? cachedReleaseUrl = null) =>
        viewModel.ApplyPreferences(
            channel,
            interval,
            promptShown,
            lastSuccessfulCheckAt,
            retryAfter,
            entityTag,
            cachedReleaseTag,
            cachedReleaseUrl);

    private static AsyncRelayCommand Command(UpdateCheckViewModel viewModel) =>
        Assert.IsType<AsyncRelayCommand>(viewModel.CheckForUpdatesCommand);

    private static FakeReleaseUpdateClient SuccessfulClient(ReleaseUpdateInfo release) =>
        new(new ReleaseUpdateResult(
            ReleaseUpdateDisposition.PublishedRelease,
            release,
            "\"release\""));

    private static Uri ReleaseUri { get; } = new(
        "https://github.com/OSSBlade/openblade-core/releases/tag/v1.1.0");

    private static ReleaseUpdateInfo CurrentRelease { get; } =
        new(new Version(1, 0, 0, 0), "v1.0.0", ReleaseUri);

    private static ReleaseUpdateInfo NewerRelease { get; } =
        new(new Version(1, 1, 0, 0), "v1.1.0", ReleaseUri);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeReleaseUpdateClient : IReleaseUpdateClient
    {
        private readonly ReleaseUpdateResult? result;
        private readonly Exception? exception;
        private int callCount;

        public FakeReleaseUpdateClient(ReleaseUpdateResult result)
        {
            this.result = result;
        }

        public FakeReleaseUpdateClient(Exception exception)
        {
            this.exception = exception;
        }

        public int CallCount => Volatile.Read(ref callCount);

        public string? LastEntityTag { get; private set; }

        public UpdateChannel? LastChannel { get; private set; }

        public Task<ReleaseUpdateResult> GetLatestReleaseAsync(
            UpdateChannel channel,
            string? entityTag,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            LastChannel = channel;
            LastEntityTag = entityTag;
            if (exception is not null)
            {
                return Task.FromException<ReleaseUpdateResult>(exception);
            }

            return Task.FromResult(result!);
        }
    }

    private sealed class BlockingReleaseUpdateClient : IReleaseUpdateClient
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task<ReleaseUpdateResult> GetLatestReleaseAsync(
            UpdateChannel channel,
            string? entityTag,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The infinite delay completed.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class ControlledReleaseUpdateClient : IReleaseUpdateClient
    {
        private readonly TaskCompletionSource<ReleaseUpdateResult> result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ReleaseUpdateResult> GetLatestReleaseAsync(
            UpdateChannel channel,
            string? entityTag,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return result.Task.WaitAsync(cancellationToken);
        }

        public void Complete(ReleaseUpdateResult value) => result.TrySetResult(value);
    }
}
