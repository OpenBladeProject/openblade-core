using OpenSynapse.Setup;

namespace OpenSynapse.Setup.Tests;

public sealed class TrayProcessIdentityTests
{
    [Theory]
    [InlineData(@"C:\Program Files\OpenSynapse\OpenSynapse.Tray.exe")]
    [InlineData(@"c:\program files\opensynapse\OPENSYNAPSE.TRAY.EXE")]
    public void IsInstalledTray_AcceptsOnlyExactInstalledExecutable(string candidate)
    {
        Assert.True(TrayProcessIdentity.IsInstalledTray(
            candidate,
            @"C:\Program Files\OpenSynapse\OpenSynapse.Tray.exe"));
    }

    [Theory]
    [InlineData(@"C:\Other\OpenSynapse.Tray.exe")]
    [InlineData(@"C:\Program Files\OpenSynapse\OpenSynapse.Tray.Helper.exe")]
    [InlineData("")]
    public void IsInstalledTray_RejectsOtherProcesses(string candidate)
    {
        Assert.False(TrayProcessIdentity.IsInstalledTray(
            candidate,
            @"C:\Program Files\OpenSynapse\OpenSynapse.Tray.exe"));
    }
}
