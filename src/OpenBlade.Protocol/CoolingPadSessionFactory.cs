namespace OpenBlade.Protocol;

public enum CoolingPadSessionFault
{
    AdmissionClosed,
    Disconnected,
    ControllerConflict,
    Rejected,
    SubmissionUnknown,
}

public sealed record CoolingPadSessionOpenResult(
    ICoolingPadDemandSession? Session,
    CoolingPadSessionFault? Fault);

/// <summary>Production admission remains closed. Developer opening is explicit.</summary>
public sealed class CoolingPadSessionFactory
{
    private readonly Func<IValidationActorFactory>? createActors;
    private readonly TimeProvider clock;
    private readonly PendingDisposalRegistry pending = new();
    private int active;
    private readonly bool developerCounter;
    private int counterOpened;
    internal CoolingPadDeveloperCounterPolicy? CounterEvidence { get; private set; }
    internal bool DeveloperCounterResourcesReleased => developerCounter
        && Volatile.Read(ref active) == 0 && pending.Count == 0;

    public CoolingPadSessionFactory() : this(null, TimeProvider.System) { }

    internal CoolingPadSessionFactory(Func<IValidationActorFactory>? createActors, TimeProvider clock, bool developerCounter = false)
    {
        this.developerCounter = developerCounter;
        this.createActors = createActors;
        this.clock = clock;
    }

    public static CoolingPadSessionFactory CreateDeveloperWindows() =>
        new(() => CreateDeveloperActors(), TimeProvider.System);

    internal static CoolingPadSessionFactory CreateDeveloperCounterWindows() =>
        new(() => CreateDeveloperActors(true), TimeProvider.System, developerCounter: true);

    public async Task<CoolingPadSessionOpenResult> OpenAsync(CancellationToken cancellationToken)
    {
        if (createActors is null)
        {
            return new(null, CoolingPadSessionFault.AdmissionClosed);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return new(null, CoolingPadSessionFault.Rejected);
        }
        if (pending.Count != 0 || Interlocked.CompareExchange(ref active, 1, 0) != 0)
        {
            return new(null, CoolingPadSessionFault.Rejected);
        }

        if (developerCounter && Interlocked.Exchange(ref counterOpened, 1) != 0)
        { Interlocked.Exchange(ref active, 0); return new(null, CoolingPadSessionFault.Rejected); }
        var context = new ValidationRunContext();
        if (developerCounter) { CounterEvidence = context.CreateCoolingPadDeveloperCounterPolicy(); }
        // Isolate synchronous enumeration/opening too. A late open is disposed, never returned.
        Task<HidSessionResult<ValidationActor>> opening = Task.Run(async () =>
            await createActors().OpenAsync(context).ConfigureAwait(false), CancellationToken.None);
        try
        {
            var opened = await opening.WaitAsync(TimeSpan.FromSeconds(3), clock, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (opened.Failure is not null || opened.Value is null)
            {
                if (opened.Value is not null)
                {
                    await BoundedValidationActorDisposal.DisposeAsync(new CoolingPadDemandSession.DeferredDisposal(opened.Value), clock,
                        TimeSpan.FromSeconds(5), pending).ConfigureAwait(false);
                }
                if (opened.Value is null || opened.Value.Session.Disposition == RazerCommandSessionDisposition.FullyDisposed)
                {
                    Interlocked.Exchange(ref active, 0);
                }
                return new(null, CoolingPadDemandSession.MapFault(opened.Failure));
            }
            return new(new CoolingPadDemandSession(opened.Value, context, clock, pending,
                () => Interlocked.Exchange(ref active, 0), CounterEvidence), null);
        }
        catch
        {
            pending.Retain(Task.Run(async () =>
            {
                bool release = false;
                try
                {
                    var late = await opening.ConfigureAwait(false);
                    if (late.Value is not null)
                    {
                        await BoundedValidationActorDisposal.DisposeAsync(new CoolingPadDemandSession.DeferredDisposal(late.Value), clock,
                            TimeSpan.FromSeconds(5), pending).ConfigureAwait(false);
                    }
                    release = late.Value is null || late.Value.Session.Disposition == RazerCommandSessionDisposition.FullyDisposed;
                }
                catch { release = opening.IsFaulted || opening.IsCanceled; }
                finally
                {
                    if (release) { Interlocked.Exchange(ref active, 0); }
                }
            }));
            return new(null, CoolingPadSessionFault.Disconnected);
        }
    }

    internal static readonly HidHandleIdentity ExactPadIdentity =
        new(0x1532, 0x0F43, 0x000C, 0x0001, 91, 0x0200);

    private static IValidationActorFactory CreateDeveloperActors(bool counter = false)
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var host = new WindowsHostIdentitySource(registry, new RuntimeWindowsPlatformEvidenceSource(registry),
            WindowsHostIdentitySource.Rz090528Identity);
        var padNative = new PadInterfaceNative(native);
        var guard = new PadAccessGuard(new RunningProcessSource(), new WindowsServiceSource(),
            () => HostAndPadStillExact(host, padNative), requireOpenBladeServiceAbsent: counter, developerProcessAllowed: counter ? CounterProcessAlone : null);
        var opener = new WindowsRazerHidSessionOpener(
            host, guard, padNative, ControllerExclusivityPolicy.Strict, ExactPadIdentity);
        return new ValidationActorFactory(opener, new OverlappedFeatureOperation(native), guard,
            TimeProvider.System, TimeSpan.FromSeconds(3), ControllerExclusivityPolicy.Strict,
            TimeSpan.FromMilliseconds(30), featureReportId: 0, developerCounterIdentity: counter ? ExactPadIdentity : null);
    }

