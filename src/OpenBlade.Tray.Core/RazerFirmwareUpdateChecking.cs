using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Input;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record RazerFirmwareSupportPage(
    string ModelNumber,
    Uri OfficialPageUri);

public sealed record RazerPublishedFirmwareVersions(
    string? BiosVersion,
    string? EcVersion,
    string? McuVersion);

public static class RazerFirmwareSupportCatalog
{
    private static readonly RazerFirmwareSupportPage[] Pages =
    [
        new(
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            new Uri("https://mysupport.razer.com/app/answers/detail/a_id/20854/~/razer-blade-16%E2%80%9D-%282026%29-%7C-rz09-0581x-firmware-updaters")),
        new(
            RazerBladeModelCatalog.Rz090528ModelNumber,
            new Uri("https://mysupport.razer.com/app/answers/detail/a_id/19821/~/razer-blade-16-%282025%29-%7C-rz09-0528x-drivers-%26-downloads")),
    ];

    public static RazerFirmwareSupportPage? Find(string? modelNumber)
    {
        RazerBladeModelInfo? model = RazerBladeModelCatalog.Find(modelNumber);
        return model is null
            ? null
            : Pages.SingleOrDefault(page => string.Equals(
                page.ModelNumber,
                model.ModelNumber,
                StringComparison.OrdinalIgnoreCase));
    }
}

public interface IRazerFirmwareUpdateClient
{
    Task<RazerPublishedFirmwareVersions> GetPublishedVersionsAsync(
        RazerFirmwareSupportPage supportPage,
        CancellationToken cancellationToken);
}

public sealed class RazerFirmwareUpdateException : Exception
{
    public RazerFirmwareUpdateException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public static partial class RazerFirmwarePageParser
{
    public static RazerPublishedFirmwareVersions Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        string text = WebUtility.HtmlDecode(TagRegex().Replace(html, " "));
        text = WhitespaceRegex().Replace(text, " ");
        return new(
            FindVersion(text, BiosVersionRegex()),
            FindVersion(text, EcVersionRegex()),
            FindVersion(text, McuVersionRegex()));
    }

