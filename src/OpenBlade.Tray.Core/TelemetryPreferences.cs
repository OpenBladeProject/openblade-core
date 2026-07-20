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
    ActiveProfile,
    BatteryPower,
    CpuTemperature,
    GpuTemperature,
    Fan1Speed,
    Fan2Speed,
}

public sealed record TrayHoverLabelItemPreference(
    TrayHoverLabelItemKind Kind,
    bool IsEnabled);

public sealed record LiveReadingDisplayItem(string Label, string Value);

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
        new(TrayHoverLabelItemKind.ActiveProfile, true),
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

        Dictionary<TrayHoverLabelItemKind, bool> supplied = preferences
            .Where(item => item is not null && Enum.IsDefined(item.Kind))
            .GroupBy(item => item.Kind)
            .ToDictionary(group => group.Key, group => group.First().IsEnabled);
        List<TrayHoverLabelItemPreference> normalized = preferences
            .Where(item => item is not null && Enum.IsDefined(item.Kind))
            .DistinctBy(item => item.Kind)
            .Select(item => new TrayHoverLabelItemPreference(item.Kind, item.IsEnabled))
            .ToList();
        foreach (TrayHoverLabelItemPreference fallback in TrayHoverItems)
        {
            if (!supplied.ContainsKey(fallback.Kind))
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
                    TrayHoverLabelItemKind.ActiveProfile => profile,
                    TrayHoverLabelItemKind.BatteryPower => power,
                    _ => false,
                },
            })
            .ToArray();
    }
}
