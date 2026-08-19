using System.Buffers;
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
    string? LastKnownPublishedMcuVersion = null,
    string? LastKnownPublishedVbiosVersion = null,
    string? LastKnownPublishedVbiosGpuName = null,
    string? LastKnownPublishedThunderboltVersion = null,
    string? LastKnownPublishedTouchpadVersion = null,
    RazerPublishedFirmwareVersionStatus? LastKnownPublishedBiosStatus = null,
    RazerPublishedFirmwareVersionStatus? LastKnownPublishedEcStatus = null,
    RazerPublishedFirmwareVersionStatus? LastKnownPublishedMcuStatus = null,
    RazerPublishedFirmwareVersionStatus? LastKnownPublishedVbiosStatus = null,
    RazerPublishedFirmwareVersionStatus? LastKnownPublishedThunderboltStatus = null,
    RazerPublishedFirmwareVersionStatus? LastKnownPublishedTouchpadStatus = null,
    NotificationPreferences? Notifications = null,
    string? LastNotifiedReleaseTag = null,
    string? LastNotifiedFirmwareSignature = null,
    TrayDashboardPreference? TrayDashboard = null,
    bool LabFeaturesEnabled = false,
    UiLanguagePreference Language = UiLanguagePreference.FollowSystem)
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
        LastKnownPublishedMcuVersion: null,
        LastKnownPublishedVbiosVersion: null,
        LastKnownPublishedVbiosGpuName: null,
        LastKnownPublishedThunderboltVersion: null,
        LastKnownPublishedTouchpadVersion: null,
        LastKnownPublishedBiosStatus: null,
        LastKnownPublishedEcStatus: null,
        LastKnownPublishedMcuStatus: null,
        LastKnownPublishedVbiosStatus: null,
        LastKnownPublishedThunderboltStatus: null,
        LastKnownPublishedTouchpadStatus: null,
        Notifications: NotificationPreferences.Default,
        LastNotifiedReleaseTag: null,
        LastNotifiedFirmwareSignature: null,
        TrayDashboard: TrayDashboardPreferenceDefaults.Preference,
        LabFeaturesEnabled: false,
        Language: UiLanguagePreference.FollowSystem);

    public UiLanguagePreference EffectiveLanguage =>
        UiLanguagePreferencePolicy.Normalize(Language);

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

    public NotificationPreferences EffectiveNotifications =>
        Notifications ?? NotificationPreferences.Default;

    public TrayDashboardPreference EffectiveTrayDashboard
    {
        get
        {
            // Normalization allocates a detached snapshot. Cache the result when a
            // caller needs it repeatedly instead of treating this as a hot-path getter.
            return TrayDashboardPreferenceDefaults.Normalize(TrayDashboard);
        }
    }
}

public sealed class UiPreferenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private readonly string path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenBlade",
        "preferences.json");

    public static UiPreferenceStore CreateDefault() => new(DefaultPath);

    public UiPreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public UiLanguagePreference LoadLanguagePreference()
    {
        if (!File.Exists(path))
        {
            return UiLanguagePreference.FollowSystem;
        }

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return UiLanguagePreference.FollowSystem;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("language", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String
                    && Enum.TryParse(
                        property.Value.GetString(),
                        ignoreCase: true,
                        out UiLanguagePreference namedPreference))
                {
                    return UiLanguagePreferencePolicy.Normalize(namedPreference);
                }
                if (property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out int numericPreference))
                {
                    return UiLanguagePreferencePolicy.Normalize(
                        (UiLanguagePreference)numericPreference);
                }
                return UiLanguagePreference.FollowSystem;
            }
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or SecurityException)
        {
        }

        return UiLanguagePreference.FollowSystem;
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
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            UiPreferences? preferences = TryGetFutureDashboard(
                    document.RootElement,
                    out _,
                    out int schemaVersion)
                ? DeserializeWithFutureDashboardPlaceholder(
                    document.RootElement,
                    schemaVersion)
                : document.RootElement.Deserialize<UiPreferences>(SerializerOptions);
            return preferences ?? UiPreferences.Default;
        }
        catch (JsonException)
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
            string? futureDashboardJson =
                await ReadFutureDashboardJsonAsync(cancellationToken)
                    .ConfigureAwait(false);
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
                    if (futureDashboardJson is null)
                    {
                        await JsonSerializer.SerializeAsync(
                            stream,
                            preferences,
                            SerializerOptions,
                            cancellationToken);
                    }
                    else
                    {
                        await WritePreferencesWithPreservedDashboardAsync(
                            stream,
                            preferences,
                            futureDashboardJson,
                            cancellationToken).ConfigureAwait(false);
                    }
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

    private async Task<string?> ReadFutureDashboardJsonAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
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
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return TryGetFutureDashboard(
                    document.RootElement,
                    out JsonElement dashboard,
                    out _)
                ? dashboard.GetRawText()
                : null;
        }
        catch (JsonException)
        {
            // LoadAsync already treats malformed JSON as defaults.
            return null;
        }
    }

    private static UiPreferences? DeserializeWithFutureDashboardPlaceholder(
        JsonElement root,
        int schemaVersion)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRootWithDashboard(
                writer,
                root,
                dashboardWriter =>
                {
                    dashboardWriter.WriteStartObject();
                    dashboardWriter.WriteNumber("schemaVersion", schemaVersion);
                    dashboardWriter.WriteEndObject();
                });
        }

        return JsonSerializer.Deserialize<UiPreferences>(
            buffer.WrittenSpan,
            SerializerOptions);
    }

    private static async Task WritePreferencesWithPreservedDashboardAsync(
        Stream stream,
        UiPreferences preferences,
        string futureDashboardJson,
        CancellationToken cancellationToken)
    {
        JsonElement serialized = JsonSerializer.SerializeToElement(
            preferences,
            SerializerOptions);
        using var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Indented = true });
        WriteRootWithDashboard(
            writer,
            serialized,
            dashboardWriter => dashboardWriter.WriteRawValue(futureDashboardJson));
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteRootWithDashboard(
        Utf8JsonWriter writer,
        JsonElement root,
        Action<Utf8JsonWriter> writeDashboard)
    {
        writer.WriteStartObject();
        bool dashboardWritten = false;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name.Equals(
                    "trayDashboard",
                    StringComparison.OrdinalIgnoreCase))
            {
                writer.WritePropertyName(property.Name);
                writeDashboard(writer);
                dashboardWritten = true;
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        if (!dashboardWritten)
        {
            writer.WritePropertyName("trayDashboard");
            writeDashboard(writer);
        }
        writer.WriteEndObject();
    }

    private static bool TryGetFutureDashboard(
        JsonElement root,
        out JsonElement dashboard,
        out int schemaVersion)
    {
        dashboard = default;
        schemaVersion = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!property.Name.Equals(
                    "trayDashboard",
                    StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty dashboardProperty in property.Value.EnumerateObject())
            {
                if (dashboardProperty.Name.Equals(
                        "schemaVersion",
                        StringComparison.OrdinalIgnoreCase)
                    && dashboardProperty.Value.TryGetInt32(out schemaVersion)
                    && schemaVersion > TrayDashboardPreferenceDefaults.CurrentSchemaVersion)
                {
                    dashboard = property.Value;
                    return true;
                }
            }
        }

        return false;
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
