using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal interface IThermalExternalDriftValidationDevice
{
    Task InjectPerformanceDriftOnceAsync(
        PerformancePreset candidate,
        CancellationToken cancellationToken);
}
