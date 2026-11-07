namespace OpenBlade.CpuTemperature;

internal enum CpuThermalChannelKind { IntelCore, IntelPackage, AmdDie, AmdControl, Other }

internal sealed record CpuThermalChannel(CpuThermalChannelKind Kind, float? Celsius,
    float? TjMax = null, float? Slope = null);

internal static class CpuTemperatureSelection
{
    // Package/die covers the processor; otherwise require every exposed core,
    // rather than reporting a cool subset after one sensor becomes unavailable.
    internal static int? Select(IReadOnlyList<CpuThermalChannel> channels)
    {
        CpuThermalChannel[] measured = channels.Where(channel => channel.Kind != CpuThermalChannelKind.Other).ToArray();
        if (measured.Length == 0) { return null; }
        foreach (CpuThermalChannel channel in measured)
        {
            if (channel.Kind is CpuThermalChannelKind.IntelCore or CpuThermalChannelKind.IntelPackage
                && (channel.TjMax is not (>= 60 and <= 125) || channel.Slope != 1)) { return null; }
            if (channel.Celsius is { } value && !Valid(value)) { return null; }
        }

        CpuThermalChannel[] packages = measured.Where(channel => channel.Kind == CpuThermalChannelKind.IntelPackage).ToArray();
        CpuThermalChannel[] cores = measured.Where(channel => channel.Kind == CpuThermalChannelKind.IntelCore).ToArray();
        CpuThermalChannel[] amd = measured.Where(channel => channel.Kind is CpuThermalChannelKind.AmdDie or CpuThermalChannelKind.AmdControl).ToArray();
        if (packages.Length > 1 || (packages.Length + cores.Length > 0 && amd.Length > 0)) { return null; }
        if (amd.Length > 0)
        {
            // Tctl can contain a model-specific control offset. Prefer the
            // library's corrected die value where it exposes both readings.
            CpuThermalChannel[] die = amd.Where(channel => channel.Kind == CpuThermalChannelKind.AmdDie).ToArray();
            CpuThermalChannel[] selected = die.Length > 0 ? die : amd;
            return selected.Length == 1 && selected[0].Celsius is { } value
                ? ToMilliCelsius(value) : null;
        }
        bool packageValid = packages.Length == 1 && packages[0].Celsius.HasValue;
        if (!packageValid && (cores.Length == 0 || cores.Any(channel => channel.Celsius is null))) { return null; }
        return ToMilliCelsius(measured.Where(channel => channel.Celsius.HasValue).Max(channel => channel.Celsius!.Value));
    }

    private static bool Valid(float value) => float.IsFinite(value) && value is >= 10 and <= 125;
    private static int ToMilliCelsius(float value) => checked((int)Math.Ceiling(value * 1000.0));
}
