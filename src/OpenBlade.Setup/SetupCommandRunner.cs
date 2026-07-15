namespace OpenBlade.Setup;

public interface ISetupPlatform
{
    string CurrentUserSid { get; }
    Task PrepareServiceForInstallAsync(CancellationToken cancellationToken);
    Task CopyPayloadAsync(CancellationToken cancellationToken);
    Task WriteAuthorizedUserSidAsync(string sid, CancellationToken cancellationToken);
    Task InstallServiceAsync(CancellationToken cancellationToken);
    Task VerifyServiceHealthAsync(CancellationToken cancellationToken);
    Task InstallStartupShortcutAsync(CancellationToken cancellationToken);
    Task RemoveStartupShortcutAsync(CancellationToken cancellationToken);
    Task RemoveServiceAsync(CancellationToken cancellationToken);
    Task StopTrayAsync(CancellationToken cancellationToken);
    Task RemoveBinariesAsync(CancellationToken cancellationToken);
    Task RemoveDataAsync(CancellationToken cancellationToken);
}

public sealed class SetupCommandRunner(ISetupPlatform platform)
{
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!SetupCommandParser.TryParse(arguments, out SetupCommand command))
        {
            Console.Error.WriteLine("Usage: OpenBlade.Setup.exe install | OpenBlade.Setup.exe uninstall [--purge]");
            return 2;
        }

        if (command == SetupCommand.Install)
        {
            await platform.RemoveStartupShortcutAsync(cancellationToken);
            await platform.PrepareServiceForInstallAsync(cancellationToken);
            await platform.StopTrayAsync(cancellationToken);
            await platform.CopyPayloadAsync(cancellationToken);
            await platform.WriteAuthorizedUserSidAsync(platform.CurrentUserSid, cancellationToken);
            await platform.InstallServiceAsync(cancellationToken);
            await platform.VerifyServiceHealthAsync(cancellationToken);
            await platform.InstallStartupShortcutAsync(cancellationToken);
            return 0;
        }

        await platform.RemoveStartupShortcutAsync(cancellationToken);
        await platform.RemoveServiceAsync(cancellationToken);
        await platform.StopTrayAsync(cancellationToken);
        await platform.RemoveBinariesAsync(cancellationToken);
        if (command == SetupCommand.UninstallAndPurge)
        {
            await platform.RemoveDataAsync(cancellationToken);
        }

        return 0;
    }
}
