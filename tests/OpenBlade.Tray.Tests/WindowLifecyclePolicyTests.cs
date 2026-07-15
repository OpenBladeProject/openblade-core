using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class WindowLifecyclePolicyTests
{
    [Fact]
    public void UserClose_HidesWindowAndKeepsTrayProcessRunning()
    {
        WindowCloseDecision decision = WindowLifecyclePolicy.Decide(exitRequested: false);

        Assert.Equal(WindowCloseDecision.Hide, decision);
    }

    [Fact]
    public void TrayExit_AllowsWindowAndTrayProcessToClose()
    {
        WindowCloseDecision decision = WindowLifecyclePolicy.Decide(exitRequested: true);

        Assert.Equal(WindowCloseDecision.Exit, decision);
    }
}