    internal static bool HostAndPadStillExact(IHostIdentitySource host, IHidCandidateNative native)
    {
        HostIdentityReadResult identity = host.ReadExactIdentity();
        if (identity.Evidence is null || identity.Failure is not null) { return false; }
        int exactCount = 0;
        foreach (string path in native.EnumerateDevicePaths())
        {
            using OpenedCandidateHandle? candidate = native.TryOpen(path,
                WindowsRazerHidEvidenceEvaluator.MetadataOnlyOpenParameters).Candidate;
            if (candidate is null) { return false; }
            if (native.ReadIdentity(candidate).MatchesExpected(ExactPadIdentity)) { exactCount++; }
        }
        return exactCount == 1;
    }

    internal sealed class PadInterfaceNative(IHidCandidateNative native) : IHidCandidateNative
    {
        public IReadOnlyList<string> EnumerateDevicePaths() => native.EnumerateDevicePaths()
            .Where(path => path.Contains("vid_1532&pid_0f43&", StringComparison.OrdinalIgnoreCase)
                && IsControlInterface(path)).ToArray();
        internal static bool IsControlInterface(string path) =>
            path.Contains("&mi_00#", StringComparison.OrdinalIgnoreCase)
            || path.Contains("&mi_00&", StringComparison.OrdinalIgnoreCase);
        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters) => native.TryOpen(path, parameters);
        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle) => native.ReadIdentity(handle);
        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle) => native.BindSelected(handle);
    }

    private static bool CounterProcessAlone() => CounterProcessAdmissionAllowed(
        Environment.ProcessPath, Environment.ProcessId, () =>
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("OpenBlade.Capture");
            try { return processes.Select(process => process.Id).ToArray(); }
            finally { foreach (var process in processes) { process.Dispose(); } }
        });

    internal static bool CounterProcessAdmissionAllowed(string? executable, int currentProcessId,
        Func<IReadOnlyList<int>> captureProcessIds)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)
                || !Path.GetFileName(executable).Equals("OpenBlade.Capture.exe", StringComparison.OrdinalIgnoreCase))
            { return false; }
            var ids = captureProcessIds();
            return ids.Count == 1 && ids[0] == currentProcessId;
        }
        catch { return false; }
    }

    internal sealed class PadAccessGuard(IRunningProcessSource processes, IWindowsServiceSource services,
        Func<bool>? identityAllowed = null, bool requireOpenBladeServiceAbsent = false, Func<bool>? developerProcessAllowed = null)
        : IHardwareAccessGuard
    {
        private readonly WindowsHardwareAccessGuard controllers = new(processes, services);
        public HardwareAccessDecision Check()
        {
            HardwareAccessDecision decision;
            try
            {
                if (requireOpenBladeServiceAbsent && developerProcessAllowed?.Invoke() != true)
                { return new([], "Developer Capture process admission is unavailable or conflicted."); }
                decision = controllers.Check();
                var captures = processes.GetProcessNames().Where(name =>
                    name.Equals("USBPcapCMD", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("SynapseAppEngine", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("RazerAppEngine", StringComparison.OrdinalIgnoreCase)
                    || requireOpenBladeServiceAbsent && name.Equals("OpenBlade.Service", StringComparison.OrdinalIgnoreCase));
                var vendorServices = services.GetServices().Where(service =>
                    (requireOpenBladeServiceAbsent ? service.State != WindowsServiceState.Stopped
                        : service.State is WindowsServiceState.Running or WindowsServiceState.StartPending or WindowsServiceState.ContinuePending)
                    && (service.ServiceName.Contains("Razer", StringComparison.OrdinalIgnoreCase)
                        || service.DisplayName.Contains("Razer", StringComparison.OrdinalIgnoreCase)
                        || requireOpenBladeServiceAbsent && service.ServiceName.Equals("OpenBlade", StringComparison.OrdinalIgnoreCase)))
                    .Select(service => service.ServiceName);
                var combined = new HardwareAccessDecision([.. decision.Conflicts, .. captures, .. vendorServices], decision.DetectionFailure);
                if (combined.Safe && identityAllowed?.Invoke() == false)
                {
                    return new([], "Exact host or pad metadata is unavailable or changed.");
                }
                return combined;
            }
            catch
            {
                return new([], "Pad controller detection failed.");
            }
        }
    }
}
