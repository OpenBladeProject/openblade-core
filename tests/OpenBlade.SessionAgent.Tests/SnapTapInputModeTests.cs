using OpenBlade.SessionAgent;

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
}
