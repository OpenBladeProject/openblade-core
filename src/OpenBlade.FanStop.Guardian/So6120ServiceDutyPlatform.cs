using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using OpenBlade.Contracts;
using OpenBlade.FanStop;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.FanStop.Guardian;

internal sealed class So6120ServiceNativeReleaseException(string message, Exception? inner = null) : IOException(message, inner)
{
}

// The product owner has its own admission policy. It never manufactures an
// attended developer challenge or inherits SKU/BIOS restrictions from a trial.
internal sealed class So6120ServiceDutyPlatform : ISo6120SessionEvidenceSource, ISo6120SessionAdmissionPolicy, IDisposable
{
    private static readonly List<So6120ServiceDutyPlatform> UnresolvedOwners = [];
    private readonly So6120JournalIdentity identity;
    private readonly int servicePid;
    private readonly long serviceStartUtcTicks;
    private readonly Func<bool> ownershipStillDelegated;
    private readonly TimeProvider clock;
    private readonly Semaphore run;
    private readonly FanStopSettings settings;
    private So6120ServiceSafetySampler? safetySampler;
    private FileStream? driverPin;
    private Version? bootReportedEcVersion;
    private bool disposed;
    private bool powerUnresolved;
    private long? powerTimestamp;
    private PowerSource observedPowerSource = PowerSource.ExternalUnknown;

    private So6120ServiceDutyPlatform(So6120JournalIdentity identity, int servicePid, long serviceStartUtcTicks,
        Func<bool> ownershipStillDelegated, TimeProvider clock, Semaphore run,
        FanStopSettings settings)
    {
        this.identity = identity;
        this.servicePid = servicePid;
        this.serviceStartUtcTicks = serviceStartUtcTicks;
        this.ownershipStillDelegated = ownershipStillDelegated;
        this.clock = clock;
        this.run = run;
        this.settings = settings;
    }

    internal So6120SessionBinding Binding { get; private set; } = null!;
    internal So6120InpOutBytePort Port { get; private set; } = null!;
    internal ISo6120SessionEvidenceSource EvidenceSource => this;
    internal ISo6120SessionAdmissionPolicy AdmissionPolicy => this;
    internal So6120NativeCloseEvidence CloseEvidence { get; private set; } = null!;
    internal PowerSource ObservedPowerSource => observedPowerSource;
    internal bool PowerObservationDue => !PowerObservationFresh();
    internal bool NativeOperationUnresolved => powerUnresolved;
    internal FanStopEndReason EndReason => safetySampler?.Current.EndReason ?? FanStopEndReason.None;
    internal FanStopThermalDecision? TerminalThermalDecision =>
        safetySampler?.Current is { EndReason: FanStopEndReason.ThermalThreshold } sample ? sample.ThermalDecision : null;
    internal string? EntryAdmissionRefusal { get; private set; }

    internal void BeginOwnedStop() => safetySampler!.BeginOwnedStopAsync().GetAwaiter().GetResult();

    internal void StopMonitoring() => safetySampler?.Dispose();

    internal static SafeWindowsFanTemperatureSource CreateTemperatureSource() => new();

