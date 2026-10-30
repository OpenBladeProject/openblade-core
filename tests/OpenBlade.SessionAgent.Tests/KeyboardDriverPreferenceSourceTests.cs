using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class KeyboardDriverPreferenceSourceTests
{
    [Theory]
    [InlineData(false, KeyboardInputBackend.RazerDriver, KeyboardInputBackend.WindowsHook, KeyboardDriverFeatures.None)]
    [InlineData(true, KeyboardInputBackend.WindowsHook, KeyboardInputBackend.RazerDriver, KeyboardDriverFeatures.FunctionRow)]
    [InlineData(true, KeyboardInputBackend.RazerDriver, KeyboardInputBackend.WindowsHook, KeyboardDriverFeatures.SnapTap)]
    [InlineData(true, KeyboardInputBackend.RazerDriver, KeyboardInputBackend.RazerDriver, KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow)]
    [InlineData(false, KeyboardInputBackend.Unknown, KeyboardInputBackend.RazerDriver, KeyboardDriverFeatures.FunctionRow)]
    public void DriverDemandIsExactSelectedUnion(bool enabled, KeyboardInputBackend snap,
        KeyboardInputBackend fn, KeyboardDriverFeatures expected)
    {
        Assert.Equal(expected, RazerKeyboardDriverPreferenceSource.SelectFeatures(enabled, snap, fn));
    }

    [Fact]
    public void UnionChangeEmitsAnObservedOffGenerationBeforeRearming()
    {
        using var demand = new RazerKeyboardDriverPreferenceSource();
        List<bool> signals = [];
        demand.SnapTapEnabledChanged += signals.Add;
        demand.SetFeatures(KeyboardDriverFeatures.SnapTap);
        demand.SetFeatures(KeyboardDriverFeatures.SnapTap);
        demand.SetFeatures(KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow);
        Assert.Equal(new[] { false, true, false, true }, signals);
        Assert.True(demand.RequestsFunctionRow);
        demand.SetFeatures(KeyboardDriverFeatures.None);
        Assert.False(demand.SnapTapEnabled);
        Assert.False(signals.Last());
    }

    [Theory]
    [InlineData("snap-tap", KeyboardDriverFeatures.SnapTap)]
    [InlineData("function-row", KeyboardDriverFeatures.FunctionRow)]
    [InlineData("snap-tap-and-function-row", KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow)]
    public void ImmutableScopeIsSharedBySupervisorAndWorker(string argument, KeyboardDriverFeatures expected)
    {
        Assert.Equal(argument, SnapTapInputHost.FeaturesArgument(expected));
        Assert.True(SnapTapInputHost.TryParseArguments(["--snap-tap-input", "123", "razer-driver", argument], out _, out _, out var scope));
        Assert.Equal(expected, scope);
        Assert.True(SnapTapInputHost.TryParseArguments(["--snap-tap-driver-worker", "123", argument], out _, out _, out scope));
        Assert.Equal(expected, scope);
        Assert.False(SnapTapInputHost.TryParseArguments(["--snap-tap-input", "123", "windows-hook", argument], out _, out _, out _));
    }

    [Fact]
    public void FunctionRowDriverDoesNotRequireSnapTapEnabledAndRejectsChangedUnion()
    {
        var preferences = new UiInputPreferences(SnapTapEnabled: false, FunctionRowBackend: KeyboardInputBackend.RazerDriver);
        Assert.Null(SnapTapInputHost.PreferenceRejection(preferences, KeyboardInputBackend.RazerDriver, KeyboardDriverFeatures.FunctionRow));
        Assert.Equal(3, SnapTapInputHost.PreferenceRejection(preferences, KeyboardInputBackend.RazerDriver, KeyboardDriverFeatures.SnapTap));
        Assert.Equal(3, SnapTapInputHost.PreferenceRejection(preferences, KeyboardInputBackend.WindowsHook, KeyboardDriverFeatures.SnapTap));
    }

    [Fact]
    public void BackendStopEndpointsAreIndependent()
    {
        Assert.NotEqual(SnapTapInputProcess.StopEventName(123, KeyboardInputBackend.WindowsHook),
            SnapTapInputProcess.StopEventName(123, KeyboardInputBackend.RazerDriver));
    }
}
