using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Panel = Microsoft.UI.Xaml.Controls.Panel;

namespace OpenBlade.Tray;

/// <summary>Explicit membership; nested editors and generated item controls are never inferred.</summary>
public static class CardReorder
{
    public static readonly DependencyProperty GroupIdProperty = DependencyProperty.RegisterAttached(
        "GroupId", typeof(string), typeof(CardReorder), new PropertyMetadata(null));
    public static readonly DependencyProperty CardIdProperty = DependencyProperty.RegisterAttached(
        "CardId", typeof(string), typeof(CardReorder), new PropertyMetadata(null));
    public static readonly DependencyProperty IsHeaderProperty = DependencyProperty.RegisterAttached(
        "IsHeader", typeof(bool), typeof(CardReorder), new PropertyMetadata(false));

    public static string? GetGroupId(DependencyObject element) => (string?)element.GetValue(GroupIdProperty);
    public static void SetGroupId(DependencyObject element, string value) => element.SetValue(GroupIdProperty, value);
    public static string? GetCardId(DependencyObject element) => (string?)element.GetValue(CardIdProperty);
    public static void SetCardId(DependencyObject element, string value) => element.SetValue(CardIdProperty, value);
    public static bool GetIsHeader(DependencyObject element) => (bool)element.GetValue(IsHeaderProperty);
    public static void SetIsHeader(DependencyObject element, bool value) => element.SetValue(IsHeaderProperty, value);
}

public sealed partial class MainWindow
{
    private readonly Dictionary<string, IReadOnlyList<string>> savedCardOrders = new(StringComparer.Ordinal);
    private readonly Dictionary<Panel, CardRowGroup> cardRowGroups = [];
    private readonly HashSet<FrameworkElement> cardGroupRoots = [];
    private bool restoringCardOrders;
    private bool cardReorderDisposed;

    private sealed record CardRowGroup(string Id, ObservableCollection<FrameworkElement> Items);

    private static bool CanStartSectionReorder(DependencyObject origin, FrameworkElement presentation)
    {
        for (DependencyObject? current = origin; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (CardReorder.GetIsHeader(current))
            {
                return true;
            }
            if (ReferenceEquals(current, presentation))
            {
                break;
            }
        }
        return !HasSectionHeading(presentation);
    }

    private static bool HasSectionHeading(DependencyObject root)
    {
        if (CardReorder.GetIsHeader(root))
        {
            return true;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (HasSectionHeading(VisualTreeHelper.GetChild(root, index)))
            {
                return true;
            }
        }
        return false;
    }

    private void RegisterCardGroups(FrameworkElement presentation, string scope)
    {
        if (cardReorderDisposed)
        {
            return;
        }
        if (cardGroupRoots.Add(presentation))
        {
            // Templates may not exist yet when the containing page is first registered.
            presentation.Loaded += (_, _) => RegisterCardGroupDescendants(presentation, scope);
        }
        RegisterCardGroupDescendants(presentation, scope);
    }

