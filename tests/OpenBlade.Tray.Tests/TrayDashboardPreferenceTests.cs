using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class TrayDashboardPreferenceTests
{
    [Fact]
    public void DefaultsExposeCoreControlsInTheUniformGrid()
    {
        TrayDashboardPreference preference = TrayDashboardPreferenceDefaults.Preference;

        Assert.Equal(
            [
                TrayDashboardControlKind.Performance,
                TrayDashboardControlKind.Fans,
                TrayDashboardControlKind.Lighting,
            ],
            preference.Controls!.Select(control => control.Kind));
        Assert.All(preference.Controls!, control => Assert.True(control.IsVisible));
        Assert.All(
            preference.Controls!,
            control => Assert.Equal(TrayDashboardControlSize.Compact, control.Size));
        Assert.Null(preference.ExpandedControl);
        Assert.All(preference.StatusItems!, status => Assert.True(status.IsVisible));
        Assert.Equal(
            [
                TrayDashboardSliderKind.LightingBrightness,
                TrayDashboardSliderKind.ChargeLimit,
                TrayDashboardSliderKind.GpuCoreClock,
                TrayDashboardSliderKind.GpuMemoryClock,
            ],
            preference.Sliders!.Select(slider => slider.Kind));
        Assert.Equal(
            [true, true, false, false],
            preference.Sliders!.Select(slider => slider.IsVisible));
    }

    [Fact]
    public void NormalizePreservesOrderAndMigratesLegacyLayoutOptions()
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
            ],
            normalized.Controls!.Select(control => control.Kind));
        Assert.Equal(
            [true, false, false],
            normalized.Controls!.Select(control => control.IsVisible));
        Assert.All(
            normalized.Controls!,
            control => Assert.Equal(TrayDashboardControlSize.Compact, control.Size));
        Assert.Null(normalized.ExpandedControl);
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
        Assert.Equal(
            [true, false, false, false],
            normalized.Sliders!.Select(slider => slider.IsVisible));
    }

    [Fact]
    public void NormalizeDropsLegacyExpandedControlAndInvalidSizes()
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

    [Fact]
    public void NormalizeMigratesLegacyBatteryAndLightingTilesToSliders()
    {
        TrayDashboardPreference normalized = TrayDashboardPreferenceDefaults.Normalize(new(
            Controls:
            [
                new(
                    TrayDashboardControlKind.Battery,
                    true,
                    TrayDashboardControlSize.Wide),
                new(
                    TrayDashboardControlKind.Lighting,
                    false,
                    TrayDashboardControlSize.Compact),
            ]));

        Assert.DoesNotContain(
            normalized.Controls!,
            control => control.Kind == TrayDashboardControlKind.Battery);
        Assert.Equal(
            [
                (TrayDashboardSliderKind.LightingBrightness, false),
                (TrayDashboardSliderKind.ChargeLimit, true),
                (TrayDashboardSliderKind.GpuCoreClock, false),
                (TrayDashboardSliderKind.GpuMemoryClock, false),
            ],
            normalized.Sliders!.Select(slider => (slider.Kind, slider.IsVisible)));
    }

    [Fact]
    public void NormalizePreservesSliderOrderAndDropsDuplicates()
    {
        TrayDashboardPreference normalized = TrayDashboardPreferenceDefaults.Normalize(new(
            Sliders:
            [
                new(TrayDashboardSliderKind.GpuMemoryClock, true),
                new(TrayDashboardSliderKind.ChargeLimit, false),
                new(TrayDashboardSliderKind.GpuMemoryClock, false),
            ]));

        Assert.Equal(
            [
                (TrayDashboardSliderKind.GpuMemoryClock, true),
                (TrayDashboardSliderKind.ChargeLimit, false),
                (TrayDashboardSliderKind.LightingBrightness, false),
                (TrayDashboardSliderKind.GpuCoreClock, false),
            ],
            normalized.Sliders!.Select(slider => (slider.Kind, slider.IsVisible)));
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

    [Theory]
    [InlineData(TrayIconPointerButton.Left, TrayIconActivation.OpenMainWindow)]
    [InlineData(TrayIconPointerButton.Right, TrayIconActivation.None)]
    [InlineData(TrayIconPointerButton.Middle, TrayIconActivation.None)]
    [InlineData(TrayIconPointerButton.Other, TrayIconActivation.None)]
    public void TrayIconDoubleClickRoutesOnlyLeftButtonToMainWindow(
        TrayIconPointerButton button,
        TrayIconActivation expected)
    {
        Assert.Equal(
            expected,
            TrayIconActivationPolicy.ResolveDoubleClick(button));
    }

    [Theory]
    [InlineData(-1, 500, false)]
    [InlineData(0, 500, true)]
    [InlineData(500, 500, true)]
    [InlineData(501, 500, false)]
    [InlineData(1, 0, false)]
    public void TrayIconTrailingClickSuppressionUsesSystemDoubleClickTime(
        int elapsedMilliseconds,
        int systemDoubleClickMilliseconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            TrayIconActivationPolicy.ShouldSuppressTrailingClick(
                TimeSpan.FromMilliseconds(elapsedMilliseconds),
                TimeSpan.FromMilliseconds(systemDoubleClickMilliseconds)));
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 100)]
    [InlineData(50, 18)]
    [InlineData(100, 3)]
    [InlineData(200, 0)]
    [InlineData(500, 0)]
    public void PopupRevealUsesBrightnessOsdEaseOut(
        int elapsedMilliseconds,
        int expectedPosition)
    {
        Assert.Equal(
            expectedPosition,
            WindowsPopupSlidePolicy.ResolvePosition(
                start: 100,
                end: 0,
                TimeSpan.FromMilliseconds(elapsedMilliseconds),
                hiding: false));
    }

    [Fact]
    public void PopupDismissUsesBrightnessOsdEaseInOverTwoHundredMilliseconds()
    {
        Assert.Equal(
            3,
            WindowsPopupSlidePolicy.ResolvePosition(
                start: 0,
                end: 100,
                TimeSpan.FromMilliseconds(100),
                hiding: true));
        Assert.True(WindowsPopupSlidePolicy.IsComplete(TimeSpan.FromMilliseconds(200)));
        Assert.False(WindowsPopupSlidePolicy.IsComplete(TimeSpan.FromMilliseconds(199)));
    }

    [Theory]
    [InlineData(true, -24300, PowerSource.Battery, "24.3 W")]
    [InlineData(true, 31800, PowerSource.PluggedIn, "+31.8 W")]
    [InlineData(true, 0, PowerSource.PluggedIn, "Idle")]
    [InlineData(true, 0, PowerSource.Battery, "—")]
    [InlineData(false, -24300, PowerSource.Battery, "—")]
    public void StatusPowerTextFitsThePopupFooter(
        bool connected,
        int milliwatts,
        PowerSource source,
        string expected)
    {
        Assert.Equal(
            expected,
            TrayDashboardStatusPresentation.BatteryPower(
                connected,
                milliwatts,
                source));
    }

    [Theory]
    [InlineData(true, 47, 52, "47° / 52°")]
    [InlineData(true, 47, null, "47°")]
    [InlineData(true, null, 52, "52°")]
    [InlineData(true, null, null, "—")]
    [InlineData(false, 47, 52, "—")]
    public void StatusTemperatureTextFitsThePopupFooter(
        bool connected,
        int? cpu,
        int? gpu,
        string expected)
    {
        Assert.Equal(
            expected,
            TrayDashboardStatusPresentation.Temperatures(
                connected,
                cpu,
                gpu));
    }

    [Fact]
    public void StatusFanTextDropsTheRedundantNoun()
    {
        Assert.Equal(
            "Automatic",
            TrayDashboardStatusPresentation.Fans(
                connected: true,
                FanMode.Automatic));
        Assert.Equal(
            "—",
            TrayDashboardStatusPresentation.Fans(
                connected: false,
                FanMode.Automatic));
    }

    [Fact]
    public void PlacementClampsToWorkAreaAndOpensAboveBottomTaskbar()
    {
        TrayDashboardRect result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(1900, 1075),
            new TrayDashboardRect(0, 0, 1920, 1040),
            new TrayDashboardSize(420, 520),
            dpi: 96);

        Assert.Equal(new TrayDashboardRect(1492, 512, 420, 520), result);
    }

    [Fact]
    public void PlacementUsesTopEdgeForAnUpperTaskbarAnchor()
    {
        TrayDashboardRect result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(20, 0),
            new TrayDashboardRect(0, 40, 1280, 680),
            new TrayDashboardSize(420, 520),
            dpi: 96);

        Assert.Equal(new TrayDashboardRect(8, 48, 420, 520), result);
    }

    [Fact]
    public void BottomRevealStagesTheWholePopupBelowTheWorkArea()
    {
        TrayDashboardRevealGeometry result =
            TrayDashboardPlacement.ResolveRevealGeometry(
                new TrayDashboardPoint(1900, 1075),
                new TrayDashboardRect(0, 0, 1920, 1040),
                new TrayDashboardRect(1492, 512, 420, 520));

        Assert.Equal(new TrayDashboardRevealGeometry(1040), result);
    }

    [Fact]
    public void TopRevealStagesTheWholePopupAboveTheWorkArea()
    {
        TrayDashboardRevealGeometry result =
            TrayDashboardPlacement.ResolveRevealGeometry(
                new TrayDashboardPoint(20, 0),
                new TrayDashboardRect(0, 40, 1280, 680),
                new TrayDashboardRect(8, 48, 420, 520));

        Assert.Equal(new TrayDashboardRevealGeometry(-480), result);
    }

    [Fact]
    public void PlacementScalesDipDimensionsAndMarginForMonitorDpi()
    {
        TrayDashboardRect result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(1900, 1075),
            new TrayDashboardRect(0, 0, 1920, 1040),
            new TrayDashboardSize(420, 480),
            dpi: 144);

        Assert.Equal(new TrayDashboardRect(1278, 308, 630, 720), result);
    }

    [Theory]
    [InlineData(96, 360, 388, 8)]
    [InlineData(144, 540, 582, 12)]
    [InlineData(192, 720, 776, 16)]
    public void PlacementScalesRedesignedPopupAtCommonMonitorDpis(
        int dpi,
        int expectedWidth,
        int expectedHeight,
        int expectedMargin)
    {
        var workArea = new TrayDashboardRect(0, 0, 3840, 2160);
        TrayDashboardRect result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(3839, 2159),
            workArea,
            new TrayDashboardSize(360, 388),
            dpi);

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
        Assert.Equal(workArea.Right - expectedMargin, result.Right);
        Assert.Equal(workArea.Bottom - expectedMargin, result.Bottom);
    }

    [Fact]
    public void PlacementHandlesWorkAreasNarrowerThanConfiguredMargins()
    {
        TrayDashboardRect result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(3, 8),
            new TrayDashboardRect(0, 0, 6, 10),
            new TrayDashboardSize(420, 480),
            dpi: 192);

        Assert.Equal(new TrayDashboardRect(2, 4, 2, 2), result);
    }

    [Fact]
    public void PlacementRejectsAZeroDipMarginWithItsPublicParameterName()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrayDashboardPlacement.Resolve(
                new TrayDashboardPoint(100, 100),
                new TrayDashboardRect(0, 0, 1920, 1080),
                new TrayDashboardSize(420, 480),
                dpi: 96,
                marginDip: 0));

        Assert.Equal("marginDip", exception.ParamName);
    }

    [Fact]
    public void LayoutUsesAUniformThreeColumnFlow()
    {
        TrayDashboardLayout layout = TrayDashboardLayoutPolicy.Resolve(
        [
            new(
                TrayDashboardControlKind.Performance,
                true,
                TrayDashboardControlSize.Compact),
            new(
                TrayDashboardControlKind.Fans,
                true,
                TrayDashboardControlSize.Wide),
            new(
                TrayDashboardControlKind.Battery,
                true,
                TrayDashboardControlSize.Compact),
            new(
                TrayDashboardControlKind.Lighting,
                true,
                TrayDashboardControlSize.Compact),
        ]);

        Assert.Equal(2, layout.RowCount);
        Assert.Equal(2, layout.VisibleRowCount);
        Assert.False(layout.HasOverflow);
        Assert.Equal(
        [
            new(TrayDashboardControlKind.Performance, 0, 0, 1),
            new(TrayDashboardControlKind.Fans, 0, 1, 1),
            new(TrayDashboardControlKind.Battery, 0, 2, 1),
            new(TrayDashboardControlKind.Lighting, 1, 0, 1),
        ],
            layout.Controls);
        Assert.Equal(
            388,
            TrayDashboardLayoutPolicy.CalculatePreferredHeight(layout));
    }

    [Fact]
    public void LayoutCapsTheViewportAtSixControls()
    {
        TrayDashboardLayout layout = TrayDashboardLayoutPolicy.Resolve(
        [
            new(TrayDashboardControlKind.Performance, true, TrayDashboardControlSize.Compact),
            new(TrayDashboardControlKind.Fans, true, TrayDashboardControlSize.Compact),
            new(TrayDashboardControlKind.Battery, true, TrayDashboardControlSize.Compact),
            new(TrayDashboardControlKind.Lighting, true, TrayDashboardControlSize.Compact),
            new(TrayDashboardControlKind.Performance, true, TrayDashboardControlSize.Compact),
            new(TrayDashboardControlKind.Fans, true, TrayDashboardControlSize.Compact),
            new(TrayDashboardControlKind.Battery, true, TrayDashboardControlSize.Compact),
        ]);

        Assert.Equal(3, layout.RowCount);
        Assert.Equal(2, layout.VisibleRowCount);
        Assert.True(layout.HasOverflow);
        Assert.Equal(2, layout.Controls[6].Row);
        Assert.Equal(0, layout.Controls[6].Column);
        Assert.Equal(388, TrayDashboardLayoutPolicy.CalculatePreferredHeight(layout));
    }

    [Fact]
    public void LayoutDoesNotReserveRowsForHiddenControls()
    {
        TrayDashboardLayout layout = TrayDashboardLayoutPolicy.Resolve(
        [
            new(
                TrayDashboardControlKind.Performance,
                false,
                TrayDashboardControlSize.Compact),
            new(
                TrayDashboardControlKind.Fans,
                true,
                TrayDashboardControlSize.Compact),
        ]);

        Assert.Equal(1, layout.RowCount);
        Assert.Equal(1, layout.VisibleRowCount);
        Assert.False(layout.HasOverflow);
        Assert.Equal(
            388,
            TrayDashboardLayoutPolicy.CalculatePreferredHeight(layout));
    }

    [Theory]
    [InlineData(0, -1, -1)]
    [InlineData(1, -1, 0)]
    [InlineData(3, -1, 0)]
    [InlineData(3, 0, 1)]
    [InlineData(3, 1, 2)]
    [InlineData(3, 2, 0)]
    [InlineData(3, 99, 0)]
    public void QuickActionCyclesToTheNextAvailableOption(
        int optionCount,
        int currentIndex,
        int expected)
    {
        Assert.Equal(
            expected,
            TrayDashboardInteractionPolicy.ResolveNextIndex(
                optionCount,
                currentIndex));
    }

    [Fact]
    public void QuickActionRejectsANegativeOptionCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrayDashboardInteractionPolicy.ResolveNextIndex(-1, 0));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(200, true)]
    [InlineData(251, false)]
    public void DeactivationReopenSuppressionIsBounded(
        int elapsedMilliseconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            TrayDashboardInteractionPolicy.ShouldSuppressReopen(
                TimeSpan.FromMilliseconds(elapsedMilliseconds)));
    }
}
