namespace OpenBlade.Setup;

public static class TrayProcessIdentity
{
    public static bool IsInstalledTray(string? candidatePath, string installedPath) =>
        IsExactInstalledExecutable(candidatePath, installedPath);

    public static bool IsExactInstalledExecutable(string? candidatePath, string installedPath) =>
        !string.IsNullOrWhiteSpace(candidatePath)
        && Path.IsPathFullyQualified(candidatePath)
        && string.Equals(
            Path.GetFullPath(candidatePath),
            Path.GetFullPath(installedPath),
            StringComparison.OrdinalIgnoreCase);
}
