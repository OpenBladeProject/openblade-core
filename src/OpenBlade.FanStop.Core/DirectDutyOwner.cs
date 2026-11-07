namespace OpenBlade.FanStop;

internal sealed record DirectDutyOwner(Guid OperationId, string DeviceIdentity, string FirmwareEvidence, Guid ControllerSession);
