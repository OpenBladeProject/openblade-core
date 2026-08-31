namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAgentApplicationRestartContractTests
{
    [Fact]
    public void RegistrationFollowsBothPipeOwnersAndPrecedesDisplayInitialization()
    {
        string runAsync = ReadRunAsync();
        int primaryOwner = runAsync.IndexOf(
            "TryAcquireStartupOwnership(",
            StringComparison.Ordinal);
        int gpuOwner = runAsync.IndexOf(
            "SessionAgentPipeOwner.TryAcquire(",
            primaryOwner + 1,
            StringComparison.Ordinal);
        int registration = runAsync.IndexOf(
            "new WindowsApplicationRestartRegistration()",
            StringComparison.Ordinal);
        int displayInitialization = runAsync.IndexOf(
            "new WindowsDisplaySettingsController()",
            StringComparison.Ordinal);

        Assert.True(primaryOwner >= 0);
        Assert.True(gpuOwner > primaryOwner);
        Assert.True(registration > gpuOwner);
        Assert.True(displayInitialization > registration);
    }

    [Fact]
    public void IdentityRelaunchUnregistersBeforeStartingReplacement()
    {
        string runAsync = ReadRunAsync();
        int identityBranch = runAsync.IndexOf(
            "if (relaunchForIdentity)",
            StringComparison.Ordinal);
        int unregister = runAsync.IndexOf(
            "applicationRestartRegistration.TryUnregister()",
            identityBranch,
            StringComparison.Ordinal);
        int relaunch = runAsync.IndexOf(
            "return RelaunchAfterIdentityRegistration();",
            identityBranch,
            StringComparison.Ordinal);

        Assert.True(identityBranch >= 0);
        Assert.True(unregister > identityBranch);
        Assert.True(relaunch > unregister);
    }

    private static string ReadRunAsync()
    {
        string program = ReadRepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "Program.cs");
        int start = program.IndexOf(
            "private static async Task<int> RunAsync",
            StringComparison.Ordinal);
        int end = program.IndexOf(
            "internal static bool ShouldRelaunchForIdentity",
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        return program[start..end];
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
