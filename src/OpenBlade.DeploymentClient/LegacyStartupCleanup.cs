namespace OpenBlade.DeploymentClient;

internal interface ILegacyStartupCleanup
{
    void RemoveCurrentUserEntries();
}

internal static class LegacyStartupCleanupPolicy
{
    internal static IReadOnlyList<string> FileNames { get; } = Array.AsReadOnly(
        new[]
        {
            "OpenBlade.lnk",
            "OpenBlade.SessionAgent.lnk",
            "OpenBlade.cmd",
        });

    internal static IReadOnlyList<string>? GetExactTargets(string startupRoot)
    {
        if (string.IsNullOrWhiteSpace(startupRoot) || !Path.IsPathFullyQualified(startupRoot))
        {
            return null;
        }

        try
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(startupRoot));
            return FileNames.Select(fileName => Path.Combine(normalizedRoot, fileName)).ToArray();
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

internal sealed class WindowsLegacyStartupCleanup : ILegacyStartupCleanup
{
    public void RemoveCurrentUserEntries()
    {
        string startupRoot = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        RemoveExactEntries(startupRoot);
    }

    internal static void RemoveExactEntries(string startupRoot)
    {
        IReadOnlyList<string>? targets = LegacyStartupCleanupPolicy.GetExactTargets(startupRoot);
        if (targets is null)
        {
            throw new InvalidDataException(
                "The current user's Startup known-folder path is unavailable.");
        }

        foreach (string target in targets)
        {
            File.Delete(target);
        }
    }
}
