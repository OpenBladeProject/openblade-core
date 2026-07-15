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
}
