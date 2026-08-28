using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal enum GpuClockOffsetManagementState
{
    Unknown = 0,
    Managed = 1,
    ResetConfirmed = 2,
    Relinquished = 3,
    RelinquishedDefaultConfirmed = 4,
}

internal interface IGpuClockOffsetManagementDevice
{
    GpuClockOffsetManagementState GpuClockOffsetManagementState { get; }

    Task SetPerformanceAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        GpuClockOffsetSettings? gpuClockOffsets,
        bool retainGpuClockOffsetManagement,
        CancellationToken cancellationToken);

    Task SetGpuClockOffsetsAsync(
        GpuClockOffsetSettings value,
        bool retainManagement,
        CancellationToken cancellationToken);

    Task RelinquishGpuClockOffsetManagementAsync(CancellationToken cancellationToken);

    Task<bool> InvalidateGpuClockOffsetConfirmationAsync(CancellationToken cancellationToken);
}

public interface ISingleAttemptThermalDevice
{
    Task SetPerformanceOnceAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        GpuClockOffsetSettings? gpuClockOffsets,
        bool retainGpuClockOffsetManagement,
        CancellationToken cancellationToken);
}

internal interface IGpuClockOffsetConfirmationInvalidator
{
    Task<bool> InvalidateGpuClockOffsetConfirmationAsync(CancellationToken cancellationToken);
}
