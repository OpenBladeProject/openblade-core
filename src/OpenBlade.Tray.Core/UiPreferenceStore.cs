using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Tray;

public enum LiveReadingsCadence
{
    Off = 0,
    EverySecond = 1,
    EveryTwoSeconds = 2,
    EveryFiveSeconds = 5,
    EveryTenSeconds = 10,
}

public sealed record UiPreferences(
    ThemePreference Theme,
    bool LiveReadingsEnabled = true,
    TrayHoverLabelMode TrayHoverLabelMode = TrayHoverLabelMode.ProfileAndPowerFlow,
    LiveReadingsCadence LiveReadingsRefreshCadence =
        LiveReadingsCadence.EveryTwoSeconds,
    bool StartMinimized = false,
    IReadOnlyList<LiveReadingItemKind>? DashboardTelemetryItems = null,
    IReadOnlyList<TrayHoverLabelItemPreference>? TrayHoverLabelItems = null,
    IReadOnlyList<DashboardCardPreference>? DashboardCards = null,
    IReadOnlyList<PrimaryNavigationItemKind>? PrimaryNavigationItems = null,
    AppAccentColorMode AccentColorMode = AppAccentColorMode.RazerGreen,
    string? CustomAccentColor = null,
    IReadOnlyList<SpecialKeyBindingPreference>? SpecialKeyBindings = null,
    IReadOnlyList<PageLayoutPreference>? PageLayouts = null,
    UpdateChannel UpdateChannel = UpdateChannel.Stable,
    UpdateCheckInterval UpdateCheckInterval = UpdateCheckInterval.Daily,
    bool UpdateCheckPromptShown = false,
    DateTimeOffset? LastSuccessfulUpdateCheckAt = null,
    DateTimeOffset? UpdateCheckRetryAfter = null,
    string? UpdateCheckEntityTag = null,
    string? LastKnownReleaseTag = null,
    string? LastKnownReleaseUrl = null,
    UpdateCheckInterval FirmwareUpdateCheckInterval = UpdateCheckInterval.ManualOnly,
    DateTimeOffset? LastSuccessfulFirmwareUpdateCheckAt = null,
    DateTimeOffset? FirmwareUpdateCheckRetryAfter = null,
    string? LastKnownFirmwareModelNumber = null,
    string? LastKnownPublishedBiosVersion = null,
    string? LastKnownPublishedEcVersion = null,
    string? LastKnownPublishedMcuVersion = null)
{
    public static UiPreferences Default { get; } = new(
        ThemePreference.FollowWindows,
        LiveReadingsEnabled: true,
        TrayHoverLabelMode: TrayHoverLabelMode.ProfileAndPowerFlow,
        LiveReadingsRefreshCadence: LiveReadingsCadence.EveryTwoSeconds,
        StartMinimized: false,
        DashboardTelemetryItems: TelemetryPreferenceDefaults.DashboardItems,
        TrayHoverLabelItems: TelemetryPreferenceDefaults.TrayHoverItems,
        DashboardCards: DashboardCardPreferenceDefaults.Cards,
        PrimaryNavigationItems: PrimaryNavigationPreferenceDefaults.Items,
        AccentColorMode: AppAccentColorMode.RazerGreen,
        CustomAccentColor: AppAccentColorPreferenceDefaults.RazerGreenHex,
        SpecialKeyBindings: SpecialKeyBindingDefaults.Bindings,
        PageLayouts: PageLayoutPreferenceDefaults.Layouts,
        UpdateChannel: UpdateChannel.Stable,
        UpdateCheckInterval: UpdateCheckInterval.Daily,
        UpdateCheckPromptShown: false,
        LastSuccessfulUpdateCheckAt: null,
        UpdateCheckRetryAfter: null,
        UpdateCheckEntityTag: null,
        LastKnownReleaseTag: null,
        LastKnownReleaseUrl: null,
        FirmwareUpdateCheckInterval: UpdateCheckInterval.ManualOnly,
        LastSuccessfulFirmwareUpdateCheckAt: null,
        FirmwareUpdateCheckRetryAfter: null,
        LastKnownFirmwareModelNumber: null,
        LastKnownPublishedBiosVersion: null,
        LastKnownPublishedEcVersion: null,
        LastKnownPublishedMcuVersion: null);

    public LiveReadingsCadence EffectiveLiveReadingsCadence =>
        !LiveReadingsEnabled
            ? LiveReadingsCadence.Off
            : Enum.IsDefined(LiveReadingsRefreshCadence)
                && LiveReadingsRefreshCadence != LiveReadingsCadence.Off
                    ? LiveReadingsRefreshCadence
                    : LiveReadingsCadence.EveryTwoSeconds;

    public IReadOnlyList<LiveReadingItemKind> EffectiveDashboardTelemetryItems =>
        TelemetryPreferenceDefaults.NormalizeDashboardItems(DashboardTelemetryItems);

    public IReadOnlyList<TrayHoverLabelItemPreference> EffectiveTrayHoverLabelItems =>
        TelemetryPreferenceDefaults.NormalizeTrayHoverItems(
            TrayHoverLabelItems,
            Enum.IsDefined(TrayHoverLabelMode)
                ? TrayHoverLabelMode
                : TrayHoverLabelMode.ProfileAndPowerFlow);

    public IReadOnlyList<DashboardCardPreference> EffectiveDashboardCards =>
        DashboardCardPreferenceDefaults.Normalize(DashboardCards);

    public IReadOnlyList<PrimaryNavigationItemKind> EffectivePrimaryNavigationItems =>
        PrimaryNavigationPreferenceDefaults.Normalize(PrimaryNavigationItems);

    public AppAccentColorMode EffectiveAccentColorMode =>
        Enum.IsDefined(AccentColorMode)
            ? AccentColorMode
            : AppAccentColorMode.RazerGreen;

    public string EffectiveCustomAccentColor =>
        AppAccentColorPreferenceDefaults.NormalizeCustomColor(CustomAccentColor);

    public IReadOnlyList<SpecialKeyBindingPreference> EffectiveSpecialKeyBindings =>
        SpecialKeyBindingDefaults.Normalize(SpecialKeyBindings);

    public IReadOnlyList<PageLayoutPreference> EffectivePageLayouts =>
        PageLayoutPreferenceDefaults.Normalize(PageLayouts);

    public UpdateCheckInterval EffectiveUpdateCheckInterval =>
        Enum.IsDefined(UpdateCheckInterval)
            ? UpdateCheckInterval
            : UpdateCheckInterval.Daily;

    public UpdateChannel EffectiveUpdateChannel =>
        Enum.IsDefined(UpdateChannel)
            ? UpdateChannel
            : UpdateChannel.Stable;

    public UpdateCheckInterval EffectiveFirmwareUpdateCheckInterval =>
        Enum.IsDefined(FirmwareUpdateCheckInterval)
            ? FirmwareUpdateCheckInterval
            : UpdateCheckInterval.ManualOnly;
}

public sealed class UiPreferenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private readonly string path;

    public UiPreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return UiPreferences.Default;
        }

        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            UiPreferences? preferences = await JsonSerializer.DeserializeAsync<UiPreferences>(
                stream,
                SerializerOptions,
                cancellationToken);
            return preferences ?? UiPreferences.Default;
        }
        catch (JsonException)
        {
            return UiPreferences.Default;
        }
        catch (IOException)
        {
            return UiPreferences.Default;
        }
    }

    public async Task SaveAsync(
        UiPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        string directory = Path.GetDirectoryName(path)!;
        string temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        preferences,
                        SerializerOptions,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                TryDeleteTemporary(temporaryPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new UiPreferenceSaveException(
                $"Preferences could not be written to '{path}'.",
                exception);
        }
    }

    private static void TryDeleteTemporary(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }
}