    private void RegisterCardGroupDescendants(DependencyObject root, string scope)
    {
        if (cardReorderDisposed)
        {
            return;
        }
        if (root is Panel panel && CardReorder.GetGroupId(panel) is string groupId
            && !cardRowGroups.ContainsKey(panel))
        {
            var rows = panel.Children.OfType<FrameworkElement>()
                .Where(row => CardReorder.GetCardId(row) is not null).ToArray();
            if (rows.Length > 1)
            {
                string id = $"{scope}.{groupId}";
                var group = new CardRowGroup(id, new ObservableCollection<FrameworkElement>(rows));
                cardRowGroups.Add(panel, group);
                ApplySavedCardOrder(panel, group);
                foreach (FrameworkElement row in group.Items)
                {
                    liveReorder.Register(row, group.Items, row,
                        HasSectionHeading(row) ? origin => CanStartSectionReorder(origin, row) : null);
                    row.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
                    {
                        if (!cardReorderDisposed)
                        {
                            UpdateCardRowStyles(group);
                        }
                    });
                }
                group.Items.CollectionChanged += (_, _) =>
                {
                    RefreshCardRows(panel, group);
                    if (restoringCardOrders || cardReorderDisposed)
                    {
                        return;
                    }
                    savedCardOrders[id] = group.Items.Select(row => CardReorder.GetCardId(row)!).ToArray();
                    QueuePreferenceSave();
                };
            }
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            RegisterCardGroupDescendants(VisualTreeHelper.GetChild(root, index), scope);
        }
    }

    private void RestoreCardOrders(IReadOnlyList<CardOrderPreference>? preferences)
    {
        restoringCardOrders = true;
        try
        {
            savedCardOrders.Clear();
            foreach (CardOrderPreference preference in preferences ?? [])
            {
                if (preference is not null && !string.IsNullOrWhiteSpace(preference.GroupId)
                    && preference.CardIds is not null)
                {
                    savedCardOrders.TryAdd(preference.GroupId, preference.CardIds);
                }
            }
            foreach ((Panel panel, CardRowGroup group) in cardRowGroups)
            {
                ApplySavedCardOrder(panel, group);
            }
        }
        finally
        {
            restoringCardOrders = false;
        }
    }

    private void ApplySavedCardOrder(Panel panel, CardRowGroup group)
    {
        string[] known = group.Items.Select(row => CardReorder.GetCardId(row)!).ToArray();
        savedCardOrders.TryGetValue(group.Id, out IReadOnlyList<string>? saved);
        IReadOnlyList<string> order = CardOrderPreferencePolicy.Normalize(saved, known);
        // Restoration also suppresses saves for template groups already created before load.
        var byId = group.Items.ToDictionary(row => CardReorder.GetCardId(row)!, StringComparer.Ordinal);
        for (int index = 0; index < order.Count; index++)
        {
            FrameworkElement row = byId[order[index]];
            int current = group.Items.IndexOf(row);
            if (current != index)
            {
                group.Items.Move(current, index);
            }
        }
        RefreshCardRows(panel, group);
    }

    private void RefreshCardRows(Panel panel, CardRowGroup group)
    {
        UpdateCardRowStyles(group);
        // Move existing live controls, preserving bindings, editor state and collapsed siblings.
        UIElement[] desired = panel.Children.ToArray();
        int member = 0;
        for (int index = 0; index < desired.Length; index++)
        {
            if (desired[index] is FrameworkElement row && group.Items.Contains(row))
            {
                desired[index] = group.Items[member++];
            }
        }
        for (int index = 0; index < desired.Length; index++)
        {
            int current = panel.Children.IndexOf(desired[index]);
            if (current != index)
            {
                panel.Children.Move(checked((uint)current), checked((uint)index));
            }
        }
    }

    private void UpdateCardRowStyles(CardRowGroup group)
    {
        FrameworkElement? lastVisible = group.Items.LastOrDefault(row => row.Visibility == Visibility.Visible);
        foreach (FrameworkElement row in group.Items)
        {
            foreach ((string normal, string last) in new[]
            {
                ("SettingsRowStyle", "SettingsLastRowStyle"),
                ("SettingsNeutralSubcardStyle", "SettingsLastNeutralSubcardStyle"),
                ("SettingsEditorSubcardStyle", "SettingsLastEditorSubcardStyle"),
                ("SettingsEmphasizedSubcardStyle", "SettingsLastEmphasizedSubcardStyle"),
            })
            {
                if (ReferenceEquals(row.Style, Root.Resources[normal])
                    || ReferenceEquals(row.Style, Root.Resources[last]))
                {
                    row.Style = (Style)Root.Resources[ReferenceEquals(row, lastVisible) ? last : normal];
                    break;
                }
            }
        }
    }

    private IReadOnlyList<CardOrderPreference> SnapshotCardOrders() => savedCardOrders
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new CardOrderPreference(pair.Key, pair.Value.ToArray()))
        .ToArray();
}
