using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace OpenBlade.Tray;

public sealed class UpdateCheckViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IReleaseUpdateClient releaseClient;
    private readonly TimeProvider timeProvider;
    private readonly Version currentVersion;
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncRelayCommand checkForUpdatesCommand;
    private Task automaticCheckTask = Task.CompletedTask;
    private UpdateCheckInterval interval = UpdateCheckInterval.Daily;
    private DateTimeOffset? lastCheckAt;
    private string statusText = "Updates have not been checked.";
    private bool isChecking;
    private bool isUpdateAvailable;
    private Uri? releasePageUri;
    private int automaticCheckStarted;
    private int disposed;

    public UpdateCheckViewModel(
        IReleaseUpdateClient releaseClient,
        string currentVersion,
        TimeProvider? timeProvider = null)
    {
        this.releaseClient = releaseClient
            ?? throw new ArgumentNullException(nameof(releaseClient));
        if (!ReleaseVersion.TryParse(currentVersion, out this.currentVersion))
        {
            throw new ArgumentException(
                "A comparable OpenBlade product version is required.",
                nameof(currentVersion));
        }

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

    public DateTimeOffset? LastCheckAt => lastCheckAt;

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
        private set => SetField(ref releasePageUri, value);
    }

    public ICommand CheckForUpdatesCommand => checkForUpdatesCommand;

    public void ApplyPreferences(
        UpdateCheckInterval selectedInterval,
        DateTimeOffset? selectedLastCheckAt)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Interval = Enum.IsDefined(selectedInterval)
            ? selectedInterval
            : UpdateCheckInterval.Daily;
        lastCheckAt = selectedLastCheckAt;
        Notify(nameof(LastCheckAt));
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
        await checkForUpdatesCommand.DisposeAsync();
        try
        {
            await automaticCheckTask;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        lifetime.Dispose();
    }

    private async Task RunAutomaticCheckAsync()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!UpdateCheckSchedule.IsDue(Interval, LastCheckAt, now))
        {
            return;
        }

        await CheckCoreAsync(lifetime.Token);
    }

    private async Task CheckCoreAsync(CancellationToken cancellationToken)
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        StatusText = "Checking GitHub for updates...";
        IsUpdateAvailable = false;
        ReleasePageUri = null;
        lastCheckAt = timeProvider.GetUtcNow();
        Notify(nameof(LastCheckAt));
        PreferencesChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            ReleaseUpdateInfo release =
                await releaseClient.GetLatestReleaseAsync(cancellationToken);
            if (release.Version > currentVersion)
            {
                IsUpdateAvailable = true;
                ReleasePageUri = release.ReleasePageUri;
                StatusText = $"OpenBlade {release.Version} is available.";
            }
            else
            {
                StatusText = "OpenBlade is up to date.";
            }
        }
        catch (ReleaseUpdateException)
        {
            StatusText = "Couldn't check for updates. Try again later.";
        }
        finally
        {
            IsChecking = false;
        }
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
