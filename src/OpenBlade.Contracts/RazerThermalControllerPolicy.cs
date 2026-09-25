namespace OpenBlade.Contracts;

/// <summary>
/// Classifies already-detected controllers for HyperBoost thermal exclusivity.
/// Non-thermal exceptions remain visible to other controller and diagnostic policies.
/// </summary>
public static class RazerThermalControllerPolicy
{
    private static readonly HashSet<string> NonThermalControllers = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Razer Chroma SDK Diagnostic Service",
        "Razer Chroma SDK Server",
        "Razer Chroma SDK Service",
        "Razer Chroma Stream Server",
        "razerwdl",
        "RGS",
        "Razer Gaming Services Updater",
    };

    public static bool IsBlocking(string controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        // Only exact lighting and gaming-services updater aliases are exempt.
        // Synapse and unknown controllers retain the conservative default.
        return !NonThermalControllers.Contains(controller);
    }

    public static IReadOnlyList<string> GetBlockingControllers(ServiceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.HyperBoostBlockingControllers
            ?? status.PotentialControllers.Where(IsBlocking).ToArray();
    }
}
