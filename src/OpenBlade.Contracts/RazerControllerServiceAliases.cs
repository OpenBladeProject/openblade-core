namespace OpenBlade.Contracts;

public static class RazerControllerServiceAliases
{
    private static readonly HashSet<string> KnownAliases = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Razer Chroma SDK Diagnostic Service",
        "Razer Chroma SDK Server",
        "Razer Chroma SDK Service",
        "Razer Chroma Stream Server",
        "Razer Gaming Services Updater",
        "RazerSynapseService",
        "Razer Synapse Service",
    };

    private static readonly HashSet<string> InternalNameOnlyAliases = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "RGS",
    };

    public static bool Contains(string alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        return KnownAliases.Contains(alias) || InternalNameOnlyAliases.Contains(alias);
    }

    public static bool MatchesService(string serviceName, string displayName)
    {
        ArgumentNullException.ThrowIfNull(serviceName);
        ArgumentNullException.ThrowIfNull(displayName);
        return Contains(serviceName) || KnownAliases.Contains(displayName);
    }
}
