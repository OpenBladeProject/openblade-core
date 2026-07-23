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
    public async Task ExecuteAsync_MicrophoneMuteUsesInputSender()
    {
        var input = new RecordingInputSender();
        var executor = CreateExecutor(SpecialKeyActionKind.MicrophoneMute, input);

        await executor.ExecuteAsync(SpecialKeySlot.M2, CancellationToken.None);

        Assert.Equal(1, input.MicrophoneMuteCount);
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

        public bool OverridesDefaultBehavior(SpecialKeySlot requested) =>
            requested == slot
            && SpecialKeyBindingDefaults.OverridesDefaultBehavior(action);
    }

    private sealed class RecordingInputSender : ISpecialKeyInputSender
    {
        public List<string> Shortcuts { get; } = [];
        public List<string> Text { get; } = [];
        public List<SpecialKeyActionKind> MouseActions { get; } = [];
        public List<SpecialKeyActionKind> MediaActions { get; } = [];
        public List<(string Target, bool RequireWebAddress)> Launches { get; } = [];
        public int MicrophoneMuteCount { get; private set; }

        public void SendKeyboardShortcut(string shortcut) => Shortcuts.Add(shortcut);

        public void SendText(string text) => Text.Add(text);

        public void SendMouseAction(SpecialKeyActionKind action) => MouseActions.Add(action);

        public void SendMediaAction(SpecialKeyActionKind action) => MediaActions.Add(action);

        public void ToggleMicrophoneMute() => MicrophoneMuteCount++;

        public void Launch(string target, bool requireWebAddress) =>
            Launches.Add((target, requireWebAddress));
    }

    private sealed class RecordingServiceClient : IServiceClient
    {
        public int PerformanceCycles { get; private set; }

        public Task<ControlOperationResult> CyclePerformanceShortcutAsync(
            PerformanceShortcutSource source,
            CancellationToken token)
        {
            PerformanceCycles++;
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied."));
        }

        public Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token) =>
            throw new NotSupportedException();

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
            CancellationToken token) =>
            throw new NotSupportedException();

        public Task<ControlOperationResult> UpdateProfilesAsync(
            ProfileSet profiles,
            CancellationToken token) =>
            throw new NotSupportedException();

        public Task<ControlOperationResult> SetPerformanceOverrideAsync(
            ManualOverride<PerformancePreset>? value,
            CancellationToken token) =>
            throw new NotSupportedException();

        public Task<ControlOperationResult> SetFanOverrideAsync(
            ManualOverride<FanMode>? value,
            CancellationToken token) =>
            throw new NotSupportedException();

        public Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken token) =>
            throw new NotSupportedException();

        public Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken token) =>
            throw new NotSupportedException();

        public Task<ControlOperationResult> SetBatteryProtectionAsync(
            bool enabled,
            CancellationToken token) =>
            throw new NotSupportedException();

        public Task<ControlOperationResult> SetFullChargeOverrideAsync(
            bool enabled,
            CancellationToken token) =>
            throw new NotSupportedException();
    }
}
