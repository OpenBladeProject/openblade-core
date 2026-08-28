using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupPathsTests
{
    [Fact]
    public void Create_CommonStartupWithoutProgramsParentNamesCorrectParameter()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => SetupPaths.Create(
            @"C:\Program Files",
            @"C:\ProgramData",
            @"C:\Users\jack\AppData\Local",
            @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
            @"C:\"));

        Assert.Equal("commonStartupRoot", exception.ParamName);
    }

    [Fact]
    public void Create_MapsEachArtifactToItsRequiredWindowsKnownFolder()
    {
        SetupPaths paths = SetupPaths.Create(
            @"C:\Program Files",
            @"C:\ProgramData",
            @"C:\Users\jack\AppData\Local",
            @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup");

        Assert.Equal(@"C:\Program Files\OpenBlade", paths.InstallDirectory);
        Assert.Equal(@"C:\ProgramData\OpenBlade", paths.MachineDataDirectory);
        Assert.Equal(@"C:\Users\jack\AppData\Local\OpenBlade", paths.UserDataDirectory);
        Assert.Equal(
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\OpenBlade.lnk",
            paths.StartupFile);
        Assert.Equal(
            @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\OpenBlade.lnk",
            paths.LegacyTrayStartupFile);
        Assert.Equal(
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\OpenBlade.SessionAgent.lnk",
            paths.SessionAgentStartupFile);
        Assert.Equal(
            @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\OpenBlade.SessionAgent.lnk",
            paths.LegacySessionAgentStartupFile);
        Assert.Equal(
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\OpenBlade.lnk",
            paths.StartMenuFile);
        Assert.Equal(
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Uninstall OpenBlade.lnk",
            paths.UninstallStartMenuFile);
    }

    [Fact]
    public void CreateLocalSystem_UsesOnlyCommonStartupForShortcutCleanup()
    {
        SetupPaths paths = SetupPaths.CreateLocalSystem(
            @"C:\Program Files",
            @"C:\ProgramData",
            @"C:\Windows\System32\config\systemprofile\AppData\Local",
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup");

        Assert.Equal(paths.CommonStartupRoot, paths.StartupRoot);
        Assert.Equal(paths.StartupFile, paths.LegacyTrayStartupFile);
        Assert.Equal(paths.SessionAgentStartupFile, paths.LegacySessionAgentStartupFile);
        Assert.Equal(
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\OpenBlade.cmd",
            paths.LegacyStartupFile);
        Assert.Equal(
            @"C:\Windows\System32\config\systemprofile\AppData\Local\OpenBlade",
            paths.UserDataDirectory);
    }

    [Fact]
    public void StableToLocalDev_LocalSystemPredecessorDoesNotRequireUserStartup() =>
        AssertLocalSystemUpgradePaths(
            @"C:\Windows\System32\config\systemprofile\AppData\Local\StableUpgrade");

    [Fact]
    public void LocalDevToLocalDev_LocalSystemPredecessorDoesNotRequireUserStartup() =>
        AssertLocalSystemUpgradePaths(
            @"C:\Windows\System32\config\systemprofile\AppData\Local\LocalDevUpgrade");

    [Fact]
    public void CreateForExecutionIdentity_OrdinaryUserRetainsUserStartup()
    {
        SetupPaths paths = SetupPaths.CreateForExecutionIdentity(
            false,
            @"C:\Program Files",
            @"C:\ProgramData",
            @"C:\Users\jack\AppData\Local",
            @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup");

        Assert.Equal(
            @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
            paths.StartupRoot);
        Assert.NotEqual(paths.CommonStartupRoot, paths.StartupRoot);
    }

    [Theory]
    [InlineData(@"C:\Program Files\OpenBlade.uninstall.0123456789abcdef0123456789abcdef")]
    [InlineData(@"C:\Program Files\OPENBLADE.UNINSTALL.0123456789ABCDEF0123456789ABCDEF")]
    public void ExactUninstallRelayDirectory_AcceptsGuardedSibling(string candidate)
    {
        Assert.True(SetupPaths.IsExactUninstallRelayDirectory(candidate, @"C:\Program Files"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\OpenBlade.uninstall.bad")]
    [InlineData(@"C:\Program Files\OpenBlade.uninstall.0123456789abcdef0123456789abcdef\child")]
    [InlineData(@"C:\Users\jack\OpenBlade.uninstall.0123456789abcdef0123456789abcdef")]
    public void ExactUninstallRelayDirectory_RejectsUnexpectedPath(string candidate)
    {
        Assert.False(SetupPaths.IsExactUninstallRelayDirectory(candidate, @"C:\Program Files"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative-folder")]
    public void Create_RejectsMissingOrRelativeKnownFolders(string invalidFolder)
    {
        Assert.Throws<ArgumentException>(() => SetupPaths.Create(
            invalidFolder,
            @"C:\ProgramData",
            @"C:\Users\jack\AppData\Local",
            @"C:\Users\jack\Startup"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\OpenBlade")]
    [InlineData(@"C:\Program Files\OpenBlade\")]
    [InlineData(@"C:\PROGRA~1\OpenBlade")]
    public void IsExactOpenBladeDirectory_AcceptsCanonicalExactChild(string candidate) =>
        Assert.True(SetupPaths.IsExactOpenBladeDirectory(candidate, @"C:\Program Files"));

    [Theory]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\OpenBladeBackup")]
    [InlineData(@"C:\Program Files\Vendor\OpenBlade")]
    [InlineData(@"C:\OpenBlade")]
    [InlineData(@"C:\Program Files\Open" + "Synapse")]
    public void IsExactOpenBladeDirectory_RejectsUnownedPaths(string candidate) =>
        Assert.False(SetupPaths.IsExactOpenBladeDirectory(candidate, @"C:\Program Files"));

    [Theory]
    [InlineData(@"C:\Program Files\OpenBlade.installing.0123456789abcdef0123456789abcdef")]
    [InlineData(@"C:\Program Files\OpenBlade.rollback.0123456789abcdef0123456789abcdef")]
    [InlineData(@"C:\Program Files\OpenBlade.failed.0123456789abcdef0123456789abcdef")]
    public void IsExactTransactionSiblingDirectory_AcceptsOnlyStrictOwnedSiblingNames(string candidate) =>
        Assert.True(SetupPaths.IsExactTransactionSiblingDirectory(candidate, @"C:\Program Files"));

    [Theory]
    [InlineData(@"C:\Program Files\OpenBlade.installing.bad")]
    [InlineData(@"C:\Program Files\OpenBlade.rollback.0123456789abcdef0123456789abcdef\child")]
    [InlineData(@"C:\Other\OpenBlade.failed.0123456789abcdef0123456789abcdef")]
    [InlineData(@"C:\Program Files\Other.installing.0123456789abcdef0123456789abcdef")]
    public void IsExactTransactionSiblingDirectory_RejectsAnythingOutsideExactSiblingPattern(string candidate) =>
        Assert.False(SetupPaths.IsExactTransactionSiblingDirectory(candidate, @"C:\Program Files"));

    private static void AssertLocalSystemUpgradePaths(string userDataRoot)
    {
        SetupPaths paths = SetupPaths.CreateForExecutionIdentity(
            true,
            @"C:\Program Files",
            @"C:\ProgramData",
            userDataRoot,
            string.Empty,
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup");

        Assert.Equal(paths.CommonStartupRoot, paths.StartupRoot);
        Assert.Equal(paths.StartupFile, paths.LegacyTrayStartupFile);
        Assert.Equal(paths.SessionAgentStartupFile, paths.LegacySessionAgentStartupFile);
        Assert.Equal(Path.Combine(userDataRoot, "OpenBlade"), paths.UserDataDirectory);
    }
}
