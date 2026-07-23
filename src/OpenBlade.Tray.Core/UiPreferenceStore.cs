using System.IO;
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
    IReadOnlyList<SpecialKeyBindingPreference>? SpecialKeyBindings = null)
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
        SpecialKeyBindings: SpecialKeyBindingDefaults.Bindings);

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
            await using FileStream stream = File.OpenRead(path);
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
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

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
            File.Delete(temporaryPath);
        }
    }
}
