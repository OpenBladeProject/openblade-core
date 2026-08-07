namespace OpenBlade.Tray.Tests;

public sealed class TrayDashboardPreferenceTests
{
    [Fact]
    public void DefaultsExposeCoreControlsAndOneExpandedControl()
    {
        TrayDashboardPreference preference = TrayDashboardPreferenceDefaults.Preference;

        Assert.Equal(
            [
                TrayDashboardControlKind.Performance,
                TrayDashboardControlKind.Fans,
                TrayDashboardControlKind.Battery,
                TrayDashboardControlKind.Lighting,
            ],
            preference.Controls!.Select(control => control.Kind));
        Assert.All(preference.Controls!, control => Assert.True(control.IsVisible));
        Assert.All(
            preference.Controls!,
            control => Assert.Equal(TrayDashboardControlSize.Compact, control.Size));
        Assert.Equal(
            TrayDashboardControlKind.Performance,
            preference.ExpandedControl);
        Assert.All(preference.StatusItems!, status => Assert.True(status.IsVisible));
    }

    [Fact]
    public void NormalizePreservesOrderAndRestoresMissingOptionsAsHidden()
    {
        TrayDashboardPreference normalized = TrayDashboardPreferenceDefaults.Normalize(new(
            Controls:
            [
                new(
                    TrayDashboardControlKind.Lighting,
                    true,
                    TrayDashboardControlSize.Wide),
                new(
                    TrayDashboardControlKind.Performance,
                    false,
                    TrayDashboardControlSize.Compact),
                new(
                    TrayDashboardControlKind.Lighting,
                    false,
                    TrayDashboardControlSize.Compact),
            ],
            ExpandedControl: TrayDashboardControlKind.Lighting,
            StatusItems:
            [
                new(TrayDashboardStatusItemKind.Fans, true),
                new(TrayDashboardStatusItemKind.Fans, false),
            ]));

        Assert.Equal(
            [
                TrayDashboardControlKind.Lighting,
                TrayDashboardControlKind.Performance,
                TrayDashboardControlKind.Fans,
                TrayDashboardControlKind.Battery,
            ],
            normalized.Controls!.Select(control => control.Kind));
        Assert.Equal(
            [true, false, false, false],
            normalized.Controls!.Select(control => control.IsVisible));
        Assert.Equal(
            TrayDashboardControlSize.Wide,
            normalized.Controls![0].Size);
        Assert.Equal(TrayDashboardControlKind.Lighting, normalized.ExpandedControl);
        Assert.Equal(
            [
                TrayDashboardStatusItemKind.Fans,
                TrayDashboardStatusItemKind.BatteryPower,
                TrayDashboardStatusItemKind.Temperatures,
            ],
            normalized.StatusItems!.Select(status => status.Kind));
        Assert.Equal(
            [true, false, false],
            normalized.StatusItems!.Select(status => status.IsVisible));
    }

    [Fact]
    public void NormalizeDropsHiddenExpandedControlAndInvalidSizes()
    {
        TrayDashboardPreference normalized = TrayDashboardPreferenceDefaults.Normalize(new(
            Controls:
            [
                new(
                    TrayDashboardControlKind.Fans,
                    false,
                    (TrayDashboardControlSize)999),
            ],
            ExpandedControl: TrayDashboardControlKind.Fans));

        Assert.Null(normalized.ExpandedControl);
        Assert.Equal(
            TrayDashboardControlSize.Compact,
            normalized.Controls!.Single(control =>
                control.Kind == TrayDashboardControlKind.Fans).Size);
    }

    [Theory]
    [InlineData(TrayIconPointerButton.Left, TrayIconActivation.ShowDashboard)]
    [InlineData(TrayIconPointerButton.Right, TrayIconActivation.None)]
    [InlineData(TrayIconPointerButton.Middle, TrayIconActivation.None)]
    [InlineData(TrayIconPointerButton.Other, TrayIconActivation.None)]
    public void TrayIconActivationRoutesOnlyLeftButtonMouseClicks(
        TrayIconPointerButton button,
        TrayIconActivation expected)
    {
        Assert.Equal(expected, TrayIconActivationPolicy.Resolve(button));
    }

    [Fact]
    public void PlacementClampsToWorkAreaAndOpensAboveBottomTaskbar()
    {
        TrayDashboardRect result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(1900, 1075),
            new TrayDashboardRect(0, 0, 1920, 1040),
            new TrayDashboardSize(420, 520));

        Assert.Equal(new TrayDashboardRect(1492, 512, 420, 520), result);
    }

    [Fact]
    public void PlacementUsesTopEdgeForAnUpperTaskbarAnchor()
    {
        TrayDashboardRect result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(20, 0),
            new TrayDashboardRect(0, 40, 1280, 680),
            new TrayDashboardSize(420, 520));

        Assert.Equal(new TrayDashboardRect(8, 48, 420, 520), result);
    }
}
