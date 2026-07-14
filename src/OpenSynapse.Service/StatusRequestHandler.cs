using OpenSynapse.Contracts;
using OpenSynapse.Protocol;

namespace OpenSynapse.Service;

public sealed class StatusRequestHandler(IHardwareAccessGuard hardwareAccessGuard)
{
    public ServiceResponse Handle(ServiceRequest request)
    {
        if (request.Version != OpenSynapseIpc.ProtocolVersion)
        {
            return Failure(request, "Unsupported IPC protocol version.");
        }

        if (!string.Equals(request.Operation, "status", StringComparison.Ordinal))
        {
            return Failure(request, "Operation is unavailable until its device commands are validated.");
        }

        HardwareAccessDecision accessDecision = hardwareAccessGuard.Check();
        IReadOnlyList<string> conflicts = accessDecision.Conflicts;
        ServiceStatusSnapshot status = new(
            DeviceVerified: false,
            WritesBlocked: true,
            Conflicts: conflicts,
            Message: accessDecision.DetectionFailure is not null
                ? $"Controller conflict detection failed: {accessDecision.DetectionFailure}"
                : conflicts.Count == 0
                ? "PID 02E0 command fixtures have not been admitted."
                : $"Razer controller conflicts detected: {string.Join(", ", conflicts)}.");

        return new ServiceResponse(
            OpenSynapseIpc.ProtocolVersion,
            request.RequestId,
            Success: true,
            Error: null,
            Status: status);
    }

    private static ServiceResponse Failure(ServiceRequest request, string error) => new(
        OpenSynapseIpc.ProtocolVersion,
        request.RequestId,
        Success: false,
        Error: error,
        Status: null);
}
