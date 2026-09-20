using System.Collections.Specialized;

namespace OpenBlade.Tray.Tests;

public sealed class TrayDashboardPreferenceEditorTests
{
    [Fact]
    public void LoadNormalizesUnknownDuplicatesAndLimitsWithoutPublishingAnEdit()
    {
        using var editor = new TrayDashboardPreferenceEditor();
        int edits = 0;
        int availability = 0;
        editor.EditAccepted += _ => edits++;
        editor.AvailabilityChanged += () => availability++;
        var input = new TrayDashboardPreference(
            Controls:
            [
                new(TrayDashboardControlKind.Lighting, false),
                new(TrayDashboardControlKind.Fans, true),
                new(TrayDashboardControlKind.Fans, false),
                new((TrayDashboardControlKind)999, true),
                new(TrayDashboardControlKind.Performance, true),
            ],
            StatusItems:
            [
                new(TrayDashboardStatusItemKind.FanSpeed, true),
                new(TrayDashboardStatusItemKind.FanSpeed, false),
                new((TrayDashboardStatusItemKind)999, true),
                new(TrayDashboardStatusItemKind.PowerSource, true),
                new(TrayDashboardStatusItemKind.Temperatures, true),
                new(TrayDashboardStatusItemKind.BatteryPower, true),
            ],
            Sliders:
            [
                new(TrayDashboardSliderKind.GpuMemoryClock, false),
                new(TrayDashboardSliderKind.GpuCoreClock, true),
                new(TrayDashboardSliderKind.GpuCoreClock, false),
                new((TrayDashboardSliderKind)999, true),
                new(TrayDashboardSliderKind.ChargeLimit, true),
                new(TrayDashboardSliderKind.LightingBrightness, true),
            ],
            SchemaVersion: TrayDashboardPreferenceDefaults.CurrentSchemaVersion);

        editor.Load(input);

        TrayDashboardPreference normalized = TrayDashboardPreferenceDefaults.Normalize(input);
        TrayDashboardPreference saved = editor.ToPreference();
        Assert.Equal(normalized.Controls!.Where(option => option.IsVisible)
            .Concat(normalized.Controls!.Where(option => !option.IsVisible)), saved.Controls);
        Assert.Equal(normalized.StatusItems, saved.StatusItems);
        Assert.Equal(normalized.Sliders!.Where(option => option.IsVisible)
            .Concat(normalized.Sliders!.Where(option => !option.IsVisible)), saved.Sliders);
        Assert.Equal(normalized.SchemaVersion, saved.SchemaVersion);
        Assert.Equal(0, edits);
        Assert.Equal(1, availability);
        Assert.Equal(3, editor.VisibleStatusItemCount);
        Assert.False(editor.CanAddSlider);
        Assert.All(editor.StatusItems.Where(option => !option.IsVisible),
            option => Assert.False(option.IsEnabled));

        var controls = editor.Controls;
        var sliders = editor.Sliders;
        var statuses = editor.StatusItems;
        editor.Load(saved);

        Assert.Same(controls, editor.Controls);
        Assert.Same(sliders, editor.Sliders);
        Assert.Same(statuses, editor.StatusItems);
        AssertPreferenceEqual(saved, editor.ToPreference());
        Assert.Equal(0, edits);
    }

    [Fact]
    public void ControlActionsPublishCompletedEditsOnceAndKeepVisibleThenHiddenOrder()
    {
        using var editor = CreateEditor();
        var edits = new List<TrayDashboardPreference>();
        editor.EditAccepted += edits.Add;
        TrayDashboardControlOption first = editor.Controls[0];
        TrayDashboardControlOption added = Assert.Single(editor.AvailableControls);

        editor.AddControl(added);
        Assert.Single(edits);
        Assert.Same(added, editor.Controls[^1]);
        Assert.True(added.IsVisible);
        Assert.False(editor.CanAddControl);
        editor.AddControl(added);
        Assert.Single(edits);

        editor.RemoveControl(first);
        Assert.Equal(2, edits.Count);
        Assert.False(first.IsVisible);
        Assert.DoesNotContain(first, editor.Controls);
        Assert.True(editor.CanAddControl);
        Assert.Equal(new TrayDashboardControlPreference(first.Kind, false),
            edits[^1].Controls![^1]);
        editor.RemoveControl(first);
        Assert.Equal(2, edits.Count);

        editor.AddControl(first);
        Assert.Equal(3, edits.Count);
        Assert.Same(first, editor.Controls[^1]);
        Assert.Equal(editor.Controls.Select(option => option.Kind),
            edits[^1].Controls!.Select(option => option.Kind));
        AssertPreferenceEqual(edits[^1], editor.ToPreference());
    }

