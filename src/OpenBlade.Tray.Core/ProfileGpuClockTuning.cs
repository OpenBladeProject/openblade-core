using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal static class ProfileGpuClockTuning
{
    public static GpuClockTuningSettings? Resolve(PowerProfile profile) =>
        profile.GpuClockTuning
        ?? (profile.Performance == PerformancePreset.Custom
            && profile.CustomPerformance?.GpuClockOffsets is { } legacyOffsets
                ? new GpuClockTuningSettings(Enabled: true, legacyOffsets)
                : null);
}
