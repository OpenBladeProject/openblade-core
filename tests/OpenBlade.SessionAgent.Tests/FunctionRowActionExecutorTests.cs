using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class FunctionRowActionExecutorTests
{
    [Theory]
    [InlineData(FunctionRowAction.VolumeMute, SpecialKeyActionKind.VolumeMute)]
    [InlineData(FunctionRowAction.VolumeDown, SpecialKeyActionKind.VolumeDown)]
    [InlineData(FunctionRowAction.VolumeUp, SpecialKeyActionKind.VolumeUp)]
    [InlineData(FunctionRowAction.PreviousTrack, SpecialKeyActionKind.MediaPreviousTrack)]
    [InlineData(FunctionRowAction.PlayPause, SpecialKeyActionKind.MediaPlayPause)]
    [InlineData(FunctionRowAction.NextTrack, SpecialKeyActionKind.MediaNextTrack)]
    public async Task ExecuteAsync_MediaActionsUseInjectedMediaKeys(
        FunctionRowAction action,
        SpecialKeyActionKind expected)
    {
        var input = new RecordingInputSender();
        var executor = CreateExecutor(input: input);

        await executor.ExecuteAsync(action, CancellationToken.None);

        Assert.Equal([expected], input.MediaActions);
    }

    [Theory]
    [InlineData(FunctionRowAction.Project, "Win+P")]
    [InlineData(FunctionRowAction.PrintScreen, "PrintScreen")]
    public async Task ExecuteAsync_KeyboardActionsUseInjectedShortcuts(
        FunctionRowAction action,
        string expected)
    {
        var input = new RecordingInputSender();
        var executor = CreateExecutor(input: input);

        await executor.ExecuteAsync(action, CancellationToken.None);

        Assert.Equal([expected], input.Shortcuts);
    }

    [Theory]
    [InlineData(FunctionRowAction.DisplayBrightnessDown, -1)]
    [InlineData(FunctionRowAction.DisplayBrightnessUp, 1)]
    public async Task ExecuteAsync_DisplayBrightnessUsesWindowsController(
        FunctionRowAction action,
        int expectedDirection)
    {
        var display = new RecordingDisplayBrightnessController();
        var osd = new RecordingDisplayBrightnessOsd();
        var executor = CreateExecutor(display: display, osd: osd);

        await executor.ExecuteAsync(action, CancellationToken.None);

        Assert.Equal([expectedDirection], display.Directions);
        Assert.Equal([display.NextBrightness!.Value], osd.Values);
    }

    [Fact]
    public async Task DisplayBrightnessUnavailable_DoesNotShowOsd()
    {
        var display = new RecordingDisplayBrightnessController
        {
            NextBrightness = null,
        };
        var osd = new RecordingDisplayBrightnessOsd();
        var executor = CreateExecutor(display: display, osd: osd);

        await executor.ExecuteAsync(
            FunctionRowAction.DisplayBrightnessUp,
            CancellationToken.None);

        Assert.Empty(osd.Values);
    }

    [Theory]
    [InlineData(
        FunctionRowAction.KeyboardBrightnessDown,
        KeyboardBrightnessDirection.Decrease)]
    [InlineData(
        FunctionRowAction.KeyboardBrightnessUp,
        KeyboardBrightnessDirection.Increase)]
    public async Task KeyboardBrightness_UsesDedicatedServiceStepWithoutStatusRead(
        FunctionRowAction action,
        KeyboardBrightnessDirection expectedDirection)
    {
        var client = new RecordingServiceClient(
            Status(LightingSettingsFactory.SpectrumCycling(50)));
        var executor = CreateExecutor(client: client);

        await executor.ExecuteAsync(action, CancellationToken.None);

        Assert.Equal(
            [(expectedDirection, OverrideLifetime.UntilPowerSourceChange)],
            client.KeyboardBrightnessSteps);
        Assert.Equal(0, client.GetStatusCalls);
        Assert.Empty(client.LightingOverrides);
    }

    [Fact]
    public async Task KeyboardBrightness_BlockedWriteIsReported()
    {
        var client = new RecordingServiceClient(
            Status(LightingSettingsFactory.Static(
                LightingSettingsFactory.DefaultVisibleColor,
                brightness: 50)))
        {
            LightingResult = new(
                ControlOperationStatus.Blocked,
                "Another controller owns lighting."),
        };
        var executor = CreateExecutor(client: client);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(
                FunctionRowAction.KeyboardBrightnessUp,
                CancellationToken.None));

        Assert.Equal(
            "Function-row action KeyboardBrightnessUp failed: Another controller owns lighting.",
            error.Message);
        Assert.Equal("Another controller owns lighting.", error.InnerException?.Message);
    }

    [Theory]
    [InlineData(50, new byte[] { 0, 25, 50, 75, 100 }, -1, 25)]
    [InlineData(50, new byte[] { 0, 25, 50, 75, 100 }, 1, 75)]
    [InlineData(60, new byte[] { 0, 25, 50, 75, 100 }, -1, 50)]
    [InlineData(60, new byte[] { 0, 25, 50, 75, 100 }, 1, 75)]
    public void SelectAdjacentLevel_UsesPanelReportedLevels(
        byte current,
        byte[] levels,
        int direction,
        byte expected)
    {
        Assert.Equal(
            expected,
            WmiDisplayBrightnessController.SelectAdjacentLevel(
                current,
                levels,
                direction));
    }

    [Fact]
    public void SelectAdjacentLevel_DensePanelUsesVisibleTenPointStep()
    {
        byte[] levels = Enumerable.Range(0, 101).Select(value => (byte)value).ToArray();

        Assert.Equal(
            (byte)82,
            WmiDisplayBrightnessController.SelectAdjacentLevel(92, levels, -1));
        Assert.Equal(
            (byte)100,
            WmiDisplayBrightnessController.SelectAdjacentLevel(92, levels, 1));
    }

    [Theory]
    [InlineData(5, new byte[] { 0, 1, 2, 3, 4, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 }, -1, 0)]
    [InlineData(96, new byte[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 96, 97, 98, 99, 100 }, 1, 100)]
    public void SelectAdjacentLevel_NearBoundaryUsesLastSupportedLevel(
        byte current,
        byte[] levels,
        int direction,
        byte expected)
    {
        Assert.Equal(
            expected,
            WmiDisplayBrightnessController.SelectAdjacentLevel(
                current,
                levels,
                direction));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(100, 1)]
    public void SelectAdjacentLevel_AtBoundaryReturnsNull(byte current, int direction)
    {
        Assert.Null(WmiDisplayBrightnessController.SelectAdjacentLevel(
            current,
            [0, 25, 50, 75, 100],
            direction));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040, 96, 863, 918, 194, 50)]
    [InlineData(-1920, 0, 0, 1400, 144, -1106, 1218, 291, 74)]
    public void BrightnessOsdBounds_CenterAboveActiveMonitorTaskbar(
        int left,
        int top,
        int right,
        int bottom,
        uint dpi,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        DisplayBrightnessOsdBounds actual =
            WindowsDisplayBrightnessOsd.CalculateBounds(
                left,
                top,
                right,
                bottom,
                dpi);

        Assert.Equal(
            new DisplayBrightnessOsdBounds(
                expectedX,
                expectedY,
                expectedWidth,
                expectedHeight),
            actual);
    }

    [Fact]
    public void BrightnessOsdAccent_LightensTheWindowsAccentForDarkFlyout()
    {
        uint windowsBlue = 0x00D47800; // COLORREF for RGB(0, 120, 212).

        uint actual = WindowsDisplayBrightnessOsd.BlendWithWhite(
            windowsBlue,
            whitePercent: 70);

        Assert.Equal(0x00F2D6B2u, actual);
    }

    [Fact]
    public void BrightnessOsdAccent_UsesWindowsAccentLight2PaletteEntry()
    {
        byte[] accentPalette =
        [
            0xC9, 0xE6, 0xF1, 0,
            0xA7, 0xC5, 0xDE, 0,
        ];

        uint actual = WindowsDisplayBrightnessOsd.ResolveFlyoutAccentColor(
            accentPalette,
            fallbackHighlight: 0x00D47800);

        Assert.Equal(0x00DEC5A7u, actual);
    }

    [Fact]
    public void BrightnessOsdMotion_UsesDeliberateTransitionAndShortHold()
    {
        Assert.Equal(250, WindowsDisplayBrightnessOsd.SlideDurationMilliseconds);
        Assert.Equal(2000u, WindowsDisplayBrightnessOsd.HideDelayMilliseconds);
    }

    private static FunctionRowActionExecutor CreateExecutor(
        RecordingServiceClient? client = null,
        RecordingInputSender? input = null,
        RecordingDisplayBrightnessController? display = null,
        RecordingDisplayBrightnessOsd? osd = null) => new(
            client ?? new RecordingServiceClient(Status(LightingSettingsFactory.Off())),
            input ?? new RecordingInputSender(),
            display ?? new RecordingDisplayBrightnessController(),
            osd ?? new RecordingDisplayBrightnessOsd());

    private static ServiceStatusSnapshot Status(
        LightingSettings desired,
        LightingSettings? profileLighting = null)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Lighting = profileLighting ?? defaults.PluggedIn.Lighting,
            },
        };
        return new ServiceStatusSnapshot(
            Revision: 1,
            DeviceVerified: true,
            WritesBlocked: false,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: [],
            Message: "Ready",
            Capabilities: DeviceCapabilities.Unavailable(),
            ConfirmedState: null,
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    profiles.PluggedIn.Performance,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    profiles.PluggedIn.Fans,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    desired,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false);
    }

    private sealed class RecordingDisplayBrightnessController
        : IDisplayBrightnessController
    {
        public List<int> Directions { get; } = [];
        public byte? NextBrightness { get; set; } = 60;

        public Task<byte?> AdjustAsync(
            int direction,
            CancellationToken cancellationToken)
        {
            Directions.Add(direction);
            return Task.FromResult(NextBrightness);
        }
    }

    private sealed class RecordingDisplayBrightnessOsd : IDisplayBrightnessOsd
    {
        public List<byte> Values { get; } = [];

        public void Show(byte brightness) => Values.Add(brightness);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingInputSender : ISpecialKeyInputSender
    {
        public List<string> Shortcuts { get; } = [];
        public List<SpecialKeyActionKind> MediaActions { get; } = [];

        public void SendKeyboardShortcut(string shortcut) => Shortcuts.Add(shortcut);
        public void SendMediaAction(SpecialKeyActionKind action) => MediaActions.Add(action);
        public void SendText(string text) => throw new NotSupportedException();
        public void SendMouseAction(SpecialKeyActionKind action) =>
            throw new NotSupportedException();
        public void ToggleMicrophoneMute() => throw new NotSupportedException();
        public void Launch(string target, bool requireWebAddress) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingServiceClient : OpenBlade.Testing.UnavailableServiceClient
    {
        public RecordingServiceClient(ServiceStatusSnapshot status)
        {
            Status = status;
        }

        public ServiceStatusSnapshot Status { get; set; }
        public int GetStatusCalls { get; private set; }
        public List<ManualOverride<LightingSettings>?> LightingOverrides { get; } = [];
        public List<(KeyboardBrightnessDirection Direction, OverrideLifetime Lifetime)>
            KeyboardBrightnessSteps
        { get; } = [];
        public ControlOperationResult LightingResult { get; set; } =
            new(ControlOperationStatus.Applied, "Applied.");

        public override Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token)
        {
            GetStatusCalls++;
            return Task.FromResult(Status);
        }

        public override Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken token)
        {
            LightingOverrides.Add(value);
            return Task.FromResult(LightingResult);
        }

        public override Task<ControlOperationResult> StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection direction,
            OverrideLifetime lifetime,
            CancellationToken token)
        {
            KeyboardBrightnessSteps.Add((direction, lifetime));
            return Task.FromResult(LightingResult);
        }

    }
}
