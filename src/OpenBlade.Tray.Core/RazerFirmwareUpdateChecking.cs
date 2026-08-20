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
    Uri OfficialPageUri,
    bool PublishesThunderboltFirmware = false,
    bool PublishesTouchpadFirmware = false);

public enum RazerPublishedFirmwareVersionStatus
{
    Listed,
    NotListed,
    Unreadable,
}

public sealed record RazerPublishedVbiosVersion(
    string Version,
    string? GpuName = null);

public sealed record RazerPublishedFirmwareVersions(
    string? BiosVersion,
    string? EcVersion,
    string? McuVersion,
    RazerPublishedFirmwareVersionStatus BiosStatus =
        RazerPublishedFirmwareVersionStatus.Listed,
    RazerPublishedFirmwareVersionStatus EcStatus =
        RazerPublishedFirmwareVersionStatus.Listed,
    RazerPublishedFirmwareVersionStatus McuStatus =
        RazerPublishedFirmwareVersionStatus.Listed,
    IReadOnlyList<RazerPublishedVbiosVersion>? VbiosVersions = null,
    RazerPublishedFirmwareVersionStatus VbiosStatus =
        RazerPublishedFirmwareVersionStatus.NotListed,
    string? ThunderboltVersion = null,
    RazerPublishedFirmwareVersionStatus ThunderboltStatus =
        RazerPublishedFirmwareVersionStatus.NotListed,
    string? TouchpadVersion = null,
    RazerPublishedFirmwareVersionStatus TouchpadStatus =
        RazerPublishedFirmwareVersionStatus.NotListed)
{
    public IReadOnlyList<RazerPublishedVbiosVersion> EffectiveVbiosVersions =>
        VbiosVersions ?? [];
}