    internal static So6120ServiceDutyPlatform Acquire(So6120JournalIdentity identity, int servicePid,
        long serviceStartUtcTicks, Func<bool> ownershipStillDelegated, IFanTemperatureSource temperatureSource,
        FanStopSettings settings, CancellationToken cancellationToken)
    {
        RequireSystemSession();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(ownershipStillDelegated);
        ArgumentNullException.ThrowIfNull(temperatureSource);
        if (!FanStopSettings.IsWellFormed(settings)) { throw new InvalidDataException("Invalid immutable Stop policy."); }
        if (identity.Owner.OperationId == Guid.Empty || servicePid <= 0 || serviceStartUtcTicks <= 0
            || identity.CapturedProfile != PerformancePreset.Balanced || identity.RequestedDuty1 != 0 || identity.RequestedDuty2 != 0)
        { throw new InvalidOperationException("The service owner requires a fresh explicit Balanced Stop identity."); }
        var run = new Semaphore(1, 1, @"Global\OpenBlade.SO6120.SupervisedDirectDuty");
        bool reserved = false;
        So6120ServiceDutyPlatform? owner = null;
        So6120OwnedInpOutHandle? handle = null;
        Task<bool>? preparation = null;
        try
        {
            if (!run.WaitOne(0)) { throw new InvalidOperationException("An SO6120 owner is already active."); }
            reserved = true;
            VerifyDeveloperAttempt();
            owner = new(identity, servicePid, serviceStartUtcTicks, ownershipStillDelegated, TimeProvider.System, run, settings);
            So6120SupervisedMetadata metadata = owner.ReadMetadata();
            // This inert challenge token satisfies shared identity binding only.
            // The service policy below does not interpret it as consent.
            var scope = new So6120ChallengeScope("", "", new Version(0, 0), "", "", "");
            var challenge = new So6120CallerChallengeContext(metadata.Stamp, Guid.Empty, "", scope, scope,
                So6120AcceptedResiduals.None, So6120AcceptedResiduals.None);
            var handoff = new So6120OpenBladeHandoff(metadata.Stamp, So6120HandoffOrigin.ServiceOwned, null);
            owner.Binding = new(identity, metadata.Stamp.ConsoleSession, challenge, handoff);
            owner.RequireAdmission(metadata);
            owner.bootReportedEcVersion = metadata.ReportedEcVersion;
            // Expected session/thermal refusals must resolve before any native
            // device or HID command is opened, so they cannot fabricate an
            // uncertain Stop transaction on an idle or already-hot laptop.
            if (!owner.ActiveSession(metadata))
            {
                throw new InvalidOperationException(So6120ServiceInitialAdmissionDiagnostics.Refusal("ActiveConsoleSession",
                    $"consoleSession={metadata.Session.ConsoleSession}; boundSession={owner.Binding.ConsoleSession}"));
            }
            if (metadata.OpenBladeStatus != ServiceControllerStatus.Running)
            {
                throw new InvalidOperationException(So6120ServiceInitialAdmissionDiagnostics.Refusal("ServiceRunning",
                    $"serviceStatus={metadata.OpenBladeStatus}"));
            }
            if (metadata.OpenBladeWriterPids.Count != 1 || metadata.OpenBladeWriterPids[0] != servicePid)
            {
                throw new InvalidOperationException(So6120ServiceInitialAdmissionDiagnostics.Refusal("ServiceWriterOwnership",
                    $"writerCount={metadata.OpenBladeWriterPids.Count}; originalWriterPresent={metadata.OpenBladeWriterPids.Contains(servicePid)}"));
            }
            // A guardian has a new sensor worker on every activation. Prepare it
            // before the first one-shot admission read, while no fan handle or
            // command exists. Cold readiness never substitutes for a fresh sample.
            bool prepared;
            try
            {
                preparation = Task.Run(async () => await temperatureSource.PrepareAsync(cancellationToken).ConfigureAwait(false));
                prepared = preparation
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(So6120ServiceInitialAdmissionDiagnostics.Refusal("CpuReaderPreparation",
                    $"outcome={error.GetType().Name}; canceled={cancellationToken.IsCancellationRequested}"), error);
            }
            if (!prepared)
            {
                throw new InvalidOperationException(So6120ServiceInitialAdmissionDiagnostics.Refusal("CpuReaderPreparation", "outcome=NotReady"));
            }
            // The OS worker owns all subsequent temperature acquisition. It is
            // live before opening the native fan interface, including entry's
            // serialized adapter queries and command observations.
            owner.safetySampler = new(temperatureSource, settings, owner.clock, owner.ObserveVolatileAdmission);
            So6120ServiceSafetySnapshot initial = owner.safetySampler.WaitForInitialAsync(cancellationToken).GetAwaiter().GetResult();
            if (!initial.Admitted)
            {
                throw new InvalidOperationException(owner.DescribeSafetyRefusal(initial));
            }
            cancellationToken.ThrowIfCancellationRequested();
            owner.driverPin = So6120NativeAcquisition.PinDriverFile(metadata.Driver);
            owner.RequireAdmission(owner.ReadMetadata());
            handle = So6120NativeAcquisition.OpenDevice();
            owner.CloseEvidence = handle.Evidence;
            owner.Port = So6120InpOutBytePort.AttachWindowsHandle(handle);
            handle = null;
            return owner;
        }
        catch
        {
            owner?.StopMonitoring();
            if (owner?.safetySampler is null)
            { _ = So6120ServiceSafetySampler.DisposeSourceAfterAsync(temperatureSource, preparation ?? Task.CompletedTask); }
            if (handle is not null)
            {
                var close = handle.Evidence;
                try { handle.Dispose(); }
                catch (Exception exception)
                {
                    if (owner is not null) { RetainUnresolved(owner); }
                    throw new So6120ServiceNativeReleaseException("Native acquisition release threw; owner exclusion is retained.", exception);
                }
                if (close.State != So6120NativeCloseState.Closed)
                {
                    // Retain the pin and exclusion on ambiguous native release.
                    // The durable outer reservation remains unresolved as well.
                    if (owner is not null) { RetainUnresolved(owner); }
                    throw new So6120ServiceNativeReleaseException("Native acquisition release is unresolved; owner exclusion is retained.");
                }
            }
            owner?.driverPin?.Dispose();
            if (reserved) { run.Release(); }
            run.Dispose();
            throw;
        }
    }

