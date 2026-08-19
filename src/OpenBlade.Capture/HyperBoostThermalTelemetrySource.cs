using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal sealed class HyperBoostThermalTelemetrySource(
    IFanTemperatureSource source) : IHyperBoostThermalTelemetrySource
{
    private readonly IFanTemperatureSource source = source
        ?? throw new ArgumentNullException(nameof(source));

    public async ValueTask<HyperBoostTemperatureObservation> ReadAsync(
        CancellationToken cancellationToken)
    {
        FanTemperatureReadings readings = await source.ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        return new(
            readings.CpuCelsius,
            readings.GpuCelsius,
            readings.GpuInactive);
    }
}
