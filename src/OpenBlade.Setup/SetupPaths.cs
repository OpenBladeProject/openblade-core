namespace OpenBlade.Setup;

public sealed record SetupPaths
{
    private SetupPaths(
        string programFilesRoot,
        string machineDataRoot,
        string userDataRoot,
        string startupRoot,
        string commonStartupRoot)
    {
        ProgramFilesRoot = programFilesRoot;
        MachineDataRoot = machineDataRoot;
        UserDataRoot = userDataRoot;
        StartupRoot = startupRoot;
        CommonStartupRoot = commonStartupRoot;
        InstallDirectory = Path.Combine(programFilesRoot, "OpenBlade");
        MachineDataDirectory = Path.Combine(machineDataRoot, "OpenBlade");
        UserDataDirectory = Path.Combine(userDataRoot, "OpenBlade");
        StartupFile = Path.Combine(commonStartupRoot, "OpenBlade.lnk");
        LegacyTrayStartupFile = Path.Combine(startupRoot, "OpenBlade.lnk");
        SessionAgentStartupFile = Path.Combine(
            commonStartupRoot,
            "OpenBlade.SessionAgent.lnk");
        LegacySessionAgentStartupFile = Path.Combine(
            startupRoot,
            "OpenBlade.SessionAgent.lnk");
        LegacyStartupFile = Path.Combine(startupRoot, "OpenBlade.cmd");
        string programsRoot = Path.GetDirectoryName(commonStartupRoot)
            ?? throw new ArgumentException(
                "The Startup folder must have a Start menu Programs parent.",
                nameof(commonStartupRoot));
        StartMenuFile = Path.Combine(programsRoot, "OpenBlade.lnk");
        UninstallStartMenuFile = Path.Combine(programsRoot, "Uninstall OpenBlade.lnk");
    }

    public string ProgramFilesRoot { get; }
    public string MachineDataRoot { get; }
    public string UserDataRoot { get; }
    public string StartupRoot { get; }
    public string CommonStartupRoot { get; }
    public string InstallDirectory { get; }
    public string MachineDataDirectory { get; }
    public string UserDataDirectory { get; }
    public string StartupFile { get; }
    public string LegacyTrayStartupFile { get; }
    public string SessionAgentStartupFile { get; }
    public string LegacySessionAgentStartupFile { get; }
    public string LegacyStartupFile { get; }
    public string StartMenuFile { get; }
    public string UninstallStartMenuFile { get; }

    public static SetupPaths Create(
        string programFilesRoot,
        string machineDataRoot,
        string userDataRoot,
        string startupRoot,
        string? commonStartupRoot = null) => new(
            NormalizeKnownFolder(programFilesRoot, nameof(programFilesRoot)),
            NormalizeKnownFolder(machineDataRoot, nameof(machineDataRoot)),
            NormalizeKnownFolder(userDataRoot, nameof(userDataRoot)),
            NormalizeKnownFolder(startupRoot, nameof(startupRoot)),
            NormalizeKnownFolder(
                commonStartupRoot ?? startupRoot,
                nameof(commonStartupRoot)));

    public static SetupPaths CreateDefault() => Create(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));

    public static bool IsExactOpenBladeDirectory(string candidate, string knownFolderRoot)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(knownFolderRoot))
        {
            return false;
        }

        try
        {
            string root = NormalizeKnownFolder(knownFolderRoot, nameof(knownFolderRoot));
            string expected = Path.Combine(root, "OpenBlade");
            string actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsExactTransactionSiblingDirectory(string candidate, string programFilesRoot)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(programFilesRoot))
        {
            return false;
        }

        try
        {
            string root = NormalizeKnownFolder(programFilesRoot, nameof(programFilesRoot));
            string actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!string.Equals(Path.GetDirectoryName(actual), root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string name = Path.GetFileName(actual);
            string[] prefixes = ["OpenBlade.installing.", "OpenBlade.rollback.", "OpenBlade.failed."];
            string? prefix = prefixes.SingleOrDefault(
                candidatePrefix => name.StartsWith(candidatePrefix, StringComparison.OrdinalIgnoreCase));
            if (prefix is null || name.Length != prefix.Length + 32)
            {
                return false;
            }

            return name.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsExactUninstallRelayDirectory(string candidate, string programFilesRoot)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(programFilesRoot))
        {
            return false;
        }

        try
        {
            string root = NormalizeKnownFolder(programFilesRoot, nameof(programFilesRoot));
            string actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!string.Equals(Path.GetDirectoryName(actual), root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            const string prefix = "OpenBlade.uninstall.";
            string name = Path.GetFileName(actual);
            return name.Length == prefix.Length + 32
                && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && name.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string NormalizeKnownFolder(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A fully-qualified Windows known-folder path is required.", parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
