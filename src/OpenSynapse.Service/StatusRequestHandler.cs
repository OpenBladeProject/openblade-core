using OpenSynapse.Contracts;

namespace OpenSynapse.Service;

public sealed class StatusRequestHandler(IConflictDetector conflictDetector)
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

        IReadOnlyList<string> conflicts = conflictDetector.GetRunningConflicts();
        ServiceStatusSnapshot status = new(
            DeviceVerified: false,
            WritesBlocked: true,
            Conflicts: conflicts,
            Message: conflicts.Count == 0
                ? "PID 02E0 command fixtures have not been admitted."
                : "Razer controller conflict detected.");

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