    public So6120SessionEvidence Capture()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        So6120SupervisedMetadata metadata = ReadMetadata();
        // A run stamp retains the original logical session binding. Actual
        // current console/power remain independently present in Session so Auto
        // can clean up after logoff without pretending the user is still active.
        metadata = metadata with { Stamp = metadata.Stamp with { ConsoleSession = Binding.ConsoleSession } };
        RequireAdmission(metadata);
        return new(identity, metadata, Binding.Challenge, Binding.Handoff);
    }

    private So6120SupervisedMetadata ReadMetadata()
    {
        RequireSystemSession();
        var read = driverPin is not null && bootReportedEcVersion is not null
            ? So6120WindowsSupervisedMetadata.CaptureForService(identity.Owner.OperationId, clock, driverPin, bootReportedEcVersion)
            : So6120WindowsSupervisedMetadata.CaptureForService(identity.Owner.OperationId, clock);
        return read.Metadata ?? throw new InvalidOperationException($"Service owner metadata unavailable ({read.FailedStage}).");
    }

    public So6120AdmissionAssessment Assess(TimeProvider time, So6120SupervisedMetadata metadata,
        So6120CallerChallengeContext challenge, So6120OpenBladeHandoff handoff)
    {
        if (powerUnresolved) { return So6120AdmissionAssessment.SessionOrPowerMismatch; }
        long now = time.GetTimestamp();
        if (!ReferenceEquals(time, clock) || !ReferenceEquals(metadata.Stamp.Clock, clock)
            || metadata.Stamp.RunId != identity.Owner.OperationId || metadata.StartedTimestamp < 0
            || metadata.StartedTimestamp > metadata.Stamp.Timestamp || metadata.Stamp.Timestamp > now
            || clock.GetElapsedTime(metadata.StartedTimestamp, now) > TimeSpan.FromSeconds(5))
        { return So6120AdmissionAssessment.StaleMetadata; }
        if (metadata.ProcessArchitecture != Architecture.X64 || metadata.Host.Failure is not null
            || metadata.Host.Evidence is not
            {
                Manufacturer: So6120SupervisedAdmission.Manufacturer,
                ProductName: So6120SupervisedAdmission.Product
            })
        { return So6120AdmissionAssessment.HostMismatch; }
        if (metadata.ReportedEcVersion != So6120SupervisedAdmission.EcVersion)
        { return So6120AdmissionAssessment.EcVersionMismatch; }
        if (metadata.HidCandidates.Count == 0 || metadata.HidCandidates.Any(candidate =>
                !So6120SupervisedAdmission.IsTargetHidPath(candidate.Path) || candidate.Identity is not { } hid
                || !TargetBladeDevice.Matches(hid.VendorId, hid.ProductId))
            || metadata.HidCandidates.Count(candidate => candidate.Identity is { } hid
                && hid.UsagePage == TargetBladeDevice.UsagePage && hid.Usage == TargetBladeDevice.Usage
                && TargetBladeDevice.CanCarryProtocolReport(hid.FeatureReportByteLength)) != 1)
        { return So6120AdmissionAssessment.HidIdentityUnavailable; }
        if (metadata.Session.ProcessSession != 0 || metadata.Session.Interactive)
        { return So6120AdmissionAssessment.SessionOrPowerMismatch; }
        if (!So6120SupervisedAdmission.MatchesDriver(metadata.Driver))
        { return So6120AdmissionAssessment.DriverMismatch; }
        if (metadata.DetectedControllers.Count != 0)
        { return So6120AdmissionAssessment.ControllerDetected; }
        if (handoff.Origin != So6120HandoffOrigin.ServiceOwned || !ownershipStillDelegated()
            || metadata.OpenBladeWriterPids.Any(pid => pid != servicePid) || !OriginalServiceOrExited(metadata))
        { return So6120AdmissionAssessment.OpenBladeHandoffMissingOrMismatched; }
        return So6120AdmissionAssessment.InputsMatched;
    }

    private bool OriginalServiceOrExited(So6120SupervisedMetadata metadata)
    {
        // No service is allowed during loss cleanup. A recycled PID or a new
        // service cannot acquire this run's authority from its durable files.
        if (metadata.OpenBladeWriterPids.Count == 0)
        { return metadata.OpenBladeStatus is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending; }
        try
        {
            using var service = Process.GetProcessById(servicePid);
            return metadata.OpenBladeWriterPids.Count == 1 && !service.HasExited && service.SessionId == 0
                && service.StartTime.ToUniversalTime().Ticks == serviceStartUtcTicks;
        }
        catch { return false; }
    }

    private void RequireAdmission(So6120SupervisedMetadata metadata)
    {
        if (Assess(clock, metadata, Binding.Challenge, Binding.Handoff) != So6120AdmissionAssessment.InputsMatched)
        { throw new InvalidOperationException("Fresh service-owner admission refused."); }
    }

    internal bool RefreshEntryAdmission()
    {
        // Diagnostics belong to this invocation. An early prerequisite failure
        // must not publish the successful Acquire sample as its own evidence.
        EntryAdmissionRefusal = null;
        string stage = "EntryVolatile";
        try
        {
            So6120ServiceVolatileAdmission admission = ObserveVolatileAdmission();
            if (!admission.Admitted) { return RefuseEntry(admission.Detail ?? "Entry volatile admission failed."); }
            stage = "EntrySession";
            So6120SessionMetadata current = So6120WindowsSupervisedMetadata.ReadSessionMetadata();
            // The already-completed typed power read can be reused briefly.
            // Fresh OS session/AC-line checks remain necessary before every A1;
            // changed/unknown AC state invalidates this cached entry scope.
            if (!PowerObservationFresh() || current.AcLineStatus != 1)
            {
                stage = "EntryPowerRead";
                ReadPowerSource();
            }
            if (observedPowerSource != PowerSource.UsbC)
            { return RefuseEntry($"stage=EntryPower; observedPower={observedPowerSource}; acLine={current.AcLineStatus}; thermalSample=not-attempted"); }
            stage = "EntryThermal";
            So6120ServiceSafetySnapshot sample = safetySampler!.ReadFresh();
            if (sample.Admitted) { return true; }
            return RefuseEntry($"stage=EntryThermal; {DescribeSafetyRefusal(sample)}");
        }
        catch (Exception error)
        {
            return RefuseEntry($"stage={stage}; outcome={error.GetType().Name}; hresult=0x{error.HResult:X8}; thermalSample={(stage == "EntryThermal" ? "unavailable" : "not-attempted")}");
        }
    }

    private bool RefuseEntry(string detail)
    {
        EntryAdmissionRefusal = detail[..Math.Min(detail.Length, 640)];
        return false;
    }

    internal bool CachedEntryAdmission()
    {
        try
        {
            So6120SessionMetadata current = So6120WindowsSupervisedMetadata.ReadSessionMetadata();
            So6120ServiceSafetySnapshot sample = safetySampler!.ReadFresh();
            bool admitted = ActiveSession(current) && !current.Interactive && current.ProcessSession == 0 && current.AcLineStatus == 1
                && !powerUnresolved && PowerObservationFresh() && sample.Admitted && observedPowerSource == PowerSource.UsbC;
            if (!admitted)
            {
                // Describe this failed cached gate using existing observations;
                // do not refresh samples or add hardware calls during publication.
                return RefuseEntry(FormattableString.Invariant($"stage=EntryCached; activeSession={ActiveSession(current)}; interactive={current.Interactive}; processSession={current.ProcessSession}; acLine={current.AcLineStatus}; observedPower={observedPowerSource}; powerUnresolved={powerUnresolved}; powerAgeMs={(powerTimestamp is long observed ? clock.GetElapsedTime(observed).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "unavailable")}; safetyAdmitted={sample.Admitted}; safetyAgeMs={clock.GetElapsedTime(sample.AdmissionStarted).TotalMilliseconds:F1}; {sample.ThermalDecision?.Diagnostic}; {sample.Detail}"));
            }
            return true;
        }
        catch (Exception error)
        { return RefuseEntry($"stage=EntryCached; outcome={error.GetType().Name}; hresult=0x{error.HResult:X8}"); }
    }

    internal bool ContinueStopAdmission()
    {
        // Reading this proof never calls a sensor or waits for session.sync.
        // The independent OS sampler continues during every native operation.
        So6120ServiceSafetySnapshot sample = safetySampler!.ReadFresh();
        if (!sample.Admitted || !sample.Owned) { return false; }
        if (powerUnresolved || observedPowerSource is not (PowerSource.UsbC or PowerSource.Battery))
        { safetySampler.End(FanStopEndReason.PowerChanged, "Typed power observation no longer admits owned Stop."); return false; }
        return true;
    }

    private So6120ServiceVolatileAdmission ObserveVolatileAdmission()
    {
        var current = So6120WindowsSupervisedMetadata.CaptureServiceVolatile(clock);
        bool sessionAdmitted = ActiveSession(current.Session) && !current.Session.Interactive && current.Session.ProcessSession == 0;
        TimeSpan? metadataAge = null;
        bool? originalServiceMatched = null;
        bool admitted = current.StartedTimestamp <= current.CompletedTimestamp
            && (metadataAge = clock.GetElapsedTime(current.StartedTimestamp)).Value >= TimeSpan.Zero
            && metadataAge.Value <= So6120ServiceSafetySampler.MaximumSampleAge
            && sessionAdmitted
            && current.Session.AcLineStatus is 0 or 1 && current.DetectedControllers.Count == 0
            && current.OpenBladeStatus == ServiceControllerStatus.Running
            && current.OpenBladeWriterPids.Count == 1 && current.OpenBladeWriterPids[0] == servicePid
            && (originalServiceMatched = OriginalServiceStillRunning()).Value;
        string? detail = admitted ? null : FormattableString.Invariant($"stage=Volatile; chronological={current.StartedTimestamp <= current.CompletedTimestamp}; metadataAgeMs={metadataAge?.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "not-observed"}; sessionAdmitted={sessionAdmitted}; acLine={current.Session.AcLineStatus}; controllerCount={current.DetectedControllers.Count}; serviceStatus={current.OpenBladeStatus}; writerCount={current.OpenBladeWriterPids.Count}; originalWriterPresent={current.OpenBladeWriterPids.Contains(servicePid)}; serviceIdentity={originalServiceMatched?.ToString() ?? "not-checked"}; thermalSample=not-attempted");
        return new(admitted, sessionAdmitted ? FanStopEndReason.SafetyCondition : FanStopEndReason.SessionChanged, detail);
    }

    private bool OriginalServiceStillRunning()
    {
        using var service = Process.GetProcessById(servicePid);
        return !service.HasExited && service.SessionId == 0 && service.StartTime.ToUniversalTime().Ticks == serviceStartUtcTicks;
    }

    private bool PowerObservationFresh() => powerTimestamp is long observed
        && clock.GetElapsedTime(observed) is var age && age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(2);

    private bool ActiveSession(So6120SupervisedMetadata metadata) => ActiveSession(metadata.Session);
    private bool ActiveSession(So6120SessionMetadata session) => session.ConsoleSession is not (0 or uint.MaxValue)
        && session.ConsoleSession == Binding.ConsoleSession;

    private string DescribeSafetyRefusal(So6120ServiceSafetySnapshot sample) =>
        $"{sample.Detail} " + So6120ServiceInitialAdmissionDiagnostics.ThermalRefusal(settings,
            sample.Reading, sample.SampleAge, sample.Canceled, sample.ThermalDecision);

    // Called on the owner's serialized worker outside mailbox A0/A1 work. A
    // unresolved native read throws and fences. A complete typed reply with an
    // unknown value returns ExternalUnknown and permits same-owner Auto cleanup.
    internal PowerSource ReadPowerSource()
    {
        So6120ServicePowerReadResult result = So6120ServicePowerReadPolicy.ReadAdmittedAsync(
            () => RequireAdmission(ReadMetadata()), ReadAdmittedPowerSourceAsync).GetAwaiter().GetResult();
        if (result.QueryNotAttempted)
        {
            powerTimestamp = null;
            observedPowerSource = PowerSource.ExternalUnknown;
            safetySampler?.End(FanStopEndReason.SafetyCondition, result.AdmissionRefusal!);
        }
        return result.Source;
    }

    private async Task<PowerSource> ReadAdmittedPowerSourceAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            long started = clock.GetTimestamp();
            observedPowerSource = await So6120ServicePowerReadPolicy.ReadAsync(ReadAdapterPowerStateAsync, deadline.Token)
                .ConfigureAwait(false);
            powerTimestamp = started;
            return observedPowerSource;
        }
        catch
        {
            powerUnresolved = true;
            observedPowerSource = PowerSource.ExternalUnknown;
            throw;
        }
    }

    private static async Task<AdapterPowerState> ReadAdapterPowerStateAsync(CancellationToken token)
    {
        var opened = await ProductionBatteryFactory.CreateWindowsReadOnly().OpenAsync(token).ConfigureAwait(false);
        if (opened.Value is not { } port) { throw new IOException("Independent adapter power open failed."); }
        await using (port.ConfigureAwait(false))
        {
            if (opened.Failure is not null) { throw new IOException("Independent adapter power open is unresolved."); }
            return await port.QueryAdapterPowerStateAsync(token).ConfigureAwait(false);
        }
    }

    private static void RequireSystemSession()
    {
        FanStopGuardianTransport.RequireSystem();
        using var process = Process.GetCurrentProcess();
        if (process.SessionId != 0) { throw new UnauthorizedAccessException("The guardian requires service session zero."); }
    }

    private static void VerifyDeveloperAttempt()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade", "DeveloperValidation", "SO6120");
        try { _ = File.GetAttributes(directory); }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }
        FanStopGuardianTransport.VerifyProtectedPath(directory, directory: true);
        string marker = Path.Combine(directory, "attempt.json");
        try { _ = File.GetAttributes(marker); }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }
        FanStopGuardianTransport.VerifyProtectedPath(marker, directory: false);
        throw new IOException("A developer attempt marker blocks product Stop. Use "
            + "build/Archive-So6120CompletedDeveloperAttempt.ps1 -EvidenceDirectory <reviewed-evidence> "
            + "for read-only verification; only its explicit -Archive option from an administrator console "
            + "can preserve and archive the matching independently completed attempt. No production bypass is admitted.");
    }

    public void Dispose()
    {
        if (disposed) { return; }
        StopMonitoring();
        try { Port.Dispose(); }
        catch (Exception exception)
        {
            RetainUnresolved(this);
            throw new So6120ServiceNativeReleaseException("Native release threw; owner exclusion is retained.", exception);
        }
        if (CloseEvidence.State != So6120NativeCloseState.Closed)
        {
            RetainUnresolved(this);
            throw new So6120ServiceNativeReleaseException("Native release is unresolved; driver pin and exclusion remain retained.");
        }
        disposed = true;
        driverPin?.Dispose();
        run.Release();
        run.Dispose();
    }

    private static void RetainUnresolved(So6120ServiceDutyPlatform owner)
    {
        lock (UnresolvedOwners)
        {
            if (!UnresolvedOwners.Contains(owner)) { UnresolvedOwners.Add(owner); }
        }
    }

}
