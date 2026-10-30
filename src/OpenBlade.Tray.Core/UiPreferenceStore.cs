using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

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
    UiLanguagePreference Language = UiLanguagePreference.FollowSystem,
    bool ThxAudioIntegrationEnabled = false,
    bool SnapTapEnabled = false,
    IReadOnlyList<TelemetryChartMetricPreference>? TelemetryChartMetrics = null,
    bool TelemetryEnabled = false,
    KeyboardInputBackend SnapTapBackend = KeyboardInputBackend.RazerDriver,
    KeyboardInputBackend FunctionRowBackend = KeyboardInputBackend.WindowsHook,
    IReadOnlyList<CardOrderPreference>? CardOrders = null)
{
    [JsonIgnore]
    public KeyboardInputBackend EffectiveSnapTapBackend => SnapTapBackend.Normalize();

    [JsonIgnore]
    public KeyboardInputBackend EffectiveFunctionRowBackend => FunctionRowBackend.Normalize();

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
        Language: UiLanguagePreference.FollowSystem,
        TelemetryEnabled: false);

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

    [JsonIgnore]
    public IReadOnlyList<TelemetryChartMetricPreference> EffectiveTelemetryChartMetrics =>
        TelemetryChartPreferenceDefaults.Normalize(TelemetryChartMetrics);

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
    internal const int MaximumFileSizeBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new KeyboardInputBackendJsonConverter(), new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private readonly string path;
    private readonly Action<string> diagnosticSink;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenBlade",
        "preferences.json");

    public static UiPreferenceStore CreateDefault() => new(DefaultPath);

    public UiPreferenceStore(string path)
        : this(path, message => Trace.TraceWarning("{0}", message))
    {
    }

    internal UiPreferenceStore(string path, Action<string> diagnosticSink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        this.diagnosticSink = diagnosticSink
            ?? throw new ArgumentNullException(nameof(diagnosticSink));
    }

    public UiLanguagePreference LoadLanguagePreference()
    {
        if (!File.Exists(path))
        {
            return UiLanguagePreference.FollowSystem;
        }

        try
        {
            using FileStream file = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using MemoryStream content = ReadBounded(file);
            using JsonDocument document = ParseContent(content);
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
            exception is JsonException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or SecurityException)
        {
            ReportDiagnostic(DiagnosticTrace.FormatException(
                "OpenBlade.Tray.UiPreferenceStore",
                "ReadLanguage",
                exception,
                exception is InvalidDataException
                    ? $"Preference input exceeds the {MaximumFileSizeBytes / 1024} KiB limit; "
                        + "following the system language. The file has not been changed."
                    : "Preference language could not be read; following the system language."));
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
            await using FileStream file = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using MemoryStream content = await ReadBoundedAsync(
                file,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument document = ParseContent(content);
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
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException)
        {
            ReportDiagnostic(DiagnosticTrace.FormatException(
                "OpenBlade.Tray.UiPreferenceStore",
                "LoadPreferences",
                exception,
                exception is InvalidDataException
                    ? $"Preference input exceeds the {MaximumFileSizeBytes / 1024} KiB limit; "
                        + "using default preferences. The file has not been changed."
                    : "Preference data is invalid; using default preferences. The file has not been changed."));

            return UiPreferences.Default;
        }
    }

    /// <summary>
    /// Reads only current-user input settings. Invalid input data propagates so
    /// the Session Agent can retain its last confirmed bindings and retry.
    /// </summary>
    public async Task<UiInputPreferences> LoadInputPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return new UiInputPreferences();
        }

        await using FileStream file = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using MemoryStream content = await ReadBoundedAsync(
            file,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using JsonDocument document = ParseContent(content);
        return document.RootElement.Deserialize<UiInputPreferences>(SerializerOptions)
            ?? throw new JsonException("Input preferences must be an object.");
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
            string? futureDashboardJson;
            bool preserveOversizedFile = false;
            try
            {
                futureDashboardJson =
                    await ReadFutureDashboardJsonAsync(cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // Retain unparsed oversized data when replacing the preferences.
                futureDashboardJson = null;
                preserveOversizedFile = true;
            }
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
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (stream.Length > MaximumFileSizeBytes)
                    {
                        throw OversizedPreferences();
                    }
                }

                if (preserveOversizedFile)
                {
                    string backupPath = Path.Combine(
                        directory,
                        $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.oversized.bak");
                    File.Replace(temporaryPath, path, backupPath);
                    ReportDiagnostic(
                        "Oversized preferences were preserved in an .oversized.bak file "
                        + "beside the preferences file before saving new preferences.");
                }
                else
                {
                    File.Move(temporaryPath, path, overwrite: true);
                }
            }
            finally
            {
                TryDeleteTemporary(temporaryPath);
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or SecurityException)
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
            await using FileStream file = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using MemoryStream content = await ReadBoundedAsync(
                file,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument document = ParseContent(content);
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

    private void ReportDiagnostic(string message)
    {
        try
        {
            diagnosticSink(message);
        }
        catch (Exception exception)
        {
            // Diagnostic failures must not change preference recovery or save outcomes.
            DiagnosticTrace.TraceException(
                "OpenBlade.Tray.UiPreferenceStore",
                "ReportDiagnostic",
                exception);
        }
    }

    private static JsonDocument ParseContent(MemoryStream content)
    {
        ReadOnlyMemory<byte> utf8Json = content.GetBuffer()
            .AsMemory(0, checked((int)content.Length));
        if (utf8Json.Span.StartsWith("\uFEFF"u8))
        {
            utf8Json = utf8Json[3..];
        }

        // The caller keeps content alive until the document has been disposed.
        return JsonDocument.Parse(utf8Json);
    }

    private static MemoryStream ReadBounded(FileStream stream)
    {
        EnsureDeclaredLengthIsBounded(stream);
        var content = new MemoryStream(InitialCapacity(stream));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                int remaining = MaximumFileSizeBytes - checked((int)content.Length);
                int count = stream.Read(
                    buffer,
                    0,
                    Math.Min(buffer.Length, remaining + 1));
                if (count == 0)
                {
                    content.Position = 0;
                    return content;
                }
                if (count > remaining)
                {
                    throw OversizedPreferences();
                }

                content.Write(buffer, 0, count);
            }
        }
        catch
        {
            content.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<MemoryStream> ReadBoundedAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        EnsureDeclaredLengthIsBounded(stream);
        var content = new MemoryStream(InitialCapacity(stream));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                int remaining = MaximumFileSizeBytes - checked((int)content.Length);
                int count = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    content.Position = 0;
                    return content;
                }
                if (count > remaining)
                {
                    throw OversizedPreferences();
                }

                content.Write(buffer, 0, count);
            }
        }
        catch
        {
            content.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureDeclaredLengthIsBounded(FileStream stream)
    {
        if (stream.Length > MaximumFileSizeBytes)
        {
            throw OversizedPreferences();
        }
    }

    private static int InitialCapacity(FileStream stream) =>
        checked((int)Math.Min(stream.Length, MaximumFileSizeBytes));

    private static InvalidDataException OversizedPreferences() => new(
        $"Preference files cannot exceed {MaximumFileSizeBytes / 1024} KiB.");

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
            DiagnosticTrace.TraceException(
                "OpenBlade.Tray.UiPreferenceStore",
                "DeleteTemporaryFile",
                exception);
        }
    }
}
