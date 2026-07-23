using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SpecialKeyActionExecutorTests
{
    [Fact]
    public void ParseShortcut_ParsesModifiersFunctionAndNavigationKeys()
    {
        (ushort key, ushort[] modifiers) =
            WindowsSpecialKeyInputSender.ParseShortcut("Ctrl+Shift+M");
        Assert.Equal(0x4D, key);
        Assert.Equal([0x11, 0x10], modifiers);

        (key, modifiers) = WindowsSpecialKeyInputSender.ParseShortcut("Alt+F12");
        Assert.Equal(0x7B, key);
        Assert.Equal([0x12], modifiers);

        (key, modifiers) = WindowsSpecialKeyInputSender.ParseShortcut("Page Up");
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
            () => WindowsSpecialKeyInputSender.ParseShortcut(value));
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
        public List<SpecialKeyActionKind> MediaActions { get; } = [];

        public void SendKeyboardShortcut(string shortcut) => Shortcuts.Add(shortcut);

        public void SendText(string text)
        {
        }

        public void SendMouseAction(SpecialKeyActionKind action)
        {
        }

        public void SendMediaAction(SpecialKeyActionKind action) => MediaActions.Add(action);

        public void ToggleMicrophoneMute()
        {
        }

        public void Launch(string target, bool requireWebAddress)
        {
        }
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
