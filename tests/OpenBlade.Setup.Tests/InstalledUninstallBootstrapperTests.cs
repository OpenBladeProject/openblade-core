using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class InstalledUninstallBootstrapperTests
{
    private static readonly SetupPaths Paths = SetupPaths.Create(
        @"C:\Program Files",
        @"C:\ProgramData",
        @"C:\Users\jack\AppData\Local",
        @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");

    [Theory]
    [InlineData("uninstall")]
    [InlineData("uninstall", "--purge")]
    public void InstalledUninstallInvocation_RequiresExactInstalledSetupPath(params string[] arguments)
    {
        Assert.True(InstalledUninstallBootstrapper.IsInstalledUninstallInvocation(
            arguments,
            @"C:\Program Files\OpenBlade\OpenBlade.Setup.exe",
            Paths));
    }

    [Theory]
    [InlineData(@"C:\Program Files\OpenBlade\OpenBlade.Setup.Helper.exe")]
    [InlineData(@"C:\Program Files\OpenBladeBackup\OpenBlade.Setup.exe")]
    [InlineData(@"C:\Users\jack\Downloads\OpenBlade.Setup.exe")]
    public void InstalledUninstallInvocation_RejectsNonInstalledExecutables(string executable)
    {
        Assert.False(InstalledUninstallBootstrapper.IsInstalledUninstallInvocation(
            ["uninstall"],
            executable,
            Paths));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall", "--unknown")]
    public void InstalledUninstallInvocation_RejectsNonUninstallCommands(params string[] arguments)
    {
        Assert.False(InstalledUninstallBootstrapper.IsInstalledUninstallInvocation(
            arguments,
            @"C:\Program Files\OpenBlade\OpenBlade.Setup.exe",
            Paths));
    }

    [Fact]
    public void RelayExecutable_RequiresGuardedProgramFilesSiblingAndExactFileName()
    {
        Assert.True(InstalledUninstallBootstrapper.IsRelayExecutable(
            @"C:\Program Files\OpenBlade.uninstall.0123456789abcdef0123456789abcdef\OpenBlade.Setup.exe",
            Paths));
        Assert.False(InstalledUninstallBootstrapper.IsRelayExecutable(
            @"C:\Users\jack\AppData\Local\Temp\OpenBlade.uninstall.0123456789abcdef0123456789abcdef\OpenBlade.Setup.exe",
            Paths));
        Assert.False(InstalledUninstallBootstrapper.IsRelayExecutable(
            @"C:\Program Files\OpenBlade.uninstall.0123456789abcdef0123456789abcdef\Other.exe",
            Paths));
    }
}
