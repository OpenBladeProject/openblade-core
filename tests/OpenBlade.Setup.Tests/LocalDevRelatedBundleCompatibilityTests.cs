using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class LocalDevRelatedBundleCompatibilityTests
{
    private const string RoamingData =
        @"C:\Windows\System32\config\systemprofile\AppData\Roaming";
    private const string Startup =
        RoamingData + @"\Microsoft\Windows\Start Menu\Programs\Startup";

    [Fact]
    public void StableToLocalDev_LocalSystemCreatesExactCachedSetupKnownFolder()
    {
        string? createdPath = null;

        LocalDevRelatedBundleCompatibility.PrepareCachedSetupEnvironment(
            true,
            RoamingData,
            Startup,
            path => createdPath = path);

        Assert.Equal(Startup, createdPath);
    }

    [Fact]
    public void PrepareCachedSetupEnvironment_RejectsPathOutsideLocalSystemProfile()
    {
        bool created = false;

        Assert.Throws<InvalidDataException>(() =>
            LocalDevRelatedBundleCompatibility.PrepareCachedSetupEnvironment(
                true,
                RoamingData,
                @"C:\Users\jack\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
                _ => created = true));

        Assert.False(created);
    }

    [Fact]
    public void PrepareCachedSetupEnvironment_OrdinaryUserDoesNotCreateDirectory()
    {
        bool created = false;

        LocalDevRelatedBundleCompatibility.PrepareCachedSetupEnvironment(
            false,
            string.Empty,
            string.Empty,
            _ => created = true);

        Assert.False(created);
    }
}
