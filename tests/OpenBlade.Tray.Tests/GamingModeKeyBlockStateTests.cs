using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class GamingModeKeyBlockStateTests
{
    [Fact]
    public void WindowsKey_DownAndUpAreSuppressedAsOnePair()
    {
        var state = new GamingModeKeyBlockState();
        var enabled = new GamingModeKeyPolicy(
            BlockWindowsKey: true,
            BlockAltTab: false);

        Assert.True(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyLeftWindows,
            keyDown: true,
            enabled));
        Assert.True(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyLeftWindows,
            keyDown: false,
            GamingModeKeyPolicy.Disabled));
    }

    [Fact]
    public void AltTab_SuppressesTabWithoutSwallowingAlt()
    {
        var state = new GamingModeKeyBlockState();
        var enabled = new GamingModeKeyPolicy(
            BlockWindowsKey: false,
            BlockAltTab: true);

        Assert.False(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyLeftAlt,
            keyDown: true,
            enabled));
        Assert.True(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyTab,
            keyDown: true,
            enabled));
        Assert.True(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyTab,
            keyDown: false,
            GamingModeKeyPolicy.Disabled));
        Assert.False(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyLeftAlt,
            keyDown: false,
            enabled));
    }

    [Fact]
    public void OrdinaryKeysAndDisabledPolicyPassThrough()
    {
        var state = new GamingModeKeyBlockState();

        Assert.False(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyRightWindows,
            keyDown: true,
            GamingModeKeyPolicy.Disabled));
        Assert.False(state.ShouldSuppress(
            0x41,
            keyDown: true,
            new GamingModeKeyPolicy(true, true)));
    }

    [Fact]
    public void AltTab_RemainsBlockedWhileEitherAltKeyIsHeld()
    {
        var state = new GamingModeKeyBlockState();
        var enabled = new GamingModeKeyPolicy(
            BlockWindowsKey: false,
            BlockAltTab: true);

        Assert.False(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyLeftAlt,
            keyDown: true,
            enabled));
        Assert.False(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyRightAlt,
            keyDown: true,
            enabled));
        Assert.False(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyLeftAlt,
            keyDown: false,
            enabled));
        Assert.True(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyTab,
            keyDown: true,
            enabled));
    }

    [Fact]
    public void AltTab_UsesHookAltContextWhenHookStartedMidChord()
    {
        var state = new GamingModeKeyBlockState();

        Assert.True(state.ShouldSuppress(
            GamingModeKeyBlockState.VirtualKeyTab,
            keyDown: true,
            new GamingModeKeyPolicy(false, true),
            altContext: true));
    }
}
