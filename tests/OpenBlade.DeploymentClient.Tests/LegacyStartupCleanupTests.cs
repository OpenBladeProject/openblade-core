namespace OpenBlade.DeploymentClient.Tests;

public sealed class LegacyStartupCleanupTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"OpenBlade.DeploymentClient.Tests.{Guid.NewGuid():N}");

    [Fact]
    public void Policy_ResolvesOnlyThreeFixedStartupEntries()
    {
        string startupRoot = Path.Combine(root, "Startup");

        IReadOnlyList<string>? targets = LegacyStartupCleanupPolicy.GetExactTargets(
            startupRoot + Path.DirectorySeparatorChar);

        Assert.NotNull(targets);
        Assert.Equal(
            [
                Path.Combine(startupRoot, "OpenBlade.lnk"),
                Path.Combine(startupRoot, "OpenBlade.SessionAgent.lnk"),
                Path.Combine(startupRoot, "OpenBlade.cmd"),
            ],
            targets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Startup")]
    public void Policy_RejectsUnavailableOrRelativeStartupRoot(string startupRoot)
    {
        Assert.Null(LegacyStartupCleanupPolicy.GetExactTargets(startupRoot));
    }

    [Fact]
    public void FileSystemCleanup_DeletesOnlyFixedLegacyEntries()
    {
        string startupRoot = Path.Combine(root, "Startup");
        Directory.CreateDirectory(startupRoot);
        foreach (string fileName in LegacyStartupCleanupPolicy.FileNames)
        {
            File.WriteAllText(Path.Combine(startupRoot, fileName), "legacy");
        }

        string unrelated = Path.Combine(startupRoot, "Keep.lnk");
        File.WriteAllText(unrelated, "keep");

        WindowsLegacyStartupCleanup.RemoveExactEntries(startupRoot);

        Assert.All(
            LegacyStartupCleanupPolicy.FileNames,
            fileName => Assert.False(File.Exists(Path.Combine(startupRoot, fileName))));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void FileSystemCleanup_UnavailableStartupRootFailsClosed()
    {
        Assert.Throws<InvalidDataException>(
            () => WindowsLegacyStartupCleanup.RemoveExactEntries(string.Empty));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
