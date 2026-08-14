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
        "RGS",
        "Razer Gaming Services Updater",
        "RazerSynapseService",
        "Razer Synapse Service",
    };

    public static bool Contains(string alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        return KnownAliases.Contains(alias);
    }
}
