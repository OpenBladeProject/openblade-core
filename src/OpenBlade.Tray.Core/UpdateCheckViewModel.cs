using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace OpenBlade.Tray;

public sealed class UpdateCheckViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    internal static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(5);
    private readonly IReleaseUpdateClient releaseClient;
    private readonly TimeProvider timeProvider;
    private readonly Version? currentVersion;
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncRelayCommand checkForUpdatesCommand;
    private Task automaticCheckTask = Task.CompletedTask;
    private UpdateCheckInterval interval = UpdateCheckInterval.Daily;
    private DateTimeOffset? lastSuccessfulCheckAt;
    private DateTimeOffset? retryAfter;
    private string? entityTag;
    private ReleaseUpdateInfo? cachedRelease;
    private string statusText = "Updates have not been checked.";
    private bool promptShown;
    private bool isChecking;
    private bool isUpdateAvailable;
    private Uri? releasePageUri;
    private int checkInProgress;
    private int automaticCheckStarted;
    private int disposed;

    public UpdateCheckViewModel(
        IReleaseUpdateClient releaseClient,
        string currentVersion,
        TimeProvider? timeProvider = null)
    {
        this.releaseClient = releaseClient
            ?? throw new ArgumentNullException(nameof(releaseClient));
        this.currentVersion = ReleaseVersion.TryParse(
            currentVersion,
            out Version parsedVersion)
            ? parsedVersion
            : null;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        checkForUpdatesCommand = new AsyncRelayCommand(
            (_, token) => CheckCoreAsync(token),
            _ => !IsChecking,
            lifetime.Token);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? PreferencesChanged;

    public IReadOnlyList<UpdateCheckIntervalOption> IntervalOptions { get; } =
    [
        new(UpdateCheckInterval.EveryStartup, "Every startup"),
        new(UpdateCheckInterval.Daily, "Daily"),
        new(UpdateCheckInterval.Weekly, "Weekly"),
        new(UpdateCheckInterval.Monthly, "Monthly"),
        new(UpdateCheckInterval.ManualOnly, "Manual only"),
    ];

    public UpdateCheckInterval Interval
    {
        get => interval;
        private set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (interval == value)
            {
                return;
            }

            interval = value;
            Notify();
            Notify(nameof(SelectedIntervalOption));
        }
    }

    public UpdateCheckIntervalOption SelectedIntervalOption
    {
        get => IntervalOptions.Single(option => option.Interval == Interval);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Interval == Interval)
            {
                return;
            }

            Interval = value.Interval;
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool NeedsConsentPrompt => !promptShown;

    public DateTimeOffset? LastSuccessfulCheckAt => lastSuccessfulCheckAt;

    public DateTimeOffset? RetryAfter => retryAfter;

    public string? EntityTag => entityTag;

    public string? CachedReleaseTag => cachedRelease?.TagName;

    public string? CachedReleaseUrl => cachedRelease?.ReleasePageUri.AbsoluteUri;

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public bool IsChecking
    {
        get => isChecking;
        private set
        {
            if (!SetField(ref isChecking, value))
            {
                return;
            }

            checkForUpdatesCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsUpdateAvailable
    {
        get => isUpdateAvailable;
        private set => SetField(ref isUpdateAvailable, value);
    }

    public Uri? ReleasePageUri
    {
        get => releasePageUri;
        private set
        {
            if (SetField(ref releasePageUri, value))
            {
                Notify(nameof(HasReleasePage));
            }
        }
    }

    public bool HasReleasePage => ReleasePageUri is not null;

    public ICommand CheckForUpdatesCommand => checkForUpdatesCommand;

    public void ApplyPreferences(
        UpdateCheckInterval selectedInterval,
        bool selectedPromptShown,
        DateTimeOffset? selectedLastSuccessfulCheckAt,
        DateTimeOffset? selectedRetryAfter,
        string? selectedEntityTag,
        string? selectedCachedReleaseTag,
        string? selectedCachedReleaseUrl)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Interval = Enum.IsDefined(selectedInterval)
            ? selectedInterval
            : UpdateCheckInterval.Daily;
        promptShown = selectedPromptShown;
        lastSuccessfulCheckAt = selectedLastSuccessfulCheckAt;
        retryAfter = selectedRetryAfter;
        entityTag = NormalizeEntityTag(selectedEntityTag);
        cachedRelease = ParseCachedRelease(
            selectedCachedReleaseTag,
            selectedCachedReleaseUrl);
        Notify(nameof(NeedsConsentPrompt));
        Notify(nameof(LastSuccessfulCheckAt));
        Notify(nameof(RetryAfter));
    }

    public void RecordConsent(UpdateCheckInterval selectedInterval)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Interval = selectedInterval;
        promptShown = true;
        Notify(nameof(NeedsConsentPrompt));
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StartAutomaticCheck()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref automaticCheckStarted, 1) != 0)
        {
            return;
        }

        automaticCheckTask = RunAutomaticCheckAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        try
        {
            await checkForUpdatesCommand.DisposeAsync();
            try
            {
                await automaticCheckTask;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Trace.TraceError(
                    "OpenBlade automatic update-check shutdown failed: {0}",
                    exception);
            }
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task RunAutomaticCheckAsync()
    {
        if (NeedsConsentPrompt)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!UpdateCheckSchedule.IsDue(
                Interval,
                LastSuccessfulCheckAt,
                RetryAfter,
                now))
        {
            return;
        }

        await CheckCoreAsync(lifetime.Token);
    }

    private async Task CheckCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref checkInProgress, 1, 0) != 0)
        {
            return;
        }

        IsChecking = true;
        StatusText = "Checking GitHub for updates...";
        IsUpdateAvailable = false;
        ReleasePageUri = null;

        try
        {
            ReleaseUpdateResult result = await releaseClient.GetLatestReleaseAsync(
                entityTag,
                cancellationToken);
            DateTimeOffset completedAt = timeProvider.GetUtcNow();
            lastSuccessfulCheckAt = completedAt;
            retryAfter = null;
            entityTag = NormalizeEntityTag(result.EntityTag);
            if (result.Disposition == ReleaseUpdateDisposition.PublishedRelease)
            {
                cachedRelease = result.Release
                    ?? throw new InvalidDataException(
                        "The published release result did not include release metadata.");
            }
            else if (result.Disposition == ReleaseUpdateDisposition.NoEligibleRelease)
            {
                cachedRelease = null;
            }

            Notify(nameof(LastSuccessfulCheckAt));
            Notify(nameof(RetryAfter));
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
            PresentRelease(result.Disposition == ReleaseUpdateDisposition.NotModified
                ? cachedRelease
                : result.Release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Update check canceled.";
            throw;
        }
        catch (ReleaseUpdateRateLimitException exception)
        {
            retryAfter = exception.RetryAfter;
            Notify(nameof(RetryAfter));
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
            StatusText = $"GitHub rate limit reached. Try again after "
                + $"{exception.RetryAfter.ToLocalTime():g}.";
        }
        catch (ReleaseUpdateRetryException exception)
        {
            retryAfter = exception.RetryAfter;
            Notify(nameof(RetryAfter));
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
            StatusText = $"GitHub is temporarily unavailable. Try again after "
                + $"{exception.RetryAfter.ToLocalTime():g}.";
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade update check failed: {0}", exception);
            retryAfter = timeProvider.GetUtcNow() + FailureRetryDelay;
            Notify(nameof(RetryAfter));
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
            StatusText = "Couldn't check for updates. Try again later.";
        }
        finally
        {
            IsChecking = false;
            Interlocked.Exchange(ref checkInProgress, 0);
        }
    }

    private void PresentRelease(ReleaseUpdateInfo? release)
    {
        if (release is null)
        {
            StatusText = "No eligible published release is available.";
            return;
        }

        if (currentVersion is null)
        {
            ReleasePageUri = release.ReleasePageUri;
            StatusText = $"Latest release: {release.TagName}. "
                + "The installed version could not be compared.";
            return;
        }

        if (release.Version > currentVersion)
        {
            IsUpdateAvailable = true;
            ReleasePageUri = release.ReleasePageUri;
            StatusText = $"OpenBlade {release.TagName.TrimStart('v', 'V')} is available.";
        }
        else
        {
            StatusText = "OpenBlade is up to date.";
        }
    }

    private static string? NormalizeEntityTag(string? value) =>
        EntityTagHeaderValue.TryParse(value, out EntityTagHeaderValue? parsed)
            ? parsed.ToString()
            : null;

    private static ReleaseUpdateInfo? ParseCachedRelease(
        string? tag,
        string? url)
    {
        if (!ReleaseVersion.TryParse(tag, out Version version)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ReleaseUpdateInfo(version, tag!, uri);
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Notify(propertyName);
        return true;
    }

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
