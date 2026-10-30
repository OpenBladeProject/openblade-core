using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapInputModeTests
{
    [Fact]
    public void DefaultAndUnrelatedModes_DoNotSelectInputHelper()
    {
        Assert.False(SnapTapInputHost.IsRequested([]));
        Assert.False(SnapTapInputHost.IsRequested(["--observe"]));
        Assert.False(SnapTapInputHost.IsRequested(["--attended-test"]));
        Assert.False(SnapTapInputHost.IsRequested(["--snap-tap-input-extra"]));
        Assert.True(SnapTapInputHost.IsRequested(["--snap-tap-input"]));
        Assert.True(SnapTapInputHost.IsRequested(["--snap-tap-driver-worker"]));
        Assert.False(SnapTapInputHost.IsRequested(["--snap-tap-driver-worker-extra"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1x")]
    [InlineData("2147483648")]
    public void InvalidParent_RejectsBeforePreferenceOrNativeAccess(string parent)
    {
        Assert.Equal(2, SnapTapInputHost.Run(["--snap-tap-input", parent]));
    }

    [Fact]
    public void MissingExtraOrSelfParent_RejectsBeforeNativeAccess()
    {
        Assert.Equal(2, SnapTapInputHost.Run(["--snap-tap-input"]));
        Assert.Equal(2, SnapTapInputHost.Run(["--snap-tap-input", "1", "extra"]));
        Assert.Equal(2, SnapTapInputHost.Run(["--snap-tap-input", Environment.ProcessId.ToString()]));
    }

    [Fact]
    public void StopEndpoint_IsBoundToItsParentProcess()
    {
        Assert.Equal(SnapTapInputProcess.StopEventName(123), SnapTapInputProcess.StopEventName(123));
        Assert.NotEqual(SnapTapInputProcess.StopEventName(123), SnapTapInputProcess.StopEventName(124));
    }

    [Theory]
    [InlineData("windows-hook", KeyboardInputBackend.WindowsHook)]
    [InlineData("razer-driver", KeyboardInputBackend.RazerDriver)]
    public void ExplicitBackendRoundTripsFixedArgument(string argument, KeyboardInputBackend expected)
    {
        Assert.True(SnapTapInputHost.TryParseArguments(["--snap-tap-input", "123", argument], out int parent, out var backend));
        Assert.Equal(123, parent);
        Assert.Equal(expected, backend);
        Assert.Equal(argument, SnapTapInputHost.BackendArgument(expected));
        Assert.False(SnapTapInputHost.TryParseArguments(["--snap-tap-driver-worker", "123", argument], out _, out _));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("WindowsHook")]
    [InlineData("1")]
    [InlineData("future-backend")]
    public void UnknownBackendRejectsBeforeNativeOrPreferenceAccess(string argument)
    {
        Assert.Equal(2, SnapTapInputHost.Run(["--snap-tap-input", "123", argument]));
    }

    [Fact]
    public void LegacyModeDefaultsToDriverAndUnknownCannotLaunch()
    {
        Assert.True(SnapTapInputHost.TryParseArguments(["--snap-tap-input", "123"], out _, out var backend));
        Assert.Equal(KeyboardInputBackend.RazerDriver, backend);
        Assert.True(SnapTapInputHost.TryParseArguments(["--snap-tap-driver-worker", "123"], out _, out backend));
        Assert.Equal(KeyboardInputBackend.RazerDriver, backend);
        Assert.Throws<ArgumentOutOfRangeException>(() => SnapTapInputProcess.Start(KeyboardInputBackend.Unknown));
    }

    [Fact]
    public void InvalidDriverJournalRequiresRecoveryWithoutChangingContents()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade-journal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "snaptap-driver-ownership.json");
        try
        {
            Assert.Null(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => throw new InvalidOperationException()));
            File.WriteAllText(path, "untrusted or incomplete evidence");
            Assert.NotNull(SnapTapInputHost.InspectWindowsDriverOwnership(directory, () => throw new InvalidOperationException()));
            Assert.Equal("untrusted or incomplete evidence", File.ReadAllText(path));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Theory]
    [InlineData("--snap-tap-input", "windows-hook")]
    [InlineData("--snap-tap-input", "razer-driver")]
    [InlineData("--snap-tap-driver-worker", "snap-tap")]
    public void PreferencesDisabledAfterLaunchReturnNoWriteAdmissionCode(string mode, string selector)
    {
        Assert.True(SnapTapInputHost.TryParseArguments([mode, "123", selector], out _, out var backend, out var features));
        var disabled = new UiInputPreferences(SnapTapEnabled: false,
            FunctionRowBackend: KeyboardInputBackend.WindowsHook);
        Assert.Equal(3, SnapTapInputHost.PreferenceRejection(disabled, backend, features));
        var changed = new UiInputPreferences(SnapTapEnabled: true,
            SnapTapBackend: backend == KeyboardInputBackend.WindowsHook ? KeyboardInputBackend.RazerDriver : KeyboardInputBackend.WindowsHook,
            FunctionRowBackend: KeyboardInputBackend.WindowsHook);
        Assert.Equal(3, SnapTapInputHost.PreferenceRejection(changed, backend, features));
    }
}
