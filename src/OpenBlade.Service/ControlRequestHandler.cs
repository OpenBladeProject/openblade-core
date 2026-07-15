using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ControlRequestHandler(IServiceControlRuntime runtime)
{
    public async Task<ServiceResponse> HandleAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Failure(Guid.Empty, "IPC request is missing.");
        }

        if (request.Version != OpenBladeIpc.ProtocolVersion)
        {
            return Failure(request.RequestId, "Unsupported IPC protocol version.");
        }

        if (request.RequestId == Guid.Empty)
        {
            return Failure(Guid.Empty, "IPC request ID is missing.");
        }

        return request switch
        {
            GetStatusRequest or SubscribeStatusRequest => Status(request.RequestId),
            UpdateProfilesRequest update => Result(
                request.RequestId,
                await runtime.UpdateProfilesAsync(update.Profiles, cancellationToken)),
            SetPerformanceOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetPerformanceOverrideAsync(set.Value, cancellationToken)),
            SetFanOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetFanOverrideAsync(set.Value, cancellationToken)),
            SetLightingOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetLightingOverrideAsync(set.Value, cancellationToken)),
            SetBatteryPolicyRequest set => Result(
                request.RequestId,
                await runtime.SetBatteryPolicyAsync(set.Value, cancellationToken)),
            SetFullChargeOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetFullChargeOverrideAsync(set.Enabled, cancellationToken)),
            _ => Failure(request.RequestId, "IPC request operation is unsupported."),
        };
    }

    private ServiceResponse Status(Guid requestId) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: true,
        Error: null,
        Status: runtime.GetStatus(),
        Result: null);

    private static ServiceResponse Result(Guid requestId, ControlOperationResult result) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: true,
        Error: null,
        Status: null,
        Result: result);

    private static ServiceResponse Failure(Guid requestId, string error) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: false,
        Error: error,
        Status: null,
        Result: null);
}