public static class RazerFirmwareSupportCatalog
{
    private static readonly RazerFirmwareSupportPage[] Pages =
    [
        new(
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            new Uri("https://mysupport.razer.com/app/answers/detail/a_id/20854/~/razer-blade-16%E2%80%9D-%282026%29-%7C-rz09-0581x-firmware-updaters"),
            PublishesThunderboltFirmware: true,
            PublishesTouchpadFirmware: true),
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
        ParsedVbiosVersions vbios = FindVbiosVersions(text);
        ParsedVersion thunderbolt = FindVersion(
            text,
            ThunderboltLabelRegex(),
            ThunderboltVersionRegex());
        ParsedVersion touchpad = FindVersion(
            text,
            TouchpadLabelRegex(),
            TouchpadVersionRegex());
        return new(
            bios.Value,
            ec.Value,
            mcu.Value,
            bios.Status,
            ec.Status,
            mcu.Status,
            vbios.Versions,
            vbios.Status,
            thunderbolt.Value,
            thunderbolt.Status,
            touchpad.Value,
            touchpad.Status);
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

    private static ParsedVbiosVersions FindVbiosVersions(string text)
    {
        MatchCollection blocks = VbiosBlockRegex().Matches(text);
        if (blocks.Count == 0)
        {
            return new([], RazerPublishedFirmwareVersionStatus.NotListed);
        }

        var gpuSpecific = new List<RazerPublishedVbiosVersion>();
        foreach (Match block in blocks)
        {
            string body = block.Groups["body"].Value;
            MatchCollection gpuVersions = VbiosGpuVersionRegex().Matches(body);
            foreach (Match match in gpuVersions)
            {
                gpuSpecific.Add(new(
                    match.Groups["version"].Value,
                    WhitespaceRegex().Replace(match.Groups["gpu"].Value, " ")));
            }
        }

        IReadOnlyList<RazerPublishedVbiosVersion> versions = gpuSpecific
            .GroupBy(
                version => $"{version.GpuName?.Trim()}|{version.Version.Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return new(
            versions,
            versions.Count > 0
                ? RazerPublishedFirmwareVersionStatus.Listed
                : RazerPublishedFirmwareVersionStatus.Unreadable);
    }

    private readonly record struct ParsedVbiosVersions(
        IReadOnlyList<RazerPublishedVbiosVersion> Versions,
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

    [GeneratedRegex(
        "\\bvBIOS\\s+Firmware\\s+Updater\\b(?<body>(?:(?!\\bUpdater\\b).){0,500})",
        RegexOptions.IgnoreCase)]
    private static partial Regex VbiosBlockRegex();

    [GeneratedRegex(
        "\\b(?<gpu>RTX\\s+\\d{4})\\s*:\\s*v\\s*(?<version>[0-9A-F]+(?:\\.[0-9A-F]+)+)\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex VbiosGpuVersionRegex();

    [GeneratedRegex(
        "\\bThunderbolt(?:\\s+5)?\\s+Firmware\\s+Updater\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ThunderboltLabelRegex();

    [GeneratedRegex(
        "\\bThunderbolt(?:\\s+5)?\\s+Firmware\\s+Updater\\b(?:(?!\\bUpdater\\b).){0,300}?\\bv\\s*(?<version>\\d+(?:\\.\\d+)+)\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ThunderboltVersionRegex();

    [GeneratedRegex(
        "\\bTouchpad\\s+Firmware\\s+Updater\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TouchpadLabelRegex();

    [GeneratedRegex(
        "\\bTouchpad\\s+Firmware\\s+Updater\\b(?:(?!\\bUpdater\\b).){0,300}?\\bv\\s*(?<version>\\d+(?:\\.\\d+)+)\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TouchpadVersionRegex();
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
            versions = versions with
            {
                ThunderboltVersion = supportPage.PublishesThunderboltFirmware
                    ? versions.ThunderboltVersion
                    : null,
                ThunderboltStatus = supportPage.PublishesThunderboltFirmware
                    ? versions.ThunderboltStatus
                    : RazerPublishedFirmwareVersionStatus.NotListed,
                TouchpadVersion = supportPage.PublishesTouchpadFirmware
                    ? versions.TouchpadVersion
                    : null,
                TouchpadStatus = supportPage.PublishesTouchpadFirmware
                    ? versions.TouchpadStatus
                    : RazerPublishedFirmwareVersionStatus.NotListed,
            };
            if (versions.BiosVersion is null
                && versions.EcVersion is null
                && versions.McuVersion is null
                && versions.EffectiveVbiosVersions.Count == 0
                && versions.ThunderboltVersion is null
                && versions.TouchpadVersion is null)
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
    Uri officialSupportUri,
    string? legacySignature = null,
    bool includesThunderboltOrTouchpadUpdate = false) : EventArgs
{
    public string Signature { get; } = signature;

    public string? LegacySignature { get; } = legacySignature;

    public bool IncludesThunderboltOrTouchpadUpdate { get; } =
        includesThunderboltOrTouchpadUpdate;

    public string Summary { get; } = summary;

    public Uri OfficialSupportUri { get; } = officialSupportUri;

    public bool CanMigrateLegacySignature(string? persistedSignature) =>
        !IncludesThunderboltOrTouchpadUpdate
        && LegacySignature is not null
        && string.Equals(
            persistedSignature,
            LegacySignature,
            StringComparison.Ordinal);
}

public sealed class FirmwareUpdateAssistantViewModel :
    INotifyPropertyChanged,
    IAsyncDisposable
{
    internal static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(5);
    private static string NotCheckedMessage =>
        TrayText.Get("Check Razer's official page for the latest firmware versions.");
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
    private string? cachedVbiosVersion;
    private string? cachedVbiosGpuName;
    private string? cachedThunderboltVersion;
    private string? cachedTouchpadVersion;
    private RazerPublishedFirmwareVersionStatus? cachedBiosStatus;
    private RazerPublishedFirmwareVersionStatus? cachedEcStatus;
    private RazerPublishedFirmwareVersionStatus? cachedMcuStatus;
    private RazerPublishedFirmwareVersionStatus? cachedVbiosStatus;
    private RazerPublishedFirmwareVersionStatus? cachedThunderboltStatus;
    private RazerPublishedFirmwareVersionStatus? cachedTouchpadStatus;
    private string statusText = NotCheckedMessage;
    private string publishedBiosVersion = TrayText.Get("Not checked");
    private string publishedEcVersion = TrayText.Get("Not checked");
    private string publishedMcuVersion = TrayText.Get("Not checked");
    private string publishedVbiosVersion = TrayText.Get("Not checked");
    private string publishedThunderboltVersion = TrayText.Get("Not checked");
    private string publishedTouchpadVersion = TrayText.Get("Not checked");
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
        new(UpdateCheckInterval.EveryStartup, TrayText.Get("Every startup")),
        new(UpdateCheckInterval.Daily, TrayText.Get("Daily")),
        new(UpdateCheckInterval.Weekly, TrayText.Get("Weekly")),
        new(UpdateCheckInterval.Monthly, TrayText.Get("Monthly")),
        new(UpdateCheckInterval.ManualOnly, TrayText.Get("Manual only")),
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

    public string? CachedVbiosVersion => cachedVbiosVersion;

    public string? CachedVbiosGpuName => cachedVbiosGpuName;

    public string? CachedThunderboltVersion => cachedThunderboltVersion;

    public string? CachedTouchpadVersion => cachedTouchpadVersion;

    public RazerPublishedFirmwareVersionStatus? CachedBiosStatus => cachedBiosStatus;

    public RazerPublishedFirmwareVersionStatus? CachedEcStatus => cachedEcStatus;

    public RazerPublishedFirmwareVersionStatus? CachedMcuStatus => cachedMcuStatus;

    public RazerPublishedFirmwareVersionStatus? CachedVbiosStatus => cachedVbiosStatus;

    public RazerPublishedFirmwareVersionStatus? CachedThunderboltStatus =>
        cachedThunderboltStatus;

    public RazerPublishedFirmwareVersionStatus? CachedTouchpadStatus =>
        cachedTouchpadStatus;

    public ICommand CheckCommand => checkCommand;

    public bool IsSupported => supportPage is not null;

    public bool ShowsBiosFirmware =>
        HasReadableFirmwareValue(installedFirmware?.BiosVersion);

    public bool ShowsEcFirmware =>
        HasReadableFirmwareValue(installedFirmware?.EcVersion);

    public bool ShowsMcuFirmware =>
        HasReadableFirmwareValue(installedFirmware?.McuVersion);

    public bool ShowsVbiosFirmware =>
        HasReadableFirmwareValue(installedFirmware?.VbiosVersion);

    public bool ShowsThunderboltFirmware =>
        supportPage?.PublishesThunderboltFirmware == true
        && HasReadableFirmwareValue(installedFirmware?.ThunderboltFirmwareVersion);

    public bool ShowsTouchpadFirmware =>
        supportPage?.PublishesTouchpadFirmware == true
        && HasReadableFirmwareValue(installedFirmware?.TouchpadFirmwareVersion);

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

    public string InstalledVbiosVersion => FirmwareValue(installedFirmware?.VbiosVersion);

    public string InstalledThunderboltVersion =>
        FirmwareValue(installedFirmware?.ThunderboltFirmwareVersion);

    public string InstalledTouchpadVersion =>
        FirmwareValue(installedFirmware?.TouchpadFirmwareVersion);

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

    public string PublishedVbiosVersion
    {
        get => publishedVbiosVersion;
        private set => SetField(ref publishedVbiosVersion, value);
    }

    public string PublishedThunderboltVersion
    {
        get => publishedThunderboltVersion;
        private set => SetField(ref publishedThunderboltVersion, value);
    }

    public string PublishedTouchpadVersion
    {
        get => publishedTouchpadVersion;
        private set => SetField(ref publishedTouchpadVersion, value);
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
        Notify(nameof(ShowsBiosFirmware));
        Notify(nameof(ShowsEcFirmware));
        Notify(nameof(ShowsMcuFirmware));
        Notify(nameof(ShowsVbiosFirmware));
        Notify(nameof(ShowsThunderboltFirmware));
        Notify(nameof(ShowsTouchpadFirmware));
        Notify(nameof(OfficialSupportUri));
        Notify(nameof(InstalledBiosVersion));
        Notify(nameof(InstalledEcVersion));
        Notify(nameof(InstalledMcuVersion));
        Notify(nameof(InstalledVbiosVersion));
        Notify(nameof(InstalledThunderboltVersion));
        Notify(nameof(InstalledTouchpadVersion));
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
        RazerPublishedFirmwareVersionStatus? selectedCachedMcuStatus = null,
        string? selectedCachedVbiosVersion = null,
        string? selectedCachedVbiosGpuName = null,
        RazerPublishedFirmwareVersionStatus? selectedCachedVbiosStatus = null,
        string? selectedCachedThunderboltVersion = null,
        RazerPublishedFirmwareVersionStatus? selectedCachedThunderboltStatus = null,
        string? selectedCachedTouchpadVersion = null,
        RazerPublishedFirmwareVersionStatus? selectedCachedTouchpadStatus = null)
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
        cachedVbiosVersion = NormalizeCachedVersion(selectedCachedVbiosVersion);
        cachedVbiosGpuName = NormalizeCachedVersion(selectedCachedVbiosGpuName);
        cachedThunderboltVersion = NormalizeCachedVersion(
            selectedCachedThunderboltVersion);
        cachedTouchpadVersion = NormalizeCachedVersion(selectedCachedTouchpadVersion);
        cachedBiosStatus = NormalizeCachedStatus(
            selectedCachedBiosStatus,
            cachedBiosVersion);
        cachedEcStatus = NormalizeCachedStatus(selectedCachedEcStatus, cachedEcVersion);
        cachedMcuStatus = NormalizeCachedStatus(
            selectedCachedMcuStatus,
            cachedMcuVersion);
        cachedVbiosStatus = NormalizeCachedStatus(
            selectedCachedVbiosStatus,
            cachedVbiosVersion);
        cachedThunderboltStatus = NormalizeCachedStatus(
            selectedCachedThunderboltStatus,
            cachedThunderboltVersion);
        cachedTouchpadStatus = NormalizeCachedStatus(
            selectedCachedTouchpadStatus,
            cachedTouchpadVersion);
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
        StatusText = TrayText.Get("Checking Razer's official support page...");
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
            ResolvedPublishedVbios vbios = ResolvePublishedVbios(
                versions,
                installedFirmware?.VbiosGpuName);
            PublishedVbiosVersion = PublishedValue(vbios.Version, vbios.Status);
            string? thunderboltVersion = page.PublishesThunderboltFirmware
                ? versions.ThunderboltVersion
                : null;
            RazerPublishedFirmwareVersionStatus thunderboltStatus =
                page.PublishesThunderboltFirmware
                    ? versions.ThunderboltStatus
                    : RazerPublishedFirmwareVersionStatus.NotListed;
            string? touchpadVersion = page.PublishesTouchpadFirmware
                ? versions.TouchpadVersion
                : null;
            RazerPublishedFirmwareVersionStatus touchpadStatus =
                page.PublishesTouchpadFirmware
                    ? versions.TouchpadStatus
                    : RazerPublishedFirmwareVersionStatus.NotListed;
            RazerPublishedFirmwareVersions applicableVersions = versions with
            {
                ThunderboltVersion = thunderboltVersion,
                ThunderboltStatus = thunderboltStatus,
                TouchpadVersion = touchpadVersion,
                TouchpadStatus = touchpadStatus,
            };
            PublishedThunderboltVersion = PublishedValue(
                thunderboltVersion,
                thunderboltStatus);
            PublishedTouchpadVersion = PublishedValue(touchpadVersion, touchpadStatus);
            cachedModelNumber = page.ModelNumber;
            cachedBiosVersion = NormalizeCachedVersion(versions.BiosVersion);
            cachedEcVersion = NormalizeCachedVersion(versions.EcVersion);
            cachedMcuVersion = NormalizeCachedVersion(versions.McuVersion);
            cachedVbiosVersion = NormalizeCachedVersion(vbios.Version);
            cachedVbiosGpuName = NormalizeCachedVersion(vbios.GpuName);
            cachedThunderboltVersion = NormalizeCachedVersion(thunderboltVersion);
            cachedTouchpadVersion = NormalizeCachedVersion(touchpadVersion);
            cachedBiosStatus = versions.BiosStatus;
            cachedEcStatus = versions.EcStatus;
            cachedMcuStatus = versions.McuStatus;
            cachedVbiosStatus = vbios.Status;
            cachedThunderboltStatus = thunderboltStatus;
            cachedTouchpadStatus = touchpadStatus;
            PresentUpdateAvailability(page, applicableVersions, vbios);
            lastSuccessfulCheckAt = timeProvider.GetUtcNow();
            retryAfter = null;
            Notify(nameof(LastSuccessfulCheckAt));
            Notify(nameof(RetryAfter));
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
            StatusText = IsUpdateAvailable
                ? UpdateAvailableSummary
                : TrayText.Get("Latest firmware versions loaded from Razer.");
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
            StatusText = TrayText.Get(
                "Couldn't read Razer's page. The official link is still available.");
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade firmware update check failed unexpectedly: {0}",
                exception);
            RecordFailureRetry(page.ModelNumber);
            StatusText = TrayText.Get(
                "Couldn't read Razer's page. The official link is still available.");
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

    private static ResolvedPublishedVbios ResolvePublishedVbios(
        RazerPublishedFirmwareVersions published,
        string? installedGpuName)
    {
        if (published.VbiosStatus != RazerPublishedFirmwareVersionStatus.Listed)
        {
            return new(null, null, published.VbiosStatus);
        }

        IReadOnlyList<RazerPublishedVbiosVersion> versions =
            published.EffectiveVbiosVersions;
        RazerPublishedVbiosVersion[] gpuSpecific = versions
            .Where(version => !string.IsNullOrWhiteSpace(version.GpuName))
            .ToArray();
        if (gpuSpecific.Length == 0)
        {
            return new(null, null, RazerPublishedFirmwareVersionStatus.Unreadable);
        }

        string? installedGpu = NormalizeVbiosGpuName(installedGpuName);
        if (installedGpu is null)
        {
            return new(null, null, RazerPublishedFirmwareVersionStatus.Unreadable);
        }

        RazerPublishedVbiosVersion[] matches = gpuSpecific
            .Where(version => string.Equals(
                NormalizeVbiosGpuName(version.GpuName),
                installedGpu,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1
            ? new(
                matches[0].Version,
                installedGpu,
                RazerPublishedFirmwareVersionStatus.Listed)
            : new(null, null, RazerPublishedFirmwareVersionStatus.Unreadable);
    }

    private static string? NormalizeVbiosGpuName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(
                value.Trim(),
                "Unavailable",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Match match = Regex.Match(
            value,
            "\\bRTX\\s+(?<model>\\d{4})\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? $"RTX {match.Groups["model"].Value}"
            : null;
    }

    private readonly record struct ResolvedPublishedVbios(
        string? Version,
        string? GpuName,
        RazerPublishedFirmwareVersionStatus Status);

    private void PresentUpdateAvailability(
        RazerFirmwareSupportPage page,
        RazerPublishedFirmwareVersions published,
        ResolvedPublishedVbios vbios)
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
            installedFirmware?.McuVersion,
            published.McuVersion);
        AddNewerVbiosComponent(
            newerComponents,
            installedFirmware?.VbiosVersion,
            vbios.Version);
        int standardComponentCount = newerComponents.Count;
        AddNewerComponent(
            newerComponents,
            "Thunderbolt 5",
            installedFirmware?.ThunderboltFirmwareVersion,
            published.ThunderboltVersion);
        AddNewerComponent(
            newerComponents,
            "Touchpad",
            installedFirmware?.TouchpadFirmwareVersion,
            published.TouchpadVersion);
        bool includesThunderboltOrTouchpadUpdate =
            newerComponents.Count > standardComponentCount;

        IsUpdateAvailable = newerComponents.Count > 0;
        UpdateAvailableSummary = IsUpdateAvailable
            ? TrayText.Format(
                "Newer {0} firmware is available from Razer.",
                string.Join(", ", newerComponents))
            : string.Empty;
        if (!IsUpdateAvailable)
        {
            return;
        }

        string legacySignature = string.Join(
            '|',
            page.ModelNumber,
            $"bios={NormalizeCachedVersion(published.BiosVersion) ?? "-"}",
            $"ec={NormalizeCachedVersion(published.EcVersion) ?? "-"}",
            $"mcu={NormalizeCachedVersion(published.McuVersion) ?? "-"}",
            $"vbios={NormalizeCachedVersion(vbios.Version) ?? "-"}",
            $"vbiosGpu={NormalizeVbiosGpuName(vbios.GpuName) ?? "-"}");
        string signature = string.Join(
            '|',
            legacySignature,
            $"thunderbolt={NormalizeCachedVersion(published.ThunderboltVersion) ?? "-"}",
            $"touchpad={NormalizeCachedVersion(published.TouchpadVersion) ?? "-"}");
        UpdateAvailable?.Invoke(
            this,
            new FirmwareUpdateAvailableEventArgs(
                signature,
                UpdateAvailableSummary,
                page.OfficialPageUri,
                legacySignature,
                includesThunderboltOrTouchpadUpdate));
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
            newerComponents.Add(TrayText.Format("{0} {1}", label, published!.Trim()));
        }
    }

    private static void AddNewerVbiosComponent(
        ICollection<string> newerComponents,
        string? installed,
        string? published)
    {
        IReadOnlyList<uint>? installedVersion = ParseComparableVbiosVersion(installed);
        IReadOnlyList<uint>? publishedVersion = ParseComparableVbiosVersion(published);
        if (installedVersion is null || publishedVersion is null)
        {
            if ((installedVersion is null && IsTraceableVersion(installed))
                || (publishedVersion is null && IsTraceableVersion(published)))
            {
                Trace.TraceInformation(
                    "OpenBlade could not compare VBIOS firmware versions. Installed: '{0}'; published: '{1}'.",
                    installed ?? "Unavailable",
                    published ?? "Not listed");
            }
            return;
        }

        int componentCount = Math.Max(installedVersion.Count, publishedVersion.Count);
        for (int index = 0; index < componentCount; index++)
        {
            uint installedComponent = index < installedVersion.Count
                ? installedVersion[index]
                : 0;
            uint publishedComponent = index < publishedVersion.Count
                ? publishedVersion[index]
                : 0;
            if (publishedComponent == installedComponent)
            {
                continue;
            }

            if (publishedComponent > installedComponent)
            {
                newerComponents.Add(TrayText.Format("VBIOS {0}", published!.Trim()));
            }
            return;
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

    private static IReadOnlyList<uint>? ParseComparableVbiosVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] components = value.Trim().TrimStart('v', 'V').Split('.');
        if (components.Length < 2)
        {
            return null;
        }

        var parsed = new uint[components.Length];
        for (int index = 0; index < components.Length; index++)
        {
            if (!uint.TryParse(
                    components[index],
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsed[index]))
            {
                return null;
            }
        }

        return parsed;
    }

    private static bool IsTraceableVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(
            value.Trim(),
            "Unavailable",
            StringComparison.OrdinalIgnoreCase);

    private string CreateNotCheckedMessage() =>
        lastSuccessfulCheckAt is DateTimeOffset checkedAt
            ? TrayText.Format(
                "Last checked {0:g}. Check again to load published versions.",
                checkedAt.ToLocalTime())
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
            || cachedMcuVersion is not null
            || cachedVbiosVersion is not null
            || cachedThunderboltVersion is not null
            || cachedTouchpadVersion is not null;
        if (lastSuccessfulCheckAt is not null && modelMatches && hasCachedVersion)
        {
            bool hasCachedVbiosResult = cachedVbiosVersion is not null
                || cachedVbiosStatus is not null;
            bool cachedVbiosMatchesGpu = cachedVbiosVersion is null
                || cachedVbiosGpuName is null
                || string.Equals(
                    NormalizeVbiosGpuName(cachedVbiosGpuName),
                    NormalizeVbiosGpuName(installedFirmware?.VbiosGpuName),
                    StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<RazerPublishedVbiosVersion> cachedVbiosVersions =
                cachedVbiosVersion is not null && cachedVbiosMatchesGpu
                    ? [new(cachedVbiosVersion, cachedVbiosGpuName)]
                    : [];
            var published = new RazerPublishedFirmwareVersions(
                cachedBiosVersion,
                cachedEcVersion,
                cachedMcuVersion,
                cachedBiosStatus ?? RazerPublishedFirmwareVersionStatus.Unreadable,
                cachedEcStatus ?? RazerPublishedFirmwareVersionStatus.Unreadable,
                cachedMcuStatus ?? RazerPublishedFirmwareVersionStatus.Unreadable,
                cachedVbiosVersions,
                cachedVbiosMatchesGpu
                    ? cachedVbiosStatus
                        ?? RazerPublishedFirmwareVersionStatus.Unreadable
                    : RazerPublishedFirmwareVersionStatus.Unreadable,
                supportPage!.PublishesThunderboltFirmware
                    ? cachedThunderboltVersion
                    : null,
                supportPage.PublishesThunderboltFirmware
                    ? cachedThunderboltStatus
                        ?? RazerPublishedFirmwareVersionStatus.Unreadable
                    : RazerPublishedFirmwareVersionStatus.NotListed,
                supportPage.PublishesTouchpadFirmware
                    ? cachedTouchpadVersion
                    : null,
                supportPage.PublishesTouchpadFirmware
                    ? cachedTouchpadStatus
                        ?? RazerPublishedFirmwareVersionStatus.Unreadable
                    : RazerPublishedFirmwareVersionStatus.NotListed);
            ResolvedPublishedVbios vbios = ResolvePublishedVbios(
                published,
                installedFirmware?.VbiosGpuName);
            PublishedBiosVersion = PublishedValue(
                published.BiosVersion,
                published.BiosStatus);
            PublishedEcVersion = PublishedValue(
                published.EcVersion,
                published.EcStatus);
            PublishedMcuVersion = PublishedValue(
                published.McuVersion,
                published.McuStatus);
            PublishedVbiosVersion = hasCachedVbiosResult
                ? PublishedValue(vbios.Version, vbios.Status)
                : TrayText.Get("Not checked");
            PublishedThunderboltVersion = cachedThunderboltStatus is not null
                || cachedThunderboltVersion is not null
                    ? PublishedValue(
                        published.ThunderboltVersion,
                        published.ThunderboltStatus)
                    : TrayText.Get("Not checked");
            PublishedTouchpadVersion = cachedTouchpadStatus is not null
                || cachedTouchpadVersion is not null
                    ? PublishedValue(published.TouchpadVersion, published.TouchpadStatus)
                    : TrayText.Get("Not checked");
            PresentUpdateAvailability(supportPage!, published, vbios);
            StatusText = IsUpdateAvailable
                ? UpdateAvailableSummary
                : TrayText.Format(
                    "Latest firmware versions last loaded {0:g}.",
                    lastSuccessfulCheckAt.Value.ToLocalTime());
            return;
        }

        IsUpdateAvailable = false;
        UpdateAvailableSummary = string.Empty;
        PublishedBiosVersion = TrayText.Get("Not checked");
        PublishedEcVersion = TrayText.Get("Not checked");
        PublishedMcuVersion = TrayText.Get("Not checked");
        PublishedVbiosVersion = TrayText.Get("Not checked");
        PublishedThunderboltVersion = TrayText.Get("Not checked");
        PublishedTouchpadVersion = TrayText.Get("Not checked");
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
            cachedVbiosVersion = null;
            cachedVbiosGpuName = null;
            cachedThunderboltVersion = null;
            cachedTouchpadVersion = null;
            cachedBiosStatus = null;
            cachedEcStatus = null;
            cachedMcuStatus = null;
            cachedVbiosStatus = null;
            cachedThunderboltStatus = null;
            cachedTouchpadStatus = null;
            Notify(nameof(LastSuccessfulCheckAt));
            Notify(nameof(RetryAfter));
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string FirmwareValue(string? value) =>
        !HasReadableFirmwareValue(value)
            ? TrayText.Get("Unavailable")
            : value!.Trim();

    private static bool HasReadableFirmwareValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value.Trim(), "Unavailable", StringComparison.OrdinalIgnoreCase);

    private static string PublishedValue(
        string? value,
        RazerPublishedFirmwareVersionStatus status) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : status == RazerPublishedFirmwareVersionStatus.NotListed
                ? TrayText.Get("Not listed")
                : TrayText.Get("Couldn't read");

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
