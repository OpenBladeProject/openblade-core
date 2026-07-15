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

        Assert.Equal(@"C:\Program Files\OpenSynapse", paths.InstallDirectory);
        Assert.Equal(@"C:\ProgramData\OpenSynapse", paths.MachineDataDirectory);
        Assert.Equal(@"C:\Users\jack\AppData\Local\OpenSynapse", paths.UserDataDirectory);
        Assert.Equal(
            @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\OpenSynapse.lnk",
            paths.StartupFile);
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
    [InlineData(@"C:\Program Files\OpenSynapse")]
    [InlineData(@"C:\Program Files\OpenSynapse\")]
    [InlineData(@"C:\PROGRA~1\OpenSynapse")]
    public void IsExactOpenSynapseDirectory_AcceptsCanonicalExactChild(string candidate)
    {
        Assert.True(SetupPaths.IsExactOpenSynapseDirectory(candidate, @"C:\Program Files"));
    }

    [Theory]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\OpenSynapseBackup")]
    [InlineData(@"C:\Program Files\Vendor\OpenSynapse")]
    [InlineData(@"C:\OpenSynapse")]
    public void IsExactOpenSynapseDirectory_RejectsRootsSiblingsAndNestedLookalikes(string candidate)
    {
        Assert.False(SetupPaths.IsExactOpenSynapseDirectory(candidate, @"C:\Program Files"));
    }
}