    private static string? FindVersion(string text, Regex regex)
    {
        Match match = regex.Match(text);
        return match.Success ? match.Groups["version"].Value : null;
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        "\\bBIOS(?:\\s+Firmware)?\\s+Updater\\b.{0,300}?\\bv\\s*(?<version>\\d+(?:\\.\\d+)+)\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex BiosVersionRegex();

    [GeneratedRegex(
        "\\bEC\\s+Firmware\\s+Updater\\b.{0,300}?\\bv\\s*(?<version>\\d+(?:\\.\\d+)+)\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex EcVersionRegex();

    [GeneratedRegex(
        "\\bMCU\\s+Firmware\\s+Updater\\b.{0,300}?\\bv\\s*(?<version>\\d+(?:\\.\\d+)+)\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex McuVersionRegex();
}

public sealed class RazerFirmwareUpdateClient : IRazerFirmwareUpdateClient
{
    private const int MaximumResponseBytes = 512 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();
    private readonly HttpClient httpClient;

    public RazerFirmwareUpdateClient()
        : this(SharedHttpClient)
    {
    }

    public RazerFirmwareUpdateClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<RazerPublishedFirmwareVersions> GetPublishedVersionsAsync(
        RazerFirmwareSupportPage supportPage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supportPage);
        ValidateOfficialPage(supportPage.OfficialPageUri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, supportPage.OfficialPageUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.UserAgent.ParseAdd("OpenBlade");
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is Uri effectiveUri)
            {
                ValidateOfficialPage(effectiveUri);
            }
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(
                MaximumResponseBytes,
                timeout.Token).ConfigureAwait(false);
            string html = await response.Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
            RazerPublishedFirmwareVersions versions = RazerFirmwarePageParser.Parse(html);
            if (versions.BiosVersion is null
                && versions.EcVersion is null
                && versions.McuVersion is null)
            {
                throw new InvalidDataException(
                    "Razer's support page did not contain recognized updater versions.");
            }

            return versions;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new RazerFirmwareUpdateException(
                "The official firmware check timed out.",
                exception);
        }
        catch (RazerFirmwareUpdateException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or InvalidDataException
            or NotSupportedException)
        {
            throw new RazerFirmwareUpdateException(
                "Razer's official firmware page could not be read.",
                exception);
        }
    }

    private static void ValidateOfficialPage(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "mysupport.razer.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Firmware metadata must come from the official Razer support host.",
                nameof(uri));
        }
    }

    private static HttpClient CreateSharedHttpClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}

public sealed class FirmwareUpdateAssistantViewModel :
    INotifyPropertyChanged,
    IAsyncDisposable
{
    internal static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(5);
    private const string NotCheckedMessage =
        "Check Razer's official page for published updater versions.";
    private readonly IRazerFirmwareUpdateClient client;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncRelayCommand checkCommand;
    private Task automaticCheckTask = Task.CompletedTask;
    private RazerFirmwareSupportPage? supportPage;
    private FirmwareVersions? installedFirmware;
    private UpdateCheckInterval interval = UpdateCheckInterval.ManualOnly;
    private DateTimeOffset? lastSuccessfulCheckAt;
    private DateTimeOffset? retryAfter;
    private string statusText = NotCheckedMessage;
    private string publishedBiosVersion = "Not checked";
    private string publishedEcVersion = "Not checked";
    private string publishedMcuVersion = "Not checked";
    private bool isChecking;
    private int checkInProgress;
    private int automaticCheckStarted;
    private int disposed;

    public FirmwareUpdateAssistantViewModel(
        IRazerFirmwareUpdateClient client,
        TimeProvider? timeProvider = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        checkCommand = new AsyncRelayCommand(
            (_, token) => CheckAsync(token),
            _ => IsSupported && !IsChecking,
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

    public DateTimeOffset? LastSuccessfulCheckAt => lastSuccessfulCheckAt;

    public DateTimeOffset? RetryAfter => retryAfter;

    public ICommand CheckCommand => checkCommand;

    public bool IsSupported => supportPage is not null;

    public bool IsChecking
    {
        get => isChecking;
        private set
        {
            if (SetField(ref isChecking, value))
            {
                checkCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public Uri? OfficialSupportUri => supportPage?.OfficialPageUri;

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public string InstalledBiosVersion => FirmwareValue(installedFirmware?.BiosVersion);

    public string InstalledEcVersion => FirmwareValue(installedFirmware?.EcVersion);

    public string InstalledMcuVersion => FirmwareValue(installedFirmware?.McuVersion);

    public string PublishedBiosVersion
    {
        get => publishedBiosVersion;
        private set => SetField(ref publishedBiosVersion, value);
    }

    public string PublishedEcVersion
    {
        get => publishedEcVersion;
        private set => SetField(ref publishedEcVersion, value);
    }

    public string PublishedMcuVersion
    {
        get => publishedMcuVersion;
        private set => SetField(ref publishedMcuVersion, value);
    }

    public void UpdateInstalledFirmware(FirmwareVersions? firmware)
    {
        RazerFirmwareSupportPage? nextPage = RazerFirmwareSupportCatalog.Find(
            firmware?.ModelNumber);
        bool modelChanged = !string.Equals(
            supportPage?.ModelNumber,
            nextPage?.ModelNumber,
            StringComparison.OrdinalIgnoreCase);
        installedFirmware = firmware;
        supportPage = nextPage;
        Notify(nameof(IsSupported));
        Notify(nameof(OfficialSupportUri));
        Notify(nameof(InstalledBiosVersion));
        Notify(nameof(InstalledEcVersion));
        Notify(nameof(InstalledMcuVersion));
        checkCommand.NotifyCanExecuteChanged();

        if (modelChanged)
        {
            PublishedBiosVersion = "Not checked";
            PublishedEcVersion = "Not checked";
            PublishedMcuVersion = "Not checked";
            StatusText = CreateNotCheckedMessage();
        }
    }

    public void ApplyPreferences(
        UpdateCheckInterval selectedInterval,
        DateTimeOffset? selectedLastSuccessfulCheckAt,
        DateTimeOffset? selectedRetryAfter)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Interval = Enum.IsDefined(selectedInterval)
            ? selectedInterval
            : UpdateCheckInterval.ManualOnly;
        lastSuccessfulCheckAt = selectedLastSuccessfulCheckAt;
        retryAfter = selectedRetryAfter;
        StatusText = CreateNotCheckedMessage();
        Notify(nameof(LastSuccessfulCheckAt));
        Notify(nameof(RetryAfter));
    }

    public void StartAutomaticCheck()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!IsSupported || Interlocked.Exchange(ref automaticCheckStarted, 1) != 0)
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
            await checkCommand.DisposeAsync();
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
                    "OpenBlade automatic firmware-check shutdown failed: {0}",
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
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!UpdateCheckSchedule.IsDue(
                Interval,
                LastSuccessfulCheckAt,
                RetryAfter,
                now))
        {
            return;
        }

        await CheckAsync(lifetime.Token);
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref checkInProgress, 1, 0) != 0)
        {
            return;
        }

        RazerFirmwareSupportPage? page = supportPage;
        if (page is null)
        {
            Interlocked.Exchange(ref checkInProgress, 0);
            return;
        }

        IsChecking = true;
        StatusText = "Checking Razer's official support page...";
        try
        {
            RazerPublishedFirmwareVersions versions =
                await client.GetPublishedVersionsAsync(page, cancellationToken);
            if (!string.Equals(
                    page.ModelNumber,
                    supportPage?.ModelNumber,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            PublishedBiosVersion = PublishedValue(versions.BiosVersion);
            PublishedEcVersion = PublishedValue(versions.EcVersion);
            PublishedMcuVersion = PublishedValue(versions.McuVersion);
            lastSuccessfulCheckAt = timeProvider.GetUtcNow();
            retryAfter = null;
            Notify(nameof(LastSuccessfulCheckAt));
            Notify(nameof(RetryAfter));
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
            StatusText = "Published updater versions loaded from Razer.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RazerFirmwareUpdateException exception)
        {
            Trace.TraceWarning(
                "OpenBlade firmware update check failed: {0}",
                exception);
            RecordFailureRetry();
            StatusText = "Couldn't read Razer's page. The official link is still available.";
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade firmware update check failed unexpectedly: {0}",
                exception);
            RecordFailureRetry();
            StatusText = "Couldn't read Razer's page. The official link is still available.";
        }
        finally
        {
            IsChecking = false;
            Interlocked.Exchange(ref checkInProgress, 0);
        }
    }

    private void RecordFailureRetry()
    {
        retryAfter = timeProvider.GetUtcNow() + FailureRetryDelay;
        Notify(nameof(RetryAfter));
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private string CreateNotCheckedMessage() =>
        lastSuccessfulCheckAt is DateTimeOffset checkedAt
            ? $"Last checked {checkedAt.ToLocalTime():g}. Check again to load published versions."
            : NotCheckedMessage;

    private static string FirmwareValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "Unavailable", StringComparison.OrdinalIgnoreCase)
            ? "Unavailable"
            : value.Trim();

    private static string PublishedValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Not listed" : value.Trim();

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