    [Fact]
    public void SliderActionsRejectTheThirdAndPublishCompletedRemovalAndAdditionOnce()
    {
        using var editor = CreateEditor();
        var edits = new List<TrayDashboardPreference>();
        editor.EditAccepted += preference =>
        {
            Assert.Equal(editor.Sliders.Select(option => option.Kind),
                preference.Sliders!.Where(option => option.IsVisible).Select(option => option.Kind));
            edits.Add(preference);
        };
        TrayDashboardSliderOption removed = editor.Sliders[0];
        TrayDashboardSliderOption added = editor.AvailableSliders.First();

        editor.AddSlider(added);
        Assert.Empty(edits);
        Assert.False(added.IsVisible);
        Assert.False(editor.CanAddSlider);
        editor.RemoveSlider(removed);
        Assert.Single(edits);
        Assert.False(removed.IsVisible);
        Assert.True(editor.CanAddSlider);
        editor.RemoveSlider(removed);
        Assert.Single(edits);
        editor.AddSlider(added);
        Assert.Equal(2, edits.Count);
        Assert.True(added.IsVisible);
        Assert.Same(added, editor.Sliders[^1]);
        Assert.False(editor.CanAddSlider);
        editor.AddSlider(added);
        Assert.Equal(2, edits.Count);
        Assert.Equal(new TrayDashboardSliderPreference(removed.Kind, false),
            edits[^1].Sliders![2]);
    }

