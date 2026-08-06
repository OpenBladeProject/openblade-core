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
    string? ProgressPipeName = null);

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
                out string? installProgressPipe))
        {
            invocation = new(
                SetupCommand.Install,
                BundleManaged: true,
                ProgressPipeName: installProgressPipe);
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
                out string? uninstallProgressPipe))
        {
            invocation = new(
                purge ? SetupCommand.UninstallAndPurge : SetupCommand.Uninstall,
                BundleManaged: true,
                ProgressPipeName: uninstallProgressPipe);
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
        out string? progressPipeName)
    {
        purge = false;
        progressPipeName = null;
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
        if (allowPurge
            && index < arguments.Count
            && string.Equals(arguments[index], "--purge", StringComparison.OrdinalIgnoreCase))
        {
            purge = true;
            index++;
        }

        if (index < arguments.Count
            && string.Equals(
                arguments[index],
                "--progress-pipe",
                StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= arguments.Count
                || string.IsNullOrWhiteSpace(arguments[index + 1]))
            {
                return false;
            }

            progressPipeName = arguments[index + 1];
            index += 2;
        }

        return index == arguments.Count;
    }
}
