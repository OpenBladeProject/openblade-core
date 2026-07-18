using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupPathsTests
{
    [Fact]
    public void Create_MapsEachArtifactToItsRequiredWindowsKnownFolder()
    {
        SetupPaths paths = SetupPaths.Create(
            @"C:\Program Files",
            @"C:\ProgramData",
            @"C:\Users\jack\AppData\Local",
            @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");

        Assert.Equal(@"C:\Program Files\OpenBlade", paths.InstallDirectory);
        Assert.Equal(@"C:\ProgramData\OpenBlade", paths.MachineDataDirectory);
        Assert.Equal(@"C:\Users\jack\AppData\Local\OpenBlade", paths.UserDataDirectory);
        Assert.Equal(
            @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\OpenBlade.lnk",
            paths.StartupFile);
        Assert.Equal(
            @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\OpenBlade.SessionAgent.lnk",
            paths.SessionAgentStartupFile);
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
}
