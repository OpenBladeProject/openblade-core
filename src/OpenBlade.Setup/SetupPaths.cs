namespace OpenBlade.Setup;

public sealed record SetupPaths
{
    private SetupPaths(
        string programFilesRoot,
        string machineDataRoot,
        string userDataRoot,
        string startupRoot)
    {
        ProgramFilesRoot = programFilesRoot;
        MachineDataRoot = machineDataRoot;
        UserDataRoot = userDataRoot;
        StartupRoot = startupRoot;
        InstallDirectory = Path.Combine(programFilesRoot, "OpenSynapse");
        MachineDataDirectory = Path.Combine(machineDataRoot, "OpenSynapse");
        UserDataDirectory = Path.Combine(userDataRoot, "OpenSynapse");
        StartupFile = Path.Combine(startupRoot, "OpenSynapse.lnk");
        LegacyStartupFile = Path.Combine(startupRoot, "OpenSynapse.cmd");
    }

    public string ProgramFilesRoot { get; }
    public string MachineDataRoot { get; }
    public string UserDataRoot { get; }
    public string StartupRoot { get; }
    public string InstallDirectory { get; }
    public string MachineDataDirectory { get; }
    public string UserDataDirectory { get; }
    public string StartupFile { get; }
    public string LegacyStartupFile { get; }

    public static SetupPaths Create(
        string programFilesRoot,
        string machineDataRoot,
        string userDataRoot,
        string startupRoot) => new(
            NormalizeKnownFolder(programFilesRoot, nameof(programFilesRoot)),
            NormalizeKnownFolder(machineDataRoot, nameof(machineDataRoot)),
            NormalizeKnownFolder(userDataRoot, nameof(userDataRoot)),
            NormalizeKnownFolder(startupRoot, nameof(startupRoot)));

    public static bool IsExactOpenSynapseDirectory(string candidate, string knownFolderRoot)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(knownFolderRoot))
        {
            return false;
        }

        try
        {
            string root = NormalizeKnownFolder(knownFolderRoot, nameof(knownFolderRoot));
            string expected = Path.Combine(root, "OpenSynapse");
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

    private static string NormalizeKnownFolder(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A fully-qualified Windows known-folder path is required.", parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
