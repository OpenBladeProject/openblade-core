using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// Developer-only raw candidate. Laptop mode semantics do not apply to this pad.
internal sealed class CoolingPadControlStateQueryCommand(
    Func<CancellationToken, Task<HidSessionResult<GetterCandidateProbeSnapshot>>> run,
    TextWriter output,
    Func<Action, IDisposable>? registerCancellation = null)
    : GetterCandidateProbeCommand(run, output, registerCancellation,
        new DeviceIdentity("RZ09-0528 cooling pad", 0x1532, 0x0F43, "2.02"))
{
    internal const string Name = "query-rz09-0528-cooling-pad-control-state";
    protected override bool DeviceBiosIsCaptureProvenance => true;

    protected override CandidateMetadata Metadata { get; } = new(
        Name, "cooling-pad-raw-control-state", "00", "84", "0000", 2);
}

internal sealed class CoolingPadControlStateQueryRunner(
    Func<ValidationRunContext> createContext,
    IValidationActorFactory actorFactory,
    TimeProvider timeProvider,
    TimeSpan exchangeTimeout,
    TimeSpan cleanupTimeout,
    PendingDisposalRegistry? pendingDisposals = null)
    : GetterCandidateProbeRunner(createContext, actorFactory, timeProvider,
        exchangeTimeout, cleanupTimeout, pendingDisposals)
{
    protected override RazerCommandSpec CreateFixedCommand() => new(
        0x00, 0x84, [0x00, 0x00], 2,
        payload => payload.Length == 2 && payload.Span[1] == 0
            && payload.Span[0] is 0x00 or 0x03);
}

internal static class CoolingPadControlStateQueryFactory
{
    internal static readonly HidHandleIdentity ExactIdentity =
        new(0x1532, 0x0F43, 0x000C, 0x0001, 91, 0x0200);
    // The descriptor captured in openblade-captures PR33 has SHA-256
    // C86119101B32403BB8C8EFBDEB67654B348C7C28E298B711D9FE1DB8C129DAFF:
    // one unnumbered 90-byte constant feature report. Windows adds prefix zero.
    internal static IGetterCandidateProbeRunner CreateWindows() =>
        GetterCandidateProbeFactory.CreateCoolingPadControlStateWindows();

    // Laptop product exclusions in patched Synapse do not exclude the pad.
    // A names-only source deliberately retains AppEngine as a competing controller.
    internal sealed class PadControllerProcessSource(IRunningProcessSource? source = null) : IRunningProcessSource
    {
        private readonly IRunningProcessSource source = source ?? new RunningProcessSource();
        public IReadOnlyList<string> GetProcessNames() => source.GetProcessNames();
    }

    internal sealed class PadAccessGuard : IHardwareAccessGuard
    {
        private readonly IRunningProcessSource processes;
        private readonly IWindowsServiceSource services;
        private readonly WindowsHardwareAccessGuard controllers;

        internal PadAccessGuard(IRunningProcessSource processes, IWindowsServiceSource services)
        {
            this.processes = processes;
            this.services = services;
            controllers = new(new PadControllerProcessSource(processes), services);
        }

        public HardwareAccessDecision Check()
        {
            var decision = controllers.Check();
            try
            {
                var captures = processes.GetProcessNames().Where(name =>
                    name.Equals("USBPcapCMD", StringComparison.OrdinalIgnoreCase));
                var vendorServices = services.GetServices().Where(service =>
                    service.State is WindowsServiceState.Running or WindowsServiceState.StartPending or WindowsServiceState.ContinuePending
                    && (service.ServiceName.Contains("Razer", StringComparison.OrdinalIgnoreCase)
                        || service.DisplayName.Contains("Razer", StringComparison.OrdinalIgnoreCase)))
                    .Select(service => service.ServiceName);
                return new([.. decision.Conflicts, .. captures, .. vendorServices], decision.DetectionFailure);
            }
            catch
            {
                return new(decision.Conflicts, "Capture-process or vendor-service detection failed.");
            }
        }
    }
}
