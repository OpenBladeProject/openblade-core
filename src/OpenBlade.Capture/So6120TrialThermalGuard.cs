using OpenBlade.Service;

namespace OpenBlade.Capture;

internal sealed record So6120TrialThermalObservation(long StartedTimestamp, long CompletedTimestamp,
    int? CpuCelsius, int? GpuCelsius, bool GpuInactive, bool Admitted, string? Failure);

// These conservative experiment limits are not a production thermal policy.
// The existing CPU source is the exact Razer ACPI thermal zone, not a claim
// that it measures every CPU die hotspot. An inactive GPU needs no NVML read.
internal sealed class So6120TrialThermalGuard(IFanTemperatureSource source, TimeProvider clock,
    Action<So6120TrialThermalObservation> record)
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(2);
    private So6120TrialThermalObservation? latest;

    internal bool Refresh()
    {
        long started = clock.GetTimestamp();
        latest = null;
        FanTemperatureReadings reading = default;
        string? failure = null;
        try
        {
            // The caller invokes this only outside an active mailbox transfer.
            reading = source.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error) { failure = error.GetType().Name; }
        long completed = clock.GetTimestamp();
        bool admitted = failure is null && reading.CpuCelsius is >= 10 and <= 55
            && reading.GpuInactive && completed >= started
            && clock.GetElapsedTime(started, completed) <= MaximumAge;
        var observation = new So6120TrialThermalObservation(started, completed,
            reading.CpuCelsius, reading.GpuCelsius, reading.GpuInactive, admitted, failure);
        record(observation);
        latest = observation;
        return CachedAdmission();
    }

    internal bool CachedAdmission()
    {
        long now = clock.GetTimestamp();
        return latest is { Admitted: true } value && now >= value.CompletedTimestamp
            && clock.GetElapsedTime(value.StartedTimestamp, now) <= MaximumAge;
    }
}
