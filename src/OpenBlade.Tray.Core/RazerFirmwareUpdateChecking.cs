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

public enum RazerPublishedFirmwareVersionStatus
{
    Listed,
    NotListed,
    Unreadable,
}

public sealed record RazerPublishedFirmwareVersions(
    string? BiosVersion,
    string? EcVersion,
    string? McuVersion,
    RazerPublishedFirmwareVersionStatus BiosStatus =
        RazerPublishedFirmwareVersionStatus.Listed,
    RazerPublishedFirmwareVersionStatus EcStatus =
        RazerPublishedFirmwareVersionStatus.Listed,
    RazerPublishedFirmwareVersionStatus McuStatus =
        RazerPublishedFirmwareVersionStatus.Listed);

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
        ParsedVersion bios = FindVersion(text, BiosLabelRegex(), BiosVersionRegex());
        ParsedVersion ec = FindVersion(text, EcLabelRegex(), EcVersionRegex());
        ParsedVersion mcu = FindVersion(text, McuLabelRegex(), McuVersionRegex());
        return new(
            bios.Value,
            ec.Value,
            mcu.Value,
            bios.Status,
            ec.Status,
            mcu.Status);
    }

    private static ParsedVersion FindVersion(
        string text,
        Regex labelRegex,
        Regex versionRegex)
    {
        Match match = versionRegex.Match(text);
        if (match.Success)
        {
            return new(
                match.Groups["version"].Value,
                RazerPublishedFirmwareVersionStatus.Listed);
        }

        return new(
            Value: null,
            labelRegex.IsMatch(text)
                ? RazerPublishedFirmwareVersionStatus.Unreadable
                : RazerPublishedFirmwareVersionStatus.NotListed);
    }

    private readonly record struct ParsedVersion(
        string? Value,
        RazerPublishedFirmwareVersionStatus Status);

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        "\\bBIOS(?:\\s+Firmware)?\\s+Updater\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex BiosLabelRegex();

    [GeneratedRegex(
        "\\bBIOS(?:\\s+Firmware)?\\s+Updater\\b(?:(?!\\bUpdater\\b).){0,300}?\\bv\\s*(?<version>\\d+(?:\\.\\d+)+)\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex BiosVersionRegex();

    [GeneratedRegex(
        "\\bEC\\s+Firmware\\s+Updater\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex EcLabelRegex();

    [GeneratedRegex(
        "\\bEC\\s+Firmware\\s+Updater\\b(?:(?!\\bUpdater\\b).){0,300}?\\bv\\s*(?<version>\\d+(?:\\.\\d+)+)\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex EcVersionRegex();

    [GeneratedRegex(
        "\\bMCU\\s+Firmware\\s+Updater\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex McuLabelRegex();

    [GeneratedRegex(
        "\\bMCU\\s+Firmware\\s+Updater\\b(?:(?!\\bUpdater\\b).){0,300}?\\bv\\s*(?<version>\\d+(?:\\.\\d+)+)\\b",
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
                if (!IsOfficialPage(effectiveUri))
                {
                    throw new RazerFirmwareUpdateException(
                        "Razer's official firmware page resolved to an unapproved destination.");
                }
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
        if (!IsOfficialPage(uri))
        {
            throw new ArgumentException(
                "Firmware metadata must come from the official Razer support host.",
                nameof(uri));
        }
    }

    private static bool IsOfficialPage(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, "mysupport.razer.com", StringComparison.OrdinalIgnoreCase);

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

public sealed class FirmwareUpdateAvailableEventArgs(
    string signature,
    string summary,
    Uri officialSupportUri) : EventArgs
{
    public string Signature { get; } = signature;

    public string Summary { get; } = summary;

    public Uri OfficialSupportUri { get; } = officialSupportUri;
}

public sealed class FirmwareUpdateAssistantViewModel :
    INotifyPropertyChanged,
    IAsyncDisposable
{
    internal static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(5);
    private const string NotCheckedMessage =
        "Check Razer's official page for the latest firmware versions.";
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
    private string? cachedModelNumber;
    private string? cachedBiosVersion;
    private string? cachedEcVersion;
    private string? cachedMcuVersion;
    private RazerPublishedFirmwareVersionStatus? cachedBiosStatus;
    private RazerPublishedFirmwareVersionStatus? cachedEcStatus;
    private RazerPublishedFirmwareVersionStatus? cachedMcuStatus;
    private string statusText = NotCheckedMessage;
    private string publishedBiosVersion = "Not checked";
    private string publishedEcVersion = "Not checked";
    private string publishedMcuVersion = "Not checked";
    private bool isChecking;
    private bool isUpdateAvailable;
    private string updateAvailableSummary = string.Empty;
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

    public event EventHandler<FirmwareUpdateAvailableEventArgs>? UpdateAvailable;

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

    public string? CachedModelNumber => cachedModelNumber;

    public string? CachedBiosVersion => cachedBiosVersion;

    public string? CachedEcVersion => cachedEcVersion;

    public string? CachedMcuVersion => cachedMcuVersion;

    public RazerPublishedFirmwareVersionStatus? CachedBiosStatus => cachedBiosStatus;

    public RazerPublishedFirmwareVersionStatus? CachedEcStatus => cachedEcStatus;

    public RazerPublishedFirmwareVersionStatus? CachedMcuStatus => cachedMcuStatus;

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

    public bool IsUpdateAvailable
    {
        get => isUpdateAvailable;
        private set => SetField(ref isUpdateAvailable, value);
    }

    public string UpdateAvailableSummary
    {
        get => updateAvailableSummary;
        private set => SetField(ref updateAvailableSummary, value);
    }

    public Uri? OfficialSupportUri => supportPage?.OfficialPageUri;

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public string InstalledBiosVersion => FirmwareValue(installedFirmware?.BiosVersion);

    public string InstalledEcVersion => FirmwareValue(installedFirmware?.EcVersion);

    public string InstalledMcuVersion =>
        McuFirmwareVersionPresentation.Format(installedFirmware?.McuVersion);

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
        bool firmwareChanged = !Equals(installedFirmware, firmware);
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

        if (modelChanged || firmwareChanged)
        {
            RestoreCachedVersionsForCurrentModel();
        }
    }

    public void ApplyPreferences(
        UpdateCheckInterval selectedInterval,
        DateTimeOffset? selectedLastSuccessfulCheckAt,
        DateTimeOffset? selectedRetryAfter,
        string? selectedCachedModelNumber = null,
        string? selectedCachedBiosVersion = null,
        string? selectedCachedEcVersion = null,
        string? selectedCachedMcuVersion = null,
        RazerPublishedFirmwareVersionStatus? selectedCachedBiosStatus = null,
        RazerPublishedFirmwareVersionStatus? selectedCachedEcStatus = null,
        RazerPublishedFirmwareVersionStatus? selectedCachedMcuStatus = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Interval = Enum.IsDefined(selectedInterval)
            ? selectedInterval
            : UpdateCheckInterval.ManualOnly;
        lastSuccessfulCheckAt = selectedLastSuccessfulCheckAt;
        retryAfter = selectedRetryAfter;
        cachedModelNumber = RazerFirmwareSupportCatalog.Find(selectedCachedModelNumber)
            ?.ModelNumber;
        cachedBiosVersion = NormalizeCachedVersion(selectedCachedBiosVersion);
        cachedEcVersion = NormalizeCachedVersion(selectedCachedEcVersion);
        cachedMcuVersion = NormalizeCachedVersion(selectedCachedMcuVersion);
        cachedBiosStatus = NormalizeCachedStatus(
            selectedCachedBiosStatus,
            cachedBiosVersion);
        cachedEcStatus = NormalizeCachedStatus(selectedCachedEcStatus, cachedEcVersion);
        cachedMcuStatus = NormalizeCachedStatus(
            selectedCachedMcuStatus,
            cachedMcuVersion);
        RestoreCachedVersionsForCurrentModel();
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

            PublishedBiosVersion = PublishedValue(versions.BiosVersion, versions.BiosStatus);
            PublishedEcVersion = PublishedValue(versions.EcVersion, versions.EcStatus);
            PublishedMcuVersion = PublishedValue(versions.McuVersion, versions.McuStatus);
            cachedModelNumber = page.ModelNumber;
            cachedBiosVersion = NormalizeCachedVersion(versions.BiosVersion);
            cachedEcVersion = NormalizeCachedVersion(versions.EcVersion);
            cachedMcuVersion = NormalizeCachedVersion(versions.McuVersion);
            cachedBiosStatus = versions.BiosStatus;
            cachedEcStatus = versions.EcStatus;
            cachedMcuStatus = versions.McuStatus;
            PresentUpdateAvailability(page, versions);
            lastSuccessfulCheckAt = timeProvider.GetUtcNow();
            retryAfter = null;
            Notify(nameof(LastSuccessfulCheckAt));
            Notify(nameof(RetryAfter));
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
            StatusText = IsUpdateAvailable
                ? UpdateAvailableSummary
                : "Latest firmware versions loaded from Razer.";
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
            RecordFailureRetry(page.ModelNumber);
            StatusText = "Couldn't read Razer's page. The official link is still available.";
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade firmware update check failed unexpectedly: {0}",
                exception);
            RecordFailureRetry(page.ModelNumber);
            StatusText = "Couldn't read Razer's page. The official link is still available.";
        }
        finally
        {
            IsChecking = false;
            Interlocked.Exchange(ref checkInProgress, 0);
        }
    }

    private void RecordFailureRetry(string modelNumber)
    {
        cachedModelNumber = modelNumber;
        retryAfter = timeProvider.GetUtcNow() + FailureRetryDelay;
        Notify(nameof(RetryAfter));
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PresentUpdateAvailability(
        RazerFirmwareSupportPage page,
        RazerPublishedFirmwareVersions published)
    {
        var newerComponents = new List<string>();
        AddNewerComponent(
            newerComponents,
            "BIOS",
            installedFirmware?.BiosVersion,
            published.BiosVersion);
        AddNewerComponent(
            newerComponents,
            "EC",
            installedFirmware?.EcVersion,
            published.EcVersion);
        AddNewerComponent(
            newerComponents,
            "MCU",
            McuFirmwareVersionPresentation.Format(installedFirmware?.McuVersion),
            published.McuVersion);

        IsUpdateAvailable = newerComponents.Count > 0;
        UpdateAvailableSummary = IsUpdateAvailable
            ? $"Newer {string.Join(", ", newerComponents)} firmware is available from Razer."
            : string.Empty;
        if (!IsUpdateAvailable)
        {
            return;
        }

        string signature = string.Join(
            '|',
            page.ModelNumber,
            $"bios={NormalizeCachedVersion(published.BiosVersion) ?? "-"}",
            $"ec={NormalizeCachedVersion(published.EcVersion) ?? "-"}",
            $"mcu={NormalizeCachedVersion(published.McuVersion) ?? "-"}");
        UpdateAvailable?.Invoke(
            this,
            new FirmwareUpdateAvailableEventArgs(
                signature,
                UpdateAvailableSummary,
                page.OfficialPageUri));
    }

    private static void AddNewerComponent(
        ICollection<string> newerComponents,
        string label,
        string? installed,
        string? published)
    {
        Version? installedVersion = ParseComparableVersion(installed);
        Version? publishedVersion = ParseComparableVersion(published);
        if (installedVersion is null || publishedVersion is null)
        {
            if ((installedVersion is null && IsTraceableVersion(installed))
                || (publishedVersion is null && IsTraceableVersion(published)))
            {
                Trace.TraceInformation(
                    "OpenBlade could not compare {0} firmware versions. Installed: '{1}'; published: '{2}'.",
                    label,
                    installed ?? "Unavailable",
                    published ?? "Not listed");
            }
            return;
        }

        if (publishedVersion > installedVersion)
        {
            newerComponents.Add($"{label} {published!.Trim()}");
        }
    }

    private static Version? ParseComparableVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string candidate = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(candidate, out Version? version)
            ? version
            : null;
    }

    private static bool IsTraceableVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(
            value.Trim(),
            "Unavailable",
            StringComparison.OrdinalIgnoreCase);

    private string CreateNotCheckedMessage() =>
        lastSuccessfulCheckAt is DateTimeOffset checkedAt
            ? $"Last checked {checkedAt.ToLocalTime():g}. Check again to load published versions."
            : NotCheckedMessage;

    private void RestoreCachedVersionsForCurrentModel()
    {
        bool modelMatches = supportPage is not null
            && cachedModelNumber is not null
            && string.Equals(
                cachedModelNumber,
                supportPage.ModelNumber,
                StringComparison.OrdinalIgnoreCase);
        bool hasCachedVersion = cachedBiosVersion is not null
            || cachedEcVersion is not null
            || cachedMcuVersion is not null;
        if (lastSuccessfulCheckAt is not null && modelMatches && hasCachedVersion)
        {
            var published = new RazerPublishedFirmwareVersions(
                cachedBiosVersion,
                cachedEcVersion,
                cachedMcuVersion,
                cachedBiosStatus ?? RazerPublishedFirmwareVersionStatus.Unreadable,
                cachedEcStatus ?? RazerPublishedFirmwareVersionStatus.Unreadable,
                cachedMcuStatus ?? RazerPublishedFirmwareVersionStatus.Unreadable);
            PublishedBiosVersion = PublishedValue(
                published.BiosVersion,
                published.BiosStatus);
            PublishedEcVersion = PublishedValue(
                published.EcVersion,
                published.EcStatus);
            PublishedMcuVersion = PublishedValue(
                published.McuVersion,
                published.McuStatus);
            PresentUpdateAvailability(supportPage!, published);
            StatusText = IsUpdateAvailable
                ? UpdateAvailableSummary
                : $"Latest firmware versions last loaded {lastSuccessfulCheckAt.Value.ToLocalTime():g}.";
            return;
        }

        IsUpdateAvailable = false;
        UpdateAvailableSummary = string.Empty;
        PublishedBiosVersion = "Not checked";
        PublishedEcVersion = "Not checked";
        PublishedMcuVersion = "Not checked";
        StatusText = CreateNotCheckedMessage();

        bool successfulCacheIsInvalid = lastSuccessfulCheckAt is not null
            && (!modelMatches || !hasCachedVersion);
        bool retryBelongsToAnotherModel = retryAfter is not null && !modelMatches;
        if (supportPage is not null
            && (successfulCacheIsInvalid || retryBelongsToAnotherModel))
        {
            lastSuccessfulCheckAt = null;
            retryAfter = null;
            cachedModelNumber = null;
            cachedBiosVersion = null;
            cachedEcVersion = null;
            cachedMcuVersion = null;
            cachedBiosStatus = null;
            cachedEcStatus = null;
            cachedMcuStatus = null;
            Notify(nameof(LastSuccessfulCheckAt));
            Notify(nameof(RetryAfter));
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string FirmwareValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "Unavailable", StringComparison.OrdinalIgnoreCase)
            ? "Unavailable"
            : value.Trim();

    private static string PublishedValue(
        string? value,
        RazerPublishedFirmwareVersionStatus status) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : status == RazerPublishedFirmwareVersionStatus.NotListed
                ? "Not listed"
                : "Couldn't read";

    private static string? NormalizeCachedVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static RazerPublishedFirmwareVersionStatus? NormalizeCachedStatus(
        RazerPublishedFirmwareVersionStatus? status,
        string? value)
    {
        if (value is not null)
        {
            return RazerPublishedFirmwareVersionStatus.Listed;
        }

        return status is RazerPublishedFirmwareVersionStatus.NotListed
            or RazerPublishedFirmwareVersionStatus.Unreadable
                ? status
                : null;
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
