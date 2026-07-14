namespace OpenSynapse.Setup;

public interface ISetupPlatform
{
    string CurrentUserSid { get; }
    Task CopyPayloadAsync(CancellationToken cancellationToken);
    Task WriteAuthorizedUserSidAsync(string sid, CancellationToken cancellationToken);
    Task InstallServiceAsync(CancellationToken cancellationToken);
    Task InstallStartupShortcutAsync(CancellationToken cancellationToken);
    Task RemoveStartupShortcutAsync(CancellationToken cancellationToken);
    Task RemoveServiceAsync(CancellationToken cancellationToken);
    Task RemoveBinariesAsync(CancellationToken cancellationToken);
    Task RemoveDataAsync(CancellationToken cancellationToken);
}

public sealed class SetupCommandRunner(ISetupPlatform platform)
{
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 1 && arguments[0].Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            await platform.CopyPayloadAsync(cancellationToken);
            await platform.WriteAuthorizedUserSidAsync(platform.CurrentUserSid, cancellationToken);
            await platform.InstallServiceAsync(cancellationToken);
            await platform.InstallStartupShortcutAsync(cancellationToken);
            return 0;
        }

        bool purge = arguments.Count == 2
            && arguments[0].Equals("uninstall", StringComparison.OrdinalIgnoreCase)
            && arguments[1].Equals("--purge", StringComparison.OrdinalIgnoreCase);
        bool uninstall = arguments.Count == 1
            && arguments[0].Equals("uninstall", StringComparison.OrdinalIgnoreCase);
        if (uninstall || purge)
        {
            await platform.RemoveStartupShortcutAsync(cancellationToken);
            await platform.RemoveServiceAsync(cancellationToken);
            await platform.RemoveBinariesAsync(cancellationToken);
            if (purge)
            {
                await platform.RemoveDataAsync(cancellationToken);
            }

            return 0;
        }

        Console.Error.WriteLine("Usage: opensynapse setup install | uninstall [--purge]");
        return 2;
    }
}