    [Fact]
    public void StatusEditsPublishBeforeAvailabilityAndIgnoreEnabledAndSameValueChanges()
    {
        using var editor = CreateEditor();
        TrayDashboardStatusItemOption visible = editor.StatusItems.First(option => option.IsVisible);
        TrayDashboardStatusItemOption hidden = editor.StatusItems.Single(option => !option.IsVisible);
        var events = new List<string>();
        editor.EditAccepted += preference =>
        {
            events.Add("edit");
            Assert.False(preference.StatusItems!.Single(option => option.Kind == visible.Kind).IsVisible);
            Assert.False(hidden.IsEnabled);
        };
        hidden.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TrayDashboardStatusItemOption.IsEnabled))
            {
                events.Add("enabled");
            }
        };
        editor.AvailabilityChanged += () => events.Add("availability");

        visible.IsVisible = false;

        Assert.Equal(["edit", "enabled", "availability"], events);
        Assert.Equal(2, editor.VisibleStatusItemCount);
        Assert.All(editor.StatusItems, option => Assert.True(option.IsEnabled));
        events.Clear();
        visible.IsVisible = false;
        visible.IsEnabled = false;
        Assert.Empty(events);
    }

    [Fact]
    public void FourthStatusIsRevertedWithoutAnAcceptedEdit()
    {
        using var editor = CreateEditor();
        TrayDashboardStatusItemOption hidden = editor.StatusItems.Single(option => !option.IsVisible);
        int edits = 0;
        int visibilityNotifications = 0;
        int availability = 0;
        editor.EditAccepted += _ => edits++;
        editor.AvailabilityChanged += () => availability++;
        hidden.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TrayDashboardStatusItemOption.IsVisible))
            {
                visibilityNotifications++;
            }
        };

        hidden.IsVisible = true;

        Assert.False(hidden.IsVisible);
        Assert.False(hidden.IsEnabled);
        Assert.Equal(3, editor.VisibleStatusItemCount);
        Assert.Equal(0, edits);
        Assert.Equal(2, visibilityNotifications);
        Assert.Equal(1, availability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetAlwaysPublishesOneCompletedDefaultPreference(bool changeFirst)
    {
        using var editor = CreateEditor();
        if (changeFirst)
        {
            editor.RemoveControl(editor.Controls[0]);
            editor.RemoveSlider(editor.Sliders[0]);
            editor.StatusItems[0].IsVisible = false;
        }
        var events = new List<string>();
        editor.AvailabilityChanged += () => events.Add("availability");
        editor.EditAccepted += preference =>
        {
            events.Add("edit");
            AssertPreferenceEqual(TrayDashboardPreferenceDefaults.Preference, preference);
            AssertPreferenceEqual(preference, editor.ToPreference());
        };

        editor.Reset();

        Assert.Equal(["availability", "edit", "availability"], events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeControlAndSliderCollectionEventsEachPublishOnce(bool sliders)
    {
        using var editor = CreateEditor();
        var changes = new List<NotifyCollectionChangedAction>();
        var edits = new List<TrayDashboardPreference>();
        editor.EditAccepted += edits.Add;
        if (sliders)
        {
            editor.Sliders.CollectionChanged += (_, args) => changes.Add(args.Action);
            TrayDashboardSliderOption option = editor.Sliders[0];
            editor.Sliders.Move(0, 1);
            editor.Sliders.Remove(option);
            editor.Sliders.Add(option);
            editor.Sliders.Clear();
            Assert.True(option.IsVisible);
        }
        else
        {
            editor.Controls.CollectionChanged += (_, args) => changes.Add(args.Action);
            TrayDashboardControlOption option = editor.Controls[0];
            editor.Controls.Move(0, 1);
            editor.Controls.Remove(option);
            editor.Controls.Add(option);
            editor.Controls.Clear();
            Assert.True(option.IsVisible);
        }

        Assert.Equal(
            [NotifyCollectionChangedAction.Move, NotifyCollectionChangedAction.Remove,
                NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Reset],
            changes);
        Assert.Equal(changes.Count, edits.Count);
        Assert.All(sliders ? edits[^1].Sliders!.Select(option => option.IsVisible)
            : edits[^1].Controls!.Select(option => option.IsVisible), Assert.False);
    }

    [Fact]
    public void StatusStructuralChangesAndControlSliderVisibilityAreNotAcceptedEdits()
    {
        using var editor = CreateEditor();
        int edits = 0;
        editor.EditAccepted += _ => edits++;
        editor.Controls[0].IsVisible = false;
        editor.Sliders[0].IsVisible = false;
        editor.StatusItems.Move(0, 1);
        var external = new TrayDashboardStatusItemOption(
            TrayDashboardStatusItemKind.FanSpeed, "External", false);
        editor.StatusItems.Add(external);
        external.IsVisible = true;
        editor.StatusItems.Remove(external);

        Assert.Equal(0, edits);
        Assert.True(editor.ToPreference().Controls![0].IsVisible);
        Assert.True(editor.ToPreference().Sliders![0].IsVisible);
    }

    [Fact]
    public void ReloadAndDisposeUnsubscribeStaleStatusAndCollectionNotifications()
    {
        var editor = CreateEditor();
        TrayDashboardStatusItemOption stale = editor.StatusItems[0];
        int edits = 0;
        int availability = 0;
        editor.EditAccepted += _ => edits++;
        editor.AvailabilityChanged += () => availability++;
        editor.Load(TrayDashboardPreferenceDefaults.Preference);
        availability = 0;

        stale.IsVisible = false;
        Assert.Equal(0, edits);
        Assert.Equal(0, availability);
        TrayDashboardStatusItemOption current = editor.StatusItems[0];
        current.IsVisible = false;
        Assert.Equal(1, edits);
        Assert.Equal(1, availability);
        editor.Dispose();
        current.IsVisible = true;
        editor.Controls.Move(0, 1);
        editor.Sliders.Move(0, 1);
        editor.Dispose();
        Assert.Equal(1, edits);
        Assert.Equal(1, availability);
    }

    private static TrayDashboardPreferenceEditor CreateEditor()
    {
        var editor = new TrayDashboardPreferenceEditor();
        editor.Load(TrayDashboardPreferenceDefaults.Preference);
        return editor;
    }

    private static void AssertPreferenceEqual(
        TrayDashboardPreference expected,
        TrayDashboardPreference actual)
    {
        Assert.Equal(expected.Controls, actual.Controls);
        Assert.Equal(expected.StatusItems, actual.StatusItems);
        Assert.Equal(expected.Sliders, actual.Sliders);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
    }
}
