using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal readonly record struct CoolingPadFanControlEvidenceGates(
    bool ExactIdentityAndTransportReviewed,
    bool HostControl2200PairPhysicallyValidated,
    bool OpenBladeHandbackValidated,
    bool FullTargetRangeValidated,
    bool SmartCurveCadenceValidated,
    bool DisconnectReconnectValidated,
    bool LowPowerLifecycleValidated,
    bool CrashRecoveryValidated,
    bool HyperBoostInterlockValidated,
    bool PhysicalButtonOwnershipPolicyValidated)
{
    internal bool WriteAdmitted =>
        ExactIdentityAndTransportReviewed
        && HostControl2200PairPhysicallyValidated
        && OpenBladeHandbackValidated
        && FullTargetRangeValidated
        && SmartCurveCadenceValidated
        && DisconnectReconnectValidated
        && LowPowerLifecycleValidated
        && CrashRecoveryValidated
        && HyperBoostInterlockValidated
        && PhysicalButtonOwnershipPolicyValidated;

    internal static CoolingPadFanControlEvidenceGates Current =>
        CoolingPadAccessoryAdmissionSource.ReadFanGates();
}

/// <summary>
/// Typed cooling-pad host-control commands kept behind the closed production
/// admission gate. Only the exact 2200-RPM acquisition pair has an OpenBlade
/// physical-positive validation; arbitrary targets, updates, and release are
/// software-complete but remain unvalidated.
/// </summary>
internal sealed class CoolingPadHostControlPlan
{
    internal const int TargetRpm = 2_200;
    internal const byte TargetRaw = TargetRpm / 50;

    private readonly IReadOnlyList<RazerCommandSpec> commands;

    private CoolingPadHostControlPlan(IReadOnlyList<RazerCommandSpec> commands)
    {
        this.commands = commands;
    }

    internal int Count => commands.Count;

    internal RazerCommandSpec GetCommand(int index) => commands[index];

    internal static CoolingPadHostControlPlan CreateValidated2200() => new(
        CreateAcquisitionCommands(new CoolingPadFanTarget(TargetRpm)));

    internal static CoolingPadHostControlPlan CreateAcquisition(
        CoolingPadFanTarget target) => new(CreateAcquisitionCommands(target));

    internal static RazerCommandSpec CreateUpdate(CoolingPadFanTarget target) =>
        CreateDemandCommand(target);

    internal static RazerCommandSpec CreateRelease() =>
        CreateEchoCommand(commandId: 0x10, [0x00, 0x06, 0x00]);

    private static IReadOnlyList<RazerCommandSpec> CreateAcquisitionCommands(
        CoolingPadFanTarget target) => Array.AsReadOnly(
        [
            CreateRelease(),
            CreateDemandCommand(target),
        ]);

    private static RazerCommandSpec CreateDemandCommand(
        CoolingPadFanTarget target) => CreateEchoCommand(
        commandId: 0x01,
        [0x01, 0x05, checked((byte)(target.Rpm / 50))]);

    private static RazerCommandSpec CreateEchoCommand(
        byte commandId,
        byte[] arguments) => new(
        classId: 0x0D,
        commandId,
        arguments,
        expectedPayloadLength: arguments.Length,
        payload => payload.Span.SequenceEqual(arguments));
}
