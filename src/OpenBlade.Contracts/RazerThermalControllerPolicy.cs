namespace OpenBlade.Contracts;

/// <summary>
/// Classifies already-detected controllers for HyperBoost thermal exclusivity.
/// Lighting-only exceptions remain visible to lighting and diagnostic policies.
/// </summary>
public static class RazerThermalControllerPolicy
{
    private static readonly HashSet<string> LightingOnlyControllers = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Razer Chroma SDK Diagnostic Service",
        "Razer Chroma SDK Server",
        "Razer Chroma SDK Service",
        "Razer Chroma Stream Server",
    };

    public static bool IsBlocking(string controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        // Only exact reviewed lighting aliases are exempt. Unknown names and
        // shared Synapse/updater processes retain the conservative default.
        return !LightingOnlyControllers.Contains(controller);
    }
}
