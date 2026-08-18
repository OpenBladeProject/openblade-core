using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal enum CoolingPadFanGetterKind
{
    ConfiguredSpeed,
    CurrentSpeed,
    FirmwareCoolingMode,
    SynapseCoolingMode,
}

internal static class CoolingPadFanGetterProbeTarget
{
    internal const byte ThermalClassId = 0x01;
    internal const byte ThermalId = 0x05;
    internal const ControllerExclusivityPolicy ExclusivityPolicy =
        ControllerExclusivityPolicy.Strict;

    internal static ExpectedHostIdentity HostIdentity { get; } = new(
        "Razer",
        "Blade 16 - RZ09-0581",
        "RZ09-05819EN4",
        Array.AsReadOnly(new[] { "4.00" }));

    internal static HidHandleIdentity PadHidIdentity { get; } = new(
        VendorId: 0x1532,
        ProductId: 0x0F43,
        UsagePage: 0x000C,
        Usage: 0x0001,
        FeatureReportByteLength: RazerReport.ReportLength + 1,
        VersionNumber: WindowsCoolingPadDiscoverySource.VersionNumber);
}

internal static class CoolingPadFanGetterProbeFactory
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    internal static IGetterCandidateProbeRunner CreateWindows(
        CoolingPadFanGetterKind kind) =>
        new CoolingPadFanGetterProbeRunner(
            static () => new ValidationRunContext(),
            BatteryValidationFactory
                .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory(),
            TimeProvider.System,
            ExchangeTimeout,
            CleanupTimeout,
            kind);
}

internal static class CoolingPadFanGetterCommands
{
    internal static RazerCommandSpec Create(CoolingPadFanGetterKind kind) => new(
        0x0D,
        GetCommandId(kind),
        CreateArguments(kind),
        GetExpectedPayloadLength(kind),
        payload => ValidatePayload(kind, payload.Span));

    internal static RazerCommandSpec CreateOpaqueObservation() =>
        Create(CoolingPadFanGetterKind.ConfiguredSpeed);

    internal static RazerCommandSpec CreateConfigurationBank0() =>
        Create(CoolingPadFanGetterKind.FirmwareCoolingMode);

    internal static RazerCommandSpec CreateConfigurationBank1() =>
        Create(CoolingPadFanGetterKind.SynapseCoolingMode);

    private static byte GetCommandId(CoolingPadFanGetterKind kind) => kind switch
    {
        CoolingPadFanGetterKind.ConfiguredSpeed => 0x81,
        CoolingPadFanGetterKind.CurrentSpeed => 0x88,
        CoolingPadFanGetterKind.FirmwareCoolingMode
            or CoolingPadFanGetterKind.SynapseCoolingMode => 0x90,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[] CreateArguments(CoolingPadFanGetterKind kind) => kind switch
    {
        CoolingPadFanGetterKind.ConfiguredSpeed
            or CoolingPadFanGetterKind.CurrentSpeed =>
        [
            CoolingPadFanGetterProbeTarget.ThermalClassId,
            CoolingPadFanGetterProbeTarget.ThermalId,
            0x00,
        ],
        CoolingPadFanGetterKind.FirmwareCoolingMode => [0x00, 0x00, 0x00],
        CoolingPadFanGetterKind.SynapseCoolingMode => [0x01, 0x00, 0x00],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static int GetExpectedPayloadLength(CoolingPadFanGetterKind kind) => kind switch
    {
        CoolingPadFanGetterKind.ConfiguredSpeed
            or CoolingPadFanGetterKind.CurrentSpeed
            or CoolingPadFanGetterKind.SynapseCoolingMode => 3,
        CoolingPadFanGetterKind.FirmwareCoolingMode => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool ValidatePayload(
        CoolingPadFanGetterKind kind,
        ReadOnlySpan<byte> payload) => kind switch
        {
            CoolingPadFanGetterKind.ConfiguredSpeed
                or CoolingPadFanGetterKind.CurrentSpeed => payload is
            [
                CoolingPadFanGetterProbeTarget.ThermalClassId,
                CoolingPadFanGetterProbeTarget.ThermalId,
                _,
            ],
            CoolingPadFanGetterKind.FirmwareCoolingMode =>
                payload is [0x00, <= 0x06],
            CoolingPadFanGetterKind.SynapseCoolingMode =>
                ValidateConfigurationBank1(payload),
            _ => false,
        };

    private static bool ValidateConfigurationBank1(ReadOnlySpan<byte> payload) =>
        payload is [0x01, <= 0x06, _];
}

internal sealed class CoolingPadFanGetterProbeRunner : GetterCandidateProbeRunner
{
    private readonly CoolingPadFanGetterKind kind;

    internal CoolingPadFanGetterProbeRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupTimeout,
        CoolingPadFanGetterKind kind,
        PendingDisposalRegistry? pendingDisposals = null)
        : base(
            createContext,
            actorFactory,
            timeProvider,
            exchangeTimeout,
            cleanupTimeout,
            pendingDisposals)
    {
        this.kind = kind;
    }

    protected override RazerCommandSpec CreateFixedCommand() =>
        CoolingPadFanGetterCommands.Create(kind);
}
