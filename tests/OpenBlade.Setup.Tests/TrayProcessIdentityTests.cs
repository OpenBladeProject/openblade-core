using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class TrayProcessIdentityTests
{
    [Theory]
    [InlineData(@"C:\Program Files\OpenBlade\OpenBlade.Tray.exe")]
    [InlineData(@"c:\program files\openblade\OPENBLADE.TRAY.EXE")]
    public void IsInstalledTray_AcceptsOnlyExactInstalledExecutable(string candidate)
    {
        Assert.True(TrayProcessIdentity.IsInstalledTray(
            candidate,
            @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe"));
    }

    [Theory]
    [InlineData(@"C:\Other\OpenBlade.Tray.exe")]
    [InlineData(@"C:\Program Files\OpenBlade\OpenBlade.Tray.Helper.exe")]
    [InlineData("")]
    public void IsInstalledTray_RejectsOtherProcesses(string candidate)
    {
        Assert.False(TrayProcessIdentity.IsInstalledTray(
            candidate,
            @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.exe", true)]
    [InlineData(@"c:\program files\openblade\OPENBLADE.SESSIONAGENT.EXE", true)]
    [InlineData(@"C:\worktree\OpenBlade.SessionAgent.exe", false)]
    [InlineData(@"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.Helper.exe", false)]
    [InlineData("", false)]
    public void IsExactInstalledExecutable_AcceptsOnlyExactAgentPath(
        string candidate,
        bool expected) =>
        Assert.Equal(
            expected,
            TrayProcessIdentity.IsExactInstalledExecutable(
                candidate,
                @"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.exe"));
}
