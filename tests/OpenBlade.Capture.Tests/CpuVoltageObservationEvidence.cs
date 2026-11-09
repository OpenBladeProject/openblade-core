using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

internal static class CpuVoltageObservationEvidence
{
    internal static object Create(string version, DateTimeOffset timestamp, ControlOperationResult response) => new
    {
        mode = "ObserveOnly",
        outcome = "ObservationRecorded",
        phase = "Observation",
        expectedServiceVersion = version,
        applyAttempted = false,
        restoreAttempted = false,
        originalVerified = false,
        baseline = (object?)null,
        forward = (object?)null,
        final = (object?)null,
        responseReceivedAtUtc = timestamp,
        controlResponse = response,
    };
}
