namespace OpenBlade.Tray.Tests;

public sealed class TrayApplicationRestartContractTests
{
    [Fact]
    public void RegistrationFollowsInstanceOwnershipAndPrecedesWindowCreation()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        int instanceOwnership = app.IndexOf(
            "TrayInstanceCoordinator.TryStart(",
            StringComparison.Ordinal);
        int duplicateExit = app.IndexOf(
            "Exit();",
            instanceOwnership,
            StringComparison.Ordinal);
        int registration = app.IndexOf(
            "new WindowsApplicationRestartRegistration()",
            instanceOwnership,
            StringComparison.Ordinal);
        int windowCreation = app.IndexOf(
            "CreateMainWindow(registration)",
            registration,
            StringComparison.Ordinal);

        Assert.True(instanceOwnership >= 0);
        Assert.True(duplicateExit > instanceOwnership);
        Assert.True(registration > duplicateExit);
        Assert.True(windowCreation > registration);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null
            && !File.Exists(Path.Combine(directory, "OpenBlade.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.False(string.IsNullOrWhiteSpace(directory));
        return File.ReadAllText(Path.Combine([directory!, .. parts]));
    }
}
