using System.ComponentModel;

namespace OpenBlade.Tray;

public enum LiveReadingItemKind
{
    BatteryPower,
    CpuTemperature,
    GpuTemperature,
    Fan1Speed,
    Fan2Speed,
}

public enum TrayHoverLabelItemKind
{
    // Schema 1 UI preference value. Normalization expands it to the two
    // independently configurable items below.
    ActiveProfile,
    BatteryPower,
    CpuTemperature,
    GpuTemperature,
    Fan1Speed,
    Fan2Speed,
    PowerSource,
    PowerMode,
}

public sealed record TrayHoverLabelItemPreference(
    TrayHoverLabelItemKind Kind,
    bool IsEnabled);

public sealed record LiveReadingGroupDisplayItem(string Label, string Value);

public sealed class LiveReadingVisibilityOption : INotifyPropertyChanged
{
    private bool isEnabled;

    public LiveReadingVisibilityOption(
        LiveReadingItemKind kind,
        string label,
        bool isEnabled)
    {
        Kind = kind;
        Label = label;
        this.isEnabled = isEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LiveReadingItemKind Kind { get; }

    public string Label { get; }

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (isEnabled == value)
            {
                return;
            }

            isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }
}

public sealed class TrayHoverLabelItemOption : INotifyPropertyChanged
{
    private bool isEnabled;

    public TrayHoverLabelItemOption(
        TrayHoverLabelItemKind kind,
        string label,
        bool isEnabled)
    {
        Kind = kind;
        Label = label;
        this.isEnabled = isEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TrayHoverLabelItemKind Kind { get; }

    public string Label { get; }

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (isEnabled == value)
            {
                return;
            }

            isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }
}

public static class TelemetryPreferenceDefaults
{
    public static readonly LiveReadingItemKind[] DashboardItems =
    [
        LiveReadingItemKind.BatteryPower,
        LiveReadingItemKind.CpuTemperature,
        LiveReadingItemKind.GpuTemperature,
        LiveReadingItemKind.Fan1Speed,
        LiveReadingItemKind.Fan2Speed,
    ];

    public static readonly TrayHoverLabelItemPreference[] TrayHoverItems =
    [
        new(TrayHoverLabelItemKind.PowerSource, true),
        new(TrayHoverLabelItemKind.PowerMode, true),
        new(TrayHoverLabelItemKind.BatteryPower, true),
        new(TrayHoverLabelItemKind.CpuTemperature, false),
        new(TrayHoverLabelItemKind.GpuTemperature, false),
        new(TrayHoverLabelItemKind.Fan1Speed, false),
        new(TrayHoverLabelItemKind.Fan2Speed, false),
    ];

    public static IReadOnlyList<LiveReadingItemKind> NormalizeDashboardItems(
        IEnumerable<LiveReadingItemKind>? enabled)
    {
        if (enabled is null)
        {
            return DashboardItems;
        }

        HashSet<LiveReadingItemKind> selected = enabled
            .Where(Enum.IsDefined)
            .ToHashSet();
        return DashboardItems.Where(selected.Contains).ToArray();
    }

    public static IReadOnlyList<TrayHoverLabelItemPreference> NormalizeTrayHoverItems(
        IEnumerable<TrayHoverLabelItemPreference>? preferences,
        TrayHoverLabelMode legacyMode)
    {
        if (preferences is null)
        {
            return FromLegacyMode(legacyMode);
        }

        List<TrayHoverLabelItemPreference> expanded = [];
        foreach (TrayHoverLabelItemPreference item in preferences.Where(item =>
                     item is not null && Enum.IsDefined(item.Kind)))
        {
            if (item.Kind == TrayHoverLabelItemKind.ActiveProfile)
            {
                expanded.Add(new TrayHoverLabelItemPreference(
                    TrayHoverLabelItemKind.PowerSource,
                    item.IsEnabled));
                expanded.Add(new TrayHoverLabelItemPreference(
                    TrayHoverLabelItemKind.PowerMode,
                    item.IsEnabled));
            }
            else
            {
                expanded.Add(item);
            }
        }

        List<TrayHoverLabelItemPreference> normalized = expanded
            .DistinctBy(item => item.Kind)
            .Select(item => new TrayHoverLabelItemPreference(item.Kind, item.IsEnabled))
            .ToList();
        HashSet<TrayHoverLabelItemKind> supplied = normalized
            .Select(item => item.Kind)
            .ToHashSet();
        foreach (TrayHoverLabelItemPreference fallback in TrayHoverItems)
        {
            if (!supplied.Contains(fallback.Kind))
            {
                normalized.Add(fallback with { IsEnabled = false });
            }
        }
        return normalized;
    }

    public static IReadOnlyList<TrayHoverLabelItemPreference> FromLegacyMode(
        TrayHoverLabelMode mode)
    {
        bool profile = mode is TrayHoverLabelMode.ActiveProfile
            or TrayHoverLabelMode.ProfileAndPowerFlow;
        bool power = mode is TrayHoverLabelMode.BatteryPowerFlow
            or TrayHoverLabelMode.ProfileAndPowerFlow;
        return TrayHoverItems
            .Select(item => item with
            {
                IsEnabled = item.Kind switch
                {
                    TrayHoverLabelItemKind.PowerSource => profile,
                    TrayHoverLabelItemKind.PowerMode => profile,
                    TrayHoverLabelItemKind.BatteryPower => power,
                    _ => false,
                },
            })
            .ToArray();
    }
}
