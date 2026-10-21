namespace OpenBlade.Contracts;

public enum ApplicationActivationMode
{
    Foreground,
    WhileRunningAfterActivation,
}

public sealed record ApplicationPowerProfileOverride
{
    public PerformancePreset? Performance { get; init; }

    public CustomPerformanceSettings? CustomPerformance { get; init; }

    public GpuClockTuningSettings? GpuClockTuning { get; init; }

    public FanMode? Fans { get; init; }

    public FanControlSettings? FanControl { get; init; }

    public LightingSettings? Lighting { get; init; }

    public DisplayProfileSettings? Display { get; init; }

    public bool? GamingModeEnabled { get; init; }

    public GamingModeKeyPolicy? GamingModeKeyPolicy { get; init; }

    public bool IsConfigured => Performance.HasValue
        || CustomPerformance is not null
        || GpuClockTuning is not null
        || Fans.HasValue
        || FanControl is not null
        || Lighting is not null
        || Display?.IsConfigured == true
        || GamingModeEnabled.HasValue
        || GamingModeKeyPolicy is not null;
}

public sealed record ApplicationProfileOverrideSet
{
    public ApplicationPowerProfileOverride? PluggedIn { get; init; }

    public ApplicationPowerProfileOverride? Battery { get; init; }

    public ApplicationPowerProfileOverride? UsbC { get; init; }

    public ApplicationPowerProfileOverride? ForPowerSource(PowerSource source) => source switch
    {
        PowerSource.PluggedIn => PluggedIn,
        PowerSource.Battery => Battery,
        PowerSource.UsbC => UsbC,
        _ => null,
    };

    public bool IsConfigured => PluggedIn?.IsConfigured == true
        || Battery?.IsConfigured == true
        || UsbC?.IsConfigured == true;
}

public sealed record ApplicationProfileDefinition(
    Guid Id,
    string Name,
    string ExecutablePath,
    ApplicationActivationMode ActivationMode,
    ApplicationProfileOverrideSet Overrides,
    bool Enabled = true)
{
    public const int MaximumNameLength = 80;
    public const int MaximumExecutablePathLength = 32767;
    public const int MaximumProfiles = 128;

    public static string NormalizeExecutablePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.Length > MaximumExecutablePathLength
            || trimmed.Any(char.IsControl)
            || !Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException(
                "An application profile requires one absolute executable path.",
                nameof(value));
        }

        string normalized = Path.GetFullPath(trimmed)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "An application profile path must identify an executable.",
                nameof(value));
        }

        return normalized;
    }
}
