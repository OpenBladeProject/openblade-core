namespace OpenBlade.Setup;

public enum SetupCommand
{
    Install,
    Uninstall,
    UninstallAndPurge,
}

public readonly record struct SetupInvocation(
    SetupCommand Command,
    bool BundleManaged,
    bool RecoverOrphanedBundle = false,
    string? ProgressPipeName = null,
    string? BundleOwnerId = null);

public static class SetupCommandParser
{
    public static bool TryParseInvocation(
        IReadOnlyList<string> arguments,
        out SetupInvocation invocation)
    {
        if (arguments.Count == 1
            && string.Equals(arguments[0], "install", StringComparison.OrdinalIgnoreCase))
        {
            invocation = new(SetupCommand.Install, BundleManaged: false);
            return true;
        }

        if (TryParseBundleManaged(
                arguments,
                "install",
                allowPurge: false,
                out _,
                out string? installProgressPipe,
                out string? installBundleOwnerId))
        {
            invocation = new(
                SetupCommand.Install,
                BundleManaged: true,
                ProgressPipeName: installProgressPipe,
                BundleOwnerId: installBundleOwnerId);
            return true;
        }

        if (arguments.Count == 1
            && string.Equals(arguments[0], "uninstall", StringComparison.OrdinalIgnoreCase))
        {
            invocation = new(SetupCommand.Uninstall, BundleManaged: false);
            return true;
        }

        if (IsInteractiveUninstall(arguments))
        {
            invocation = new(SetupCommand.Uninstall, BundleManaged: false);
            return true;
        }

        if (TryParseBundleManaged(
                arguments,
                "uninstall",
                allowPurge: true,
                out bool purge,
                out string? uninstallProgressPipe,
                out string? uninstallBundleOwnerId))
        {
            invocation = new(
                purge ? SetupCommand.UninstallAndPurge : SetupCommand.Uninstall,
                BundleManaged: true,
                ProgressPipeName: uninstallProgressPipe,
                BundleOwnerId: uninstallBundleOwnerId);
            return true;
        }

        if (arguments.Count == 2
            && string.Equals(arguments[0], "uninstall", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                arguments[1],
                "--recover-orphaned-bundle",
                StringComparison.OrdinalIgnoreCase))
        {
            invocation = new(
                SetupCommand.Uninstall,
                BundleManaged: false,
                RecoverOrphanedBundle: true);
            return true;
        }

        if (arguments.Count == 2
            && string.Equals(arguments[0], "uninstall", StringComparison.OrdinalIgnoreCase)
            && string.Equals(arguments[1], "--purge", StringComparison.OrdinalIgnoreCase))
        {
            invocation = new(SetupCommand.UninstallAndPurge, BundleManaged: false);
            return true;
        }

        invocation = default;
        return false;
    }

    public static bool IsInteractiveUninstall(IReadOnlyList<string> arguments) =>
        arguments.Count == 2
        && string.Equals(arguments[0], "uninstall", StringComparison.OrdinalIgnoreCase)
        && string.Equals(arguments[1], "--interactive", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseBundleManaged(
        IReadOnlyList<string> arguments,
        string command,
        bool allowPurge,
        out bool purge,
        out string? progressPipeName,
        out string? bundleOwnerId)
    {
        purge = false;
        progressPipeName = null;
        bundleOwnerId = null;
        if (arguments.Count < 2
            || !string.Equals(arguments[0], command, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                arguments[1],
                "--bundle-managed",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int index = 2;
        while (index < arguments.Count)
        {
            string option = arguments[index];
            if (allowPurge
                && !purge
                && string.Equals(option, "--purge", StringComparison.OrdinalIgnoreCase))
            {
                purge = true;
                index++;
                continue;
            }

            if (progressPipeName is null
                && string.Equals(option, "--progress-pipe", StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Count
                && !string.IsNullOrWhiteSpace(arguments[index + 1])
                && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                progressPipeName = arguments[index + 1];
                index += 2;
                continue;
            }

            if (bundleOwnerId is null
                && string.Equals(option, "--bundle-owner-id", StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Count
                && Guid.TryParseExact(arguments[index + 1], "D", out Guid parsedBundleOwnerId))
            {
                bundleOwnerId = parsedBundleOwnerId.ToString("D");
                index += 2;
                continue;
            }

            return false;
        }

        return bundleOwnerId is not null;
    }
}
