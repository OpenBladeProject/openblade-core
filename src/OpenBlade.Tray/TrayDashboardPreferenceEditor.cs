using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace OpenBlade.Tray;

/// <summary>
/// Owns tray-dashboard customization state. Accepted edits are published before
/// availability changes so the window keeps its existing persistence chronology.
/// </summary>
internal sealed class TrayDashboardPreferenceEditor : IDisposable
{
    private IReadOnlyList<TrayDashboardControlOption> allControlOptions = [];
    private IReadOnlyList<TrayDashboardSliderOption> allSliderOptions = [];
    private bool changingEditor;

    internal TrayDashboardPreferenceEditor()
    {
        Controls.CollectionChanged += OnOrderChanged;
        Sliders.CollectionChanged += OnOrderChanged;
    }

    internal ObservableCollection<TrayDashboardControlOption> Controls { get; } = [];

    internal ObservableCollection<TrayDashboardSliderOption> Sliders { get; } = [];

    internal ObservableCollection<TrayDashboardStatusItemOption> StatusItems { get; } = [];

    internal IEnumerable<TrayDashboardControlOption> AvailableControls =>
        allControlOptions.Where(option => !option.IsVisible);

    internal IEnumerable<TrayDashboardSliderOption> AvailableSliders =>
        allSliderOptions.Where(option => !option.IsVisible);

    internal bool CanAddControl => AvailableControls.Any();

    internal bool CanAddSlider =>
        Sliders.Count < TrayDashboardPreferenceDefaults.MaximumVisibleSliders
        && AvailableSliders.Any();

    internal int VisibleStatusItemCount => StatusItems.Count(option => option.IsVisible);

    internal event Action<TrayDashboardPreference>? EditAccepted;

    internal event Action? AvailabilityChanged;

    public void Dispose()
    {
        Controls.CollectionChanged -= OnOrderChanged;
        Sliders.CollectionChanged -= OnOrderChanged;
        foreach (TrayDashboardStatusItemOption option in StatusItems)
        {
            option.PropertyChanged -= OnStatusChanged;
        }
    }

    internal void Load(TrayDashboardPreference preference)
    {
        changingEditor = true;
        try
        {
            foreach (TrayDashboardStatusItemOption option in StatusItems)
            {
                option.PropertyChanged -= OnStatusChanged;
            }

            allControlOptions =
                TrayDashboardPreferenceDefaults.CreateControlOptions(preference);
            allSliderOptions =
                TrayDashboardPreferenceDefaults.CreateSliderOptions(preference);

            Controls.Clear();
            foreach (TrayDashboardControlOption option in
                     allControlOptions.Where(option => option.IsVisible))
            {
                Controls.Add(option);
            }

            Sliders.Clear();
            foreach (TrayDashboardSliderOption option in
                     allSliderOptions.Where(option => option.IsVisible))
            {
                Sliders.Add(option);
            }

            StatusItems.Clear();
            foreach (TrayDashboardStatusItemOption option in
                     TrayDashboardPreferenceDefaults.CreateStatusOptions(preference))
            {
                option.PropertyChanged += OnStatusChanged;
                StatusItems.Add(option);
            }
        }
        finally
        {
            changingEditor = false;
        }

        UpdateAvailability();
    }

    internal void AddControl(TrayDashboardControlOption option)
    {
        if (option.IsVisible)
        {
            return;
        }

        option.IsVisible = true;
        Controls.Add(option);
    }

    internal void RemoveControl(TrayDashboardControlOption option)
    {
        if (!Controls.Contains(option))
        {
            return;
        }

        changingEditor = true;
        try
        {
            Controls.Remove(option);
            option.IsVisible = false;
        }
        finally
        {
            changingEditor = false;
        }

        AcceptEdit();
    }

    internal void AddSlider(TrayDashboardSliderOption option)
    {
        if (Sliders.Count >= TrayDashboardPreferenceDefaults.MaximumVisibleSliders
            || option.IsVisible)
        {
            return;
        }

        option.IsVisible = true;
        Sliders.Add(option);
    }

    internal void RemoveSlider(TrayDashboardSliderOption option)
    {
        if (!Sliders.Contains(option))
        {
            return;
        }

        changingEditor = true;
        try
        {
            Sliders.Remove(option);
            option.IsVisible = false;
        }
        finally
        {
            changingEditor = false;
        }

        AcceptEdit();
    }

    internal void Reset()
    {
        Load(TrayDashboardPreferenceDefaults.Preference);
        AcceptEdit();
    }

    internal TrayDashboardPreference ToPreference()
    {
        HashSet<TrayDashboardControlKind> activeControls =
            Controls.Select(option => option.Kind).ToHashSet();
        HashSet<TrayDashboardSliderKind> activeSliders =
            Sliders.Select(option => option.Kind).ToHashSet();
        return new TrayDashboardPreference(
            Controls: Controls
                .Select(option => new TrayDashboardControlPreference(option.Kind, true))
                .Concat(allControlOptions
                    .Where(option => !activeControls.Contains(option.Kind))
                    .Select(option => new TrayDashboardControlPreference(
                        option.Kind,
                        false)))
                .ToArray(),
            StatusItems: StatusItems
                .Select(option => new TrayDashboardStatusItemPreference(
                    option.Kind,
                    option.IsVisible))
                .ToArray(),
            Sliders: Sliders
                .Select(option => new TrayDashboardSliderPreference(option.Kind, true))
                .Concat(allSliderOptions
                    .Where(option => !activeSliders.Contains(option.Kind))
                    .Select(option => new TrayDashboardSliderPreference(
                        option.Kind,
                        false)))
                .ToArray(),
            SchemaVersion: TrayDashboardPreferenceDefaults.CurrentSchemaVersion);
    }

    private void OnStatusChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (changingEditor
            || args.PropertyName != nameof(TrayDashboardStatusItemOption.IsVisible))
        {
            return;
        }

        if (sender is TrayDashboardStatusItemOption { IsVisible: true } option
            && StatusItems.Count(status => status.IsVisible)
                > TrayDashboardPreferenceDefaults.MaximumVisibleStatusItems)
        {
            changingEditor = true;
            try
            {
                option.IsVisible = false;
            }
            finally
            {
                changingEditor = false;
            }

            UpdateAvailability();
            return;
        }

        AcceptEdit();
    }

    private void OnOrderChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        if (!changingEditor)
        {
            AcceptEdit();
        }
    }

    private void AcceptEdit()
    {
        EditAccepted?.Invoke(ToPreference());
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        int visibleStatusItemCount = VisibleStatusItemCount;
        foreach (TrayDashboardStatusItemOption option in StatusItems)
        {
            option.IsEnabled = option.IsVisible
                || visibleStatusItemCount
                    < TrayDashboardPreferenceDefaults.MaximumVisibleStatusItems;
        }

        AvailabilityChanged?.Invoke();
    }
}
