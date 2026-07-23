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
        var executor = CreateExecutor(display: display);

        await executor.ExecuteAsync(action, CancellationToken.None);

        Assert.Equal([expectedDirection], display.Directions);
    }

    [Fact]
    public async Task KeyboardBrightnessDown_PreservesEffectParametersAndLogo()
    {
        LightingSettings current = LightingSettingsFactory.Reactive(
            new RgbColor(12, 34, 56),
            brightness: 55,
            ReactiveLightingDuration.Long) with
        {
            LogoMode = LogoLightingMode.Breathing,
        };
        var client = new RecordingServiceClient(Status(current));
        var executor = CreateExecutor(client: client);

        await executor.ExecuteAsync(
            FunctionRowAction.KeyboardBrightnessDown,
            CancellationToken.None);

        ManualOverride<LightingSettings> applied =
            Assert.Single(client.LightingOverrides)!;
        Assert.Equal(45, applied.Value.Brightness);
        Assert.Equal(current.Effect, applied.Value.Effect);
        Assert.Equal(current.Color, applied.Value.Color);
        Assert.Equal(current.Parameters, applied.Value.Parameters);
        Assert.Equal(current.LogoMode, applied.Value.LogoMode);
        Assert.Equal(OverrideLifetime.UntilPowerSourceChange, applied.Lifetime);
    }

    [Fact]
    public async Task KeyboardBrightnessDown_AtFirstStepTurnsKeyboardOff()
    {
        LightingSettings current = LightingSettingsFactory.Static(
            new RgbColor(12, 34, 56),
            brightness: 10) with
        {
            LogoMode = LogoLightingMode.Static,
        };
        var client = new RecordingServiceClient(Status(current));
        var executor = CreateExecutor(client: client);

        await executor.ExecuteAsync(
            FunctionRowAction.KeyboardBrightnessDown,
            CancellationToken.None);

        LightingSettings applied = Assert.Single(client.LightingOverrides)!.Value;
        Assert.Equal(LightingEffect.Off, applied.Effect);
        Assert.Equal(0, applied.Brightness);
        Assert.Equal(current.LogoMode, applied.LogoMode);
    }

    [Fact]
    public async Task KeyboardBrightnessUp_FromOffUsesActiveProfileEffectAtFirstStep()
    {
        LightingSettings profileLighting = LightingSettingsFactory.BreathingSingle(
            new RgbColor(1, 2, 3),
            brightness: 70) with
        {
            LogoMode = LogoLightingMode.Breathing,
        };
        var client = new RecordingServiceClient(
            Status(LightingSettingsFactory.Off(), profileLighting));
        var executor = CreateExecutor(client: client);

        await executor.ExecuteAsync(
            FunctionRowAction.KeyboardBrightnessUp,
            CancellationToken.None);

        LightingSettings applied = Assert.Single(client.LightingOverrides)!.Value;
        Assert.Equal(LightingEffect.Breathing, applied.Effect);
        Assert.Equal(10, applied.Brightness);
        Assert.Equal(profileLighting.Color, applied.Color);
        Assert.Equal(profileLighting.Parameters, applied.Parameters);
        Assert.Equal(profileLighting.LogoMode, applied.LogoMode);
    }

    [Fact]
    public async Task KeyboardBrightnessDownThenUp_RestoresRememberedEffect()
    {
        LightingSettings visible = LightingSettingsFactory.Ripple(
            new RgbColor(9, 8, 7),
            brightness: 10);
        var client = new RecordingServiceClient(Status(visible));
        var executor = CreateExecutor(client: client);

        await executor.ExecuteAsync(
            FunctionRowAction.KeyboardBrightnessDown,
            CancellationToken.None);
        client.Status = Status(Assert.Single(client.LightingOverrides)!.Value);
        client.LightingOverrides.Clear();

        await executor.ExecuteAsync(
            FunctionRowAction.KeyboardBrightnessUp,
            CancellationToken.None);

        LightingSettings applied = Assert.Single(client.LightingOverrides)!.Value;
        Assert.Equal(LightingEffect.Ripple, applied.Effect);
        Assert.Equal(10, applied.Brightness);
        Assert.Equal(visible.Parameters, applied.Parameters);
    }

    [Theory]
    [InlineData(FunctionRowAction.KeyboardBrightnessDown)]
    [InlineData(FunctionRowAction.KeyboardBrightnessUp)]
    public async Task KeyboardBrightness_AtBoundaryDoesNotSendRedundantOverride(
        FunctionRowAction action)
    {
        LightingSettings current = action == FunctionRowAction.KeyboardBrightnessDown
            ? LightingSettingsFactory.Off()
            : LightingSettingsFactory.Static(
                LightingSettingsFactory.DefaultVisibleColor,
                brightness: 100);
        var client = new RecordingServiceClient(Status(current));
        var executor = CreateExecutor(client: client);

        await executor.ExecuteAsync(action, CancellationToken.None);

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

        Assert.Equal("Another controller owns lighting.", error.Message);
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

    private static FunctionRowActionExecutor CreateExecutor(
        RecordingServiceClient? client = null,
        RecordingInputSender? input = null,
        RecordingDisplayBrightnessController? display = null) => new(
            client ?? new RecordingServiceClient(Status(LightingSettingsFactory.Off())),
            input ?? new RecordingInputSender(),
            display ?? new RecordingDisplayBrightnessController());

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

        public Task<bool> AdjustAsync(
            int direction,
            CancellationToken cancellationToken)
        {
            Directions.Add(direction);
            return Task.FromResult(true);
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

    private sealed class RecordingServiceClient : IServiceClient
    {
        public RecordingServiceClient(ServiceStatusSnapshot status)
        {
            Status = status;
        }

        public ServiceStatusSnapshot Status { get; set; }
        public List<ManualOverride<LightingSettings>?> LightingOverrides { get; } = [];
        public ControlOperationResult LightingResult { get; set; } =
            new(ControlOperationStatus.Applied, "Applied.");

        public Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token) =>
            Task.FromResult(Status);

        public Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken token)
        {
            LightingOverrides.Add(value);
            return Task.FromResult(LightingResult);
        }

        public Task<ServiceStatusSnapshot> ObserveStateAsync(
            bool includeLiveReadings,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<DiagnosticArchive> ExportDiagnosticsAsync(
            bool liveReadingsEnabled,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> UpdateProfilesAsync(
            ProfileSet profiles,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(
            ManualOverride<PerformancePreset>? value,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFanOverrideAsync(
            ManualOverride<FanMode>? value,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetBatteryProtectionAsync(
            bool enabled,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(
            bool enabled,
            CancellationToken token) => throw new NotSupportedException();
    }
}
