namespace OpenSynapse.Setup;

public enum SetupCommand
{
    Install,
    Uninstall,
    UninstallAndPurge,
}

public static class SetupCommandParser
{
    public static bool TryParse(IReadOnlyList<string> arguments, out SetupCommand command)
    {
        if (arguments.Count == 1
            && string.Equals(arguments[0], "install", StringComparison.OrdinalIgnoreCase))
        {
            command = SetupCommand.Install;
            return true;
        }

        if (arguments.Count == 1
            && string.Equals(arguments[0], "uninstall", StringComparison.OrdinalIgnoreCase))
        {
            command = SetupCommand.Uninstall;
            return true;
        }

        if (arguments.Count == 2
            && string.Equals(arguments[0], "uninstall", StringComparison.OrdinalIgnoreCase)
            && string.Equals(arguments[1], "--purge", StringComparison.OrdinalIgnoreCase))
        {
            command = SetupCommand.UninstallAndPurge;
            return true;
        }

        command = default;
        return false;
    }
}
