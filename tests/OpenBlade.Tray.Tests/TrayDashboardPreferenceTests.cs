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
                TrayDashboardControlKind.BatteryProtection,
                TrayDashboardControlKind.FunctionRow,
                TrayDashboardControlKind.GamingMode,
                TrayDashboardControlKind.GpuTuning,
            ],
            preference.Controls!.Select(control => control.Kind));
        Assert.Equal(
            [true, true, true, true, true, true, false],
            preference.Controls!.Select(control => control.IsVisible));
        Assert.Equal(
            TrayDashboardPreferenceDefaults.CurrentSchemaVersion,
            preference.SchemaVersion);
        Assert.Equal(
            [
                TrayDashboardStatusItemKind.BatteryPower,
                TrayDashboardStatusItemKind.Temperatures,
                TrayDashboardStatusItemKind.PowerSource,
                TrayDashboardStatusItemKind.FanSpeed,
            ],
            preference.StatusItems!.Select(status => status.Kind));
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
        Assert.Equal(
            "Keyboard brightness",
            TrayDashboardPreferenceDefaults.CreateSliderOptions(preference)
                .Single(option =>
                    option.Kind == TrayDashboardSliderKind.LightingBrightness)
                .Label);
    }

    [Fact]
    public void NormalizePreservesControlOrderAndMigratesLegacySchema()
    {
        TrayDashboardPreference normalized = TrayDashboardPreferenceDefaults.Normalize(new(
            Controls:
            [
                new(
                    TrayDashboardControlKind.Lighting,
                    true),
                new(
                    TrayDashboardControlKind.Performance,
                    false),
                new(
                    TrayDashboardControlKind.Lighting,
                    false),
            ],
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
                TrayDashboardControlKind.BatteryProtection,
                TrayDashboardControlKind.FunctionRow,
                TrayDashboardControlKind.GamingMode,
                TrayDashboardControlKind.GpuTuning,
            ],
            normalized.Controls!.Select(control => control.Kind));
        Assert.Equal(
            [true, false, false, false, false, false, false],
            normalized.Controls!.Select(control => control.IsVisible));
        Assert.Equal(
            TrayDashboardPreferenceDefaults.CurrentSchemaVersion,
            normalized.SchemaVersion);
        Assert.Equal(
            [
                TrayDashboardStatusItemKind.FanSpeed,
                TrayDashboardStatusItemKind.BatteryPower,
                TrayDashboardStatusItemKind.Temperatures,
                TrayDashboardStatusItemKind.PowerSource,
            ],
            normalized.StatusItems!.Select(status => status.Kind));
        Assert.Equal(
            [true, false, false, false],
            normalized.StatusItems!.Select(status => status.IsVisible));
        Assert.Equal(
            [true, false, false, false],
            normalized.Sliders!.Select(slider => slider.IsVisible));
    }

    [Fact]
    public void NormalizeRejectsFutureDashboardSchemas()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            TrayDashboardPreferenceDefaults.Normalize(new(
                Controls:
                [
                    new(TrayDashboardControlKind.Fans, false),
                ],
                SchemaVersion: TrayDashboardPreferenceDefaults.CurrentSchemaVersion + 1)));

        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeMigratesLegacyBatteryAndLightingTilesToSliders()
    {
        TrayDashboardPreference normalized = TrayDashboardPreferenceDefaults.Normalize(new(
            Controls:
            [
                new(
                    TrayDashboardControlKind.Battery,
                    true),
                new(
                    TrayDashboardControlKind.Lighting,
                    false),
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

    [Fact]
    public void NormalizeAllowsZeroOrOneSliderAndCapsVisibleSlidersAtTwo()
    {
        TrayDashboardPreference none = TrayDashboardPreferenceDefaults.Normalize(new(
            Sliders:
            [
                new(TrayDashboardSliderKind.ChargeLimit, false),
                new(TrayDashboardSliderKind.LightingBrightness, false),
            ]));
        TrayDashboardPreference one = TrayDashboardPreferenceDefaults.Normalize(new(
            Sliders:
            [
                new(TrayDashboardSliderKind.GpuCoreClock, true),
            ]));
        TrayDashboardPreference three = TrayDashboardPreferenceDefaults.Normalize(new(
            Sliders:
            [
                new(TrayDashboardSliderKind.GpuMemoryClock, true),
                new(TrayDashboardSliderKind.ChargeLimit, true),
                new(TrayDashboardSliderKind.LightingBrightness, true),
            ]));

        Assert.DoesNotContain(none.Sliders!, slider => slider.IsVisible);
        Assert.Equal(
            [TrayDashboardSliderKind.GpuCoreClock],
            one.Sliders!.Where(slider => slider.IsVisible).Select(slider => slider.Kind));
        Assert.Equal(
            [
                TrayDashboardSliderKind.GpuMemoryClock,
                TrayDashboardSliderKind.ChargeLimit,
            ],
            three.Sliders!.Where(slider => slider.IsVisible).Select(slider => slider.Kind));
        Assert.Equal(2, TrayDashboardPreferenceDefaults.MaximumVisibleSliders);
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
    public void StatusPowerSourceTextIsCompact()
    {
        Assert.Equal(
            "AC",
            TrayDashboardStatusPresentation.PowerSourceText(
                connected: true,
                PowerSource.PluggedIn));
        Assert.Equal(
            "USB-C",
            TrayDashboardStatusPresentation.PowerSourceText(
                connected: true,
                PowerSource.UsbC));
        Assert.Equal(
            "Battery",
            TrayDashboardStatusPresentation.PowerSourceText(
                connected: true,
                PowerSource.Battery));
        Assert.Equal(
            "—",
            TrayDashboardStatusPresentation.PowerSourceText(
                connected: false,
                PowerSource.PluggedIn));
    }

    [Theory]
    [InlineData(true, 3210, 2875, "3.2K / 2.9K")]
    [InlineData(true, 900, null, "900")]
    [InlineData(true, null, 2875, "2.9K")]
    [InlineData(true, null, null, "—")]
    [InlineData(false, 3210, 2875, "—")]
    public void StatusFanSpeedTextFitsThePopupFooter(
        bool connected,
        int? fan1Rpm,
        int? fan2Rpm,
        string expected)
    {
        Assert.Equal(
            expected,
            TrayDashboardStatusPresentation.FanSpeed(
                connected,
                fan1Rpm,
                fan2Rpm));
    }

    [Fact]
    public void PlacementClampsToWorkAreaAndOpensAboveBottomTaskbar()
    {
        TrayDashboardResolvedPlacement result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(1900, 1075),
            new TrayDashboardRect(0, 0, 1920, 1040),
            new TrayDashboardSize(420, 520),
            dpi: 96);

        Assert.Equal(new TrayDashboardRect(1492, 512, 420, 520), result.Rect);
        Assert.Equal(new TrayDashboardRevealGeometry(1040), result.RevealGeometry);
    }

    [Fact]
    public void PlacementUsesTopEdgeForAnUpperTaskbarAnchor()
    {
        TrayDashboardResolvedPlacement result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(20, 0),
            new TrayDashboardRect(0, 40, 1280, 680),
            new TrayDashboardSize(420, 520),
            dpi: 96);

        Assert.Equal(new TrayDashboardRect(8, 48, 420, 520), result.Rect);
        Assert.Equal(new TrayDashboardRevealGeometry(-480), result.RevealGeometry);
    }

    [Fact]
    public void PlacementScalesDipDimensionsAndMarginForMonitorDpi()
    {
        TrayDashboardResolvedPlacement result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(1900, 1075),
            new TrayDashboardRect(0, 0, 1920, 1040),
            new TrayDashboardSize(420, 480),
            dpi: 144);

        Assert.Equal(new TrayDashboardRect(1278, 308, 630, 720), result.Rect);
    }

    [Theory]
    [InlineData(96, 360, 388, 8)]
    [InlineData(144, 540, 582, 12)]
    [InlineData(192, 720, 776, 16)]
    public void PlacementMatchesWindowsQuickSettingsFootprintAtCommonMonitorDpis(
        int dpi,
        int expectedWidth,
        int expectedHeight,
        int expectedMargin)
    {
        var workArea = new TrayDashboardRect(0, 0, 3840, 2160);
        TrayDashboardResolvedPlacement result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(3839, 2159),
            workArea,
            new TrayDashboardSize(360, 388),
            dpi);

        Assert.Equal(expectedWidth, result.Rect.Width);
        Assert.Equal(expectedHeight, result.Rect.Height);
        Assert.Equal(workArea.Right - expectedMargin, result.Rect.Right);
        Assert.Equal(workArea.Bottom - expectedMargin, result.Rect.Bottom);
    }

    [Fact]
    public void PlacementHandlesWorkAreasNarrowerThanConfiguredMargins()
    {
        TrayDashboardResolvedPlacement result = TrayDashboardPlacement.Resolve(
            new TrayDashboardPoint(3, 8),
            new TrayDashboardRect(0, 0, 6, 10),
            new TrayDashboardSize(420, 480),
            dpi: 192);

        Assert.Equal(new TrayDashboardRect(2, 4, 2, 2), result.Rect);
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
                true),
            new(
                TrayDashboardControlKind.Fans,
                true),
            new(
                TrayDashboardControlKind.Battery,
                true),
            new(
                TrayDashboardControlKind.Lighting,
                true),
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
            TrayDashboardLayoutPolicy.CalculatePreferredHeight());
    }

    [Fact]
    public void LayoutCapsTheViewportAtSixControls()
    {
        TrayDashboardLayout layout = TrayDashboardLayoutPolicy.Resolve(
        [
            new(TrayDashboardControlKind.Performance, true),
            new(TrayDashboardControlKind.Fans, true),
            new(TrayDashboardControlKind.Battery, true),
            new(TrayDashboardControlKind.Lighting, true),
            new(TrayDashboardControlKind.Performance, true),
            new(TrayDashboardControlKind.Fans, true),
            new(TrayDashboardControlKind.Battery, true),
        ]);

        Assert.Equal(3, layout.RowCount);
        Assert.Equal(2, layout.VisibleRowCount);
        Assert.True(layout.HasOverflow);
        Assert.Equal(2, layout.Controls[6].Row);
        Assert.Equal(0, layout.Controls[6].Column);
        Assert.Equal(388, TrayDashboardLayoutPolicy.CalculatePreferredHeight());
    }

    [Fact]
    public void LayoutDoesNotReserveRowsForHiddenControls()
    {
        TrayDashboardLayout layout = TrayDashboardLayoutPolicy.Resolve(
        [
            new(
                TrayDashboardControlKind.Performance,
                false),
            new(
                TrayDashboardControlKind.Fans,
                true),
        ]);

        Assert.Equal(1, layout.RowCount);
        Assert.Equal(1, layout.VisibleRowCount);
        Assert.False(layout.HasOverflow);
        Assert.Equal(
            388,
            TrayDashboardLayoutPolicy.CalculatePreferredHeight());
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

    [Theory]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("Shell_SecondaryTrayWnd", true)]
    [InlineData("ApplicationFrameWindow", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ReopenSuppressionOnlyRecognizesShellTrayWindows(
        string? className,
        bool expected)
    {
        Assert.Equal(
            expected,
            TrayDashboardInteractionPolicy.IsShellTrayWindowClass(className));
    }
}
