using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SpecialKeyActionExecutorTests
{
    [Fact]
    public void ParseShortcut_ParsesModifiersFunctionAndNavigationKeys()
    {
        (ushort key, ushort[] modifiers) =
            SpecialKeyShortcutParser.Parse("Ctrl+Shift+M");
        Assert.Equal(0x4D, key);
        Assert.Equal([0x11, 0x10], modifiers);

        (key, modifiers) = SpecialKeyShortcutParser.Parse("Alt+F12");
        Assert.Equal(0x7B, key);
        Assert.Equal([0x12], modifiers);

        (key, modifiers) = SpecialKeyShortcutParser.Parse("Page Up");
        Assert.Equal(0x21, key);
        Assert.Empty(modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Banana+M")]
    [InlineData("Ctrl+DefinitelyNotAKey")]
    public void ParseShortcut_RejectsInvalidValues(string value)
    {
        Assert.ThrowsAny<Exception>(
            () => SpecialKeyShortcutParser.Parse(value));
    }

    [Fact]
    public async Task ExecuteAsync_CustomShortcutUsesConfiguredValue()
    {
        var bindings = new FixedBindings(
            SpecialKeySlot.M1,
            new SpecialKeyAction(
                SpecialKeyActionKind.KeyboardShortcut,
                "Ctrl+Shift+M"));
        var input = new RecordingInputSender();
        var executor = new SpecialKeyActionExecutor(
            bindings,
            new RecordingServiceClient(),
            input);

        await executor.ExecuteAsync(SpecialKeySlot.M1, CancellationToken.None);

        Assert.Equal(["Ctrl+Shift+M"], input.Shortcuts);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultM4PreservesPowerModeCycle()
    {
        var client = new RecordingServiceClient();
        var executor = new SpecialKeyActionExecutor(
            new FixedBindings(
                SpecialKeySlot.M4,
                new SpecialKeyAction(SpecialKeyActionKind.Default)),
            client,
            new RecordingInputSender());

        await executor.ExecuteAsync(SpecialKeySlot.M4, CancellationToken.None);

        Assert.Equal(1, client.PerformanceCycles);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultM3PreservesGamingModeToggle()
    {
        var client = new RecordingServiceClient
        {
            GamingModeEnabled = false,
        };
        var executor = new SpecialKeyActionExecutor(
            new FixedBindings(
                SpecialKeySlot.M3,
                new SpecialKeyAction(SpecialKeyActionKind.Default)),
            client,
            new RecordingInputSender());

        await executor.ExecuteAsync(SpecialKeySlot.M3, CancellationToken.None);

        Assert.True(client.GamingModeEnabled);
        Assert.Equal(1, client.GamingModeWrites);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultM5LeavesIndicatorToObserver()
    {
        var input = new RecordingInputSender();
        var client = new RecordingServiceClient();
        var executor = new SpecialKeyActionExecutor(
            new FixedBindings(
                SpecialKeySlot.M5,
                new SpecialKeyAction(SpecialKeyActionKind.Default)),
            client,
            input);

        await executor.ExecuteAsync(SpecialKeySlot.M5, CancellationToken.None);

        Assert.Equal(1, input.MicrophoneMuteCount);
        Assert.Null(client.MicrophoneMuteIndicator);
    }

    [Fact]
    public void ShouldHandleInDriverMode_HandlesDefaultsWithoutFirmwareFallback()
    {
        var defaultAction = new SpecialKeyAction(SpecialKeyActionKind.Default);

        Assert.True(SpecialKeyBindingSource.ShouldHandleInDriverMode(
            SpecialKeySlot.M3,
            defaultAction));
        Assert.True(SpecialKeyBindingSource.ShouldHandleInDriverMode(
            SpecialKeySlot.M5,
            defaultAction));
        Assert.False(SpecialKeyBindingSource.ShouldHandleInDriverMode(
            SpecialKeySlot.M4,
            defaultAction));
        Assert.True(SpecialKeyBindingSource.ShouldHandleInDriverMode(
            SpecialKeySlot.M1,
            new SpecialKeyAction(SpecialKeyActionKind.Disabled)));
    }

    [Fact]
    public async Task ExecuteAsync_DisabledDoesNothing()
    {
        var client = new RecordingServiceClient();
        var input = new RecordingInputSender();
        var executor = new SpecialKeyActionExecutor(
            new FixedBindings(
                SpecialKeySlot.M5,
                new SpecialKeyAction(SpecialKeyActionKind.Disabled)),
            client,
            input);

        await executor.ExecuteAsync(SpecialKeySlot.M5, CancellationToken.None);

        Assert.Equal(0, client.PerformanceCycles);
        Assert.Empty(input.Shortcuts);
        Assert.Empty(input.MediaActions);
    }

    [Theory]
    [InlineData(SpecialKeyActionKind.MouseLeftClick)]
    [InlineData(SpecialKeyActionKind.MouseRightClick)]
    [InlineData(SpecialKeyActionKind.MouseMiddleClick)]
    [InlineData(SpecialKeyActionKind.MouseBack)]
    [InlineData(SpecialKeyActionKind.MouseForward)]
    [InlineData(SpecialKeyActionKind.MouseDoubleClick)]
    [InlineData(SpecialKeyActionKind.MouseScrollUp)]
    [InlineData(SpecialKeyActionKind.MouseScrollDown)]
    public async Task ExecuteAsync_MouseActionUsesConfiguredAction(
        SpecialKeyActionKind action)
    {
        var input = new RecordingInputSender();
        var executor = CreateExecutor(action, input);

        await executor.ExecuteAsync(SpecialKeySlot.M2, CancellationToken.None);

        Assert.Equal([action], input.MouseActions);
    }

    [Theory]
    [InlineData(SpecialKeyActionKind.MediaPreviousTrack)]
    [InlineData(SpecialKeyActionKind.MediaPlayPause)]
    [InlineData(SpecialKeyActionKind.MediaNextTrack)]
    [InlineData(SpecialKeyActionKind.VolumeMute)]
    [InlineData(SpecialKeyActionKind.VolumeDown)]
    [InlineData(SpecialKeyActionKind.VolumeUp)]
    public async Task ExecuteAsync_MediaActionUsesConfiguredAction(
        SpecialKeyActionKind action)
    {
        var input = new RecordingInputSender();
        var executor = CreateExecutor(action, input);

        await executor.ExecuteAsync(SpecialKeySlot.M2, CancellationToken.None);

        Assert.Equal([action], input.MediaActions);
    }

    [Fact]
    public async Task ExecuteAsync_TextUsesConfiguredValue()
    {
        var input = new RecordingInputSender();
        var executor = CreateExecutor(
            SpecialKeyActionKind.TypeText,
            input,
            "OpenBlade");

        await executor.ExecuteAsync(SpecialKeySlot.M2, CancellationToken.None);

        Assert.Equal(["OpenBlade"], input.Text);
    }

    [Theory]
    [InlineData(SpecialKeyActionKind.LaunchApplication, "notepad.exe", false)]
    [InlineData(SpecialKeyActionKind.OpenWebsite, "https://ossblade.org", true)]
    public async Task ExecuteAsync_LaunchUsesConfiguredTargetAndAddressPolicy(
        SpecialKeyActionKind action,
        string target,
        bool requireWebAddress)
    {
        var input = new RecordingInputSender();
        var executor = CreateExecutor(action, input, target);

        await executor.ExecuteAsync(SpecialKeySlot.M2, CancellationToken.None);

        Assert.Equal([(target, requireWebAddress)], input.Launches);
    }

    [Fact]
    public async Task ExecuteAsync_MicrophoneMuteLeavesIndicatorToObserver()
    {
        var input = new RecordingInputSender();
        var client = new RecordingServiceClient();
        var executor = new SpecialKeyActionExecutor(
            new FixedBindings(
                SpecialKeySlot.M2,
                new SpecialKeyAction(SpecialKeyActionKind.MicrophoneMute)),
            client,
            input);

        await executor.ExecuteAsync(SpecialKeySlot.M2, CancellationToken.None);

        Assert.Equal(1, input.MicrophoneMuteCount);
        Assert.Null(client.MicrophoneMuteIndicator);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_CycleLightingExplicitlyRequestsEffect(bool independentBrightness, bool independentLogo)
    {
        var client = new RecordingServiceClient
        {
            LightingCapabilities = DeviceCapabilities.Unavailable() with
            {
                SupportsIndependentLightingControls = independentBrightness || independentLogo,
                LightingEffects = new HashSet<LightingEffect> { LightingEffect.Static, LightingEffect.Wave },
            },
            CurrentLighting = LightingSettingsFactory.Static(new(10, 20, 30), 65) with
            {
                LogoMode = LogoLightingMode.Static,
                IndependentControls = independentBrightness || independentLogo
                    ? new(independentBrightness ? (byte)70 : null,
                        independentLogo ? LogoLightingMode.Breathing : null)
                    : null,
            },
        };
        var executor = new SpecialKeyActionExecutor(
            new FixedBindings(SpecialKeySlot.M1, new(SpecialKeyActionKind.CycleLightingEffect)),
            client,
            new RecordingInputSender());

        await executor.ExecuteAsync(SpecialKeySlot.M1, CancellationToken.None);

        LightingSettings requested = Assert.IsType<LightingSettings>(client.LightingOverride?.Value);
        Assert.Equal(LightingEffect.Wave, requested.Effect);
        Assert.True(requested.ControlsKeyboardEffect);
        Assert.Null(requested.IndependentControls);
        Assert.Equal(independentBrightness ? 70 : 65, requested.Brightness);
        Assert.Equal(independentLogo ? LogoLightingMode.Breathing : LogoLightingMode.Static, requested.LogoMode);
    }

    private static SpecialKeyActionExecutor CreateExecutor(
        SpecialKeyActionKind action,
        RecordingInputSender input,
        string? value = null) => new(
            new FixedBindings(
                SpecialKeySlot.M2,
                new SpecialKeyAction(action, value)),
            new RecordingServiceClient(),
            input);

    private sealed class FixedBindings : ISpecialKeyBindingSource
    {
        private readonly SpecialKeySlot slot;
        private readonly SpecialKeyAction action;

        public FixedBindings(SpecialKeySlot slot, SpecialKeyAction action)
        {
            this.slot = slot;
            this.action = action;
        }

        public SpecialKeyAction GetAction(SpecialKeySlot requested) =>
            requested == slot
                ? action
                : new SpecialKeyAction(SpecialKeyActionKind.Default);

        public bool ShouldHandleInDriverMode(SpecialKeySlot requested) =>
            requested == slot
            && SpecialKeyBindingSource.ShouldHandleInDriverMode(requested, action);
    }

    private sealed class RecordingInputSender : ISpecialKeyInputSender
    {
        public List<string> Shortcuts { get; } = [];
        public List<string> Text { get; } = [];
        public List<SpecialKeyActionKind> MouseActions { get; } = [];
        public List<SpecialKeyActionKind> MediaActions { get; } = [];
        public List<(string Target, bool RequireWebAddress)> Launches { get; } = [];
        public int MicrophoneMuteCount { get; private set; }
        public bool MicrophoneMuted { get; set; }

        public void SendKeyboardShortcut(string shortcut) => Shortcuts.Add(shortcut);

        public void SendText(string text) => Text.Add(text);

        public void SendMouseAction(SpecialKeyActionKind action) => MouseActions.Add(action);

        public void SendMediaAction(SpecialKeyActionKind action) => MediaActions.Add(action);

        public void ToggleMicrophoneMute()
        {
            MicrophoneMuteCount++;
            MicrophoneMuted = !MicrophoneMuted;
        }

        public void Launch(string target, bool requireWebAddress) =>
            Launches.Add((target, requireWebAddress));
    }

    private sealed class RecordingServiceClient : OpenBlade.Testing.UnavailableServiceClient
    {
        public int PerformanceCycles { get; private set; }
        public int GamingModeWrites { get; private set; }
        public bool? GamingModeEnabled { get; set; }
        public bool? MicrophoneMuteIndicator { get; private set; }
        public DeviceCapabilities? LightingCapabilities { get; init; }
        public LightingSettings? CurrentLighting { get; init; }
        public ManualOverride<LightingSettings>? LightingOverride { get; private set; }

        public override Task<ControlOperationResult> CyclePerformanceShortcutAsync(
            PerformanceShortcutSource source,
            CancellationToken token)
        {
            PerformanceCycles++;
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied."));
        }

        public override Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token) =>
            Task.FromResult(CreateStatus(GamingModeEnabled, LightingCapabilities, CurrentLighting));

        public override Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken token)
        {
            LightingOverride = value;
            return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Applied, "Applied."));
        }

        public override Task<ControlOperationResult> ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken token)
        {
            if (value.Control == OneShotLaptopControl.GamingMode)
            {
                GamingModeEnabled = value.Enabled;
                GamingModeWrites++;
            }
            else
            {
                Assert.Equal(
                    OneShotLaptopControl.MicrophoneMuteIndicator,
                    value.Control);
                MicrophoneMuteIndicator = value.Enabled;
            }
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied."));
        }

        private static ServiceStatusSnapshot CreateStatus(bool? gamingModeEnabled,
            DeviceCapabilities? lightingCapabilities = null, LightingSettings? currentLighting = null)
        {
            ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
            return new ServiceStatusSnapshot(
                Revision: 1,
                DeviceVerified: true,
                WritesBlocked: false,
                PotentialControllers: [],
                ControllerDetectionWarning: null,
                ExternalChanges: [],
                Message: "Ready",
                Capabilities: lightingCapabilities ?? DeviceCapabilities.Unavailable(),
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
                        currentLighting ?? profiles.PluggedIn.Lighting,
                        SettingValueSource.PowerProfile),
                    new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
                ActivePowerSource: PowerSource.PluggedIn,
                LiveReadings: null,
                PerformanceOverride: null,
                FanOverride: null,
                LightingOverride: null,
                FullChargeOverride: false,
                GamingModeEnabled: gamingModeEnabled);
        }
    }
}
