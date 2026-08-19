# Cooling-pad demand API

The cooling-pad demand API lets a process in the active Windows session submit
a temporary cooling floor. OpenBlade stores each request as a lease, combines
active leases by taking the highest demand, and removes a lease when its
lifetime expires.

This API currently reports `IntentOnly`. It updates OpenBlade's cooling plan,
but it does not write the plan to the cooling pad. `AppliedDemand` remains null
and `Ownership` remains `Unowned` until the pad write and handback paths pass
their separate validation gates.

## Requirements

- The user must enable **Allow local cooling-demand API** in the cooling-pad
  card.
- The caller must run in the active interactive Windows session. The service
  rechecks the caller's SID and session when it commits or renews a lease.
- `Demand.BasisPoints` must be between `0` and `10000`.
- `TtlMilliseconds` must be between `2000` and `30000`.
- A client may hold or renew a lease by reusing its non-empty `LeaseId`.

The service clears leases on session replacement, low power, and shutdown.
Clients should renew well before expiry and should release their lease when
they no longer need it. Expiry remains the fallback if a client exits without
releasing.

## Typed C# example

Reference the same-version `OpenBlade.Contracts` assembly used by the installed
service. Open one named-pipe connection per request because the service returns
one response for each connection.

```csharp
using System.IO.Pipes;
using OpenBlade.Contracts;

using var stop = new CancellationTokenSource();
Guid leaseId = Guid.NewGuid();

ServiceResponse accepted = await SendAsync(
    new SetCoolingDemandLeaseRequest(
        OpenBladeIpc.ProtocolVersion,
        Guid.NewGuid(),
        leaseId,
        new CoolingDemandLevel(6_000),
        TtlMilliseconds: 5_000),
    stop.Token);

if (!accepted.Success)
{
    throw new InvalidOperationException(accepted.Error);
}

// Renew with the same lease ID before the five-second lifetime expires.
await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
_ = await SendAsync(
    new SetCoolingDemandLeaseRequest(
        OpenBladeIpc.ProtocolVersion,
        Guid.NewGuid(),
        leaseId,
        new CoolingDemandLevel(6_000),
        TtlMilliseconds: 5_000),
    stop.Token);

_ = await SendAsync(
    new ReleaseCoolingDemandLeaseRequest(
        OpenBladeIpc.ProtocolVersion,
        Guid.NewGuid(),
        leaseId),
    stop.Token);

static async Task<ServiceResponse> SendAsync(
    ServiceRequest request,
    CancellationToken cancellationToken)
{
    await using var pipe = new NamedPipeClientStream(
        ".",
        OpenBladeIpc.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous);
    await pipe.ConnectAsync(cancellationToken);
    await IpcMessageCodec.WriteRequestAsync(pipe, request, cancellationToken);

    ServiceServerFrame frame = await IpcMessageCodec.ReadServerFrameAsync(
        pipe,
        cancellationToken);
    if (frame is not ServiceResponse response
        || response.RequestId != request.RequestId)
    {
        throw new InvalidDataException("The service returned an unexpected response.");
    }

    return response;
}
```

`ControlOperationStatus.Stored` means the lease was accepted into the planning
model. It does not mean the cooling pad changed speed. Check
`ServiceStatusSnapshot.CoolingPad` for `ComputedDemand`, `AppliedDemand`,
`Ownership`, and `Capabilities.CoolingDemandApiAccess` if the distinction
matters to the caller.

The API exposes normalized demand only. It does not accept Razer command IDs,
raw report bytes, RPM claims, or ownership assertions from clients.
