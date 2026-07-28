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
    bool RecoverOrphanedBundle = false);

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

        if (IsBundleManaged(arguments, "install"))
        {
            invocation = new(SetupCommand.Install, BundleManaged: true);
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

        if (IsBundleManaged(arguments, "uninstall"))
        {
            invocation = new(SetupCommand.Uninstall, BundleManaged: true);
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

    private static bool IsBundleManaged(IReadOnlyList<string> arguments, string command) =>
        arguments.Count == 2
        && string.Equals(arguments[0], command, StringComparison.OrdinalIgnoreCase)
        && string.Equals(arguments[1], "--bundle-managed", StringComparison.OrdinalIgnoreCase);
}
