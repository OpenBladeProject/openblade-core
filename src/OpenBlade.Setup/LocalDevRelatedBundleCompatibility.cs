using System.Security.Principal;

namespace OpenBlade.Setup;

internal static class LocalDevRelatedBundleCompatibility
{
    internal static void PrepareCachedSetupEnvironment()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
        {
            return;
        }

        PrepareCachedSetupEnvironment(
            isLocalSystem: true,
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            Environment.GetFolderPath(
                Environment.SpecialFolder.Startup,
                Environment.SpecialFolderOption.DoNotVerify),
            path => Directory.CreateDirectory(path));
    }

    internal static void PrepareCachedSetupEnvironment(
        bool isLocalSystem,
        string roamingDataRoot,
        string startupRoot,
        Action<string> createDirectory)
    {
        ArgumentNullException.ThrowIfNull(createDirectory);
        if (!isLocalSystem)
        {
            return;
        }

        string normalizedRoamingData = NormalizeKnownFolder(
            roamingDataRoot,
            nameof(roamingDataRoot));
        string normalizedStartup = NormalizeKnownFolder(
            startupRoot,
            nameof(startupRoot));
        string expectedStartup = Path.Combine(
            normalizedRoamingData,
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs",
            "Startup");
        if (!string.Equals(
                normalizedStartup,
                expectedStartup,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The LocalSystem Startup known folder is outside its roaming profile.");
        }

        createDirectory(normalizedStartup);
    }

    private static string NormalizeKnownFolder(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "A fully-qualified Windows known-folder path is required.",
                parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
