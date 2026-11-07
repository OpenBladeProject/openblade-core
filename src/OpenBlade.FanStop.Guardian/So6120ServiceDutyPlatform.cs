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
    private readonly IFanTemperatureSource temperatureSource;
    private readonly TimeProvider clock;
    private readonly Semaphore run;
    private readonly So6120ServiceContinuationMonitor continuationMonitor;
    private readonly FanStopSettings settings;
    private readonly FanStopThermalPolicy thermalPolicy;
    private FileStream? driverPin;
    private Version? bootReportedEcVersion;
    private bool disposed;
    private bool powerUnresolved;
    private bool thermalSafe;
    private long thermalTimestamp;
    private bool ownedStopStarted;
    private long? powerTimestamp;
    private PowerSource observedPowerSource = PowerSource.ExternalUnknown;

    private So6120ServiceDutyPlatform(So6120JournalIdentity identity, int servicePid, long serviceStartUtcTicks,
        Func<bool> ownershipStillDelegated, IFanTemperatureSource temperatureSource, TimeProvider clock, Semaphore run,
        FanStopSettings settings)
    {
        this.identity = identity;
        this.servicePid = servicePid;
        this.serviceStartUtcTicks = serviceStartUtcTicks;
        this.ownershipStillDelegated = ownershipStillDelegated;
        this.temperatureSource = temperatureSource;
        this.clock = clock;
        this.run = run;
        this.settings = settings;
        thermalPolicy = new(settings, clock);
        continuationMonitor = new(() => ReadContinuationAdmission() && RefreshTemperature(continuation: true), clock);
    }

    internal So6120SessionBinding Binding { get; private set; } = null!;
    internal So6120InpOutBytePort Port { get; private set; } = null!;
    internal ISo6120SessionEvidenceSource EvidenceSource => this;
    internal ISo6120SessionAdmissionPolicy AdmissionPolicy => this;
    internal So6120NativeCloseEvidence CloseEvidence { get; private set; } = null!;
    internal PowerSource ObservedPowerSource => observedPowerSource;
    internal bool NativeOperationUnresolved => powerUnresolved;
    internal FanStopEndReason EndReason { get; private set; }

    internal void BeginOwnedStop() => ownedStopStarted = true;

    internal static SafeWindowsFanTemperatureSource CreateTemperatureSource() => new();

    internal static So6120ServiceDutyPlatform Acquire(So6120JournalIdentity identity, int servicePid,
        long serviceStartUtcTicks, Func<bool> ownershipStillDelegated, IFanTemperatureSource temperatureSource,
        FanStopSettings settings)
    {
        RequireSystemSession();
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
        try
        {
            if (!run.WaitOne(0)) { throw new InvalidOperationException("An SO6120 owner is already active."); }
            reserved = true;
            VerifyDeveloperAttempt();
            owner = new(identity, servicePid, serviceStartUtcTicks, ownershipStillDelegated, temperatureSource, TimeProvider.System, run, settings);
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
            if (!owner.ActiveSession(metadata) || metadata.OpenBladeStatus != ServiceControllerStatus.Running
                || metadata.OpenBladeWriterPids.Count != 1 || metadata.OpenBladeWriterPids[0] != servicePid
                || !owner.RefreshTemperature())
            { throw new InvalidOperationException("Initial command-free Stop admission refused."); }
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
            (temperatureSource as IDisposable)?.Dispose();
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
        try
        {
            if (!ReadContinuationAdmission())
            { return false; }
            So6120SessionMetadata current = So6120WindowsSupervisedMetadata.ReadSessionMetadata();
            // The already-completed typed power read can be reused briefly.
            // Fresh OS session/AC-line checks remain necessary before every A1;
            // changed/unknown AC state invalidates this cached entry scope.
            if (!PowerObservationFresh() || current.AcLineStatus != 1) { ReadPowerSource(); }
            return observedPowerSource == PowerSource.UsbC && RefreshTemperature();
        }
        catch { thermalSafe = false; return false; }
    }

    internal bool CachedEntryAdmission()
    {
        try
        {
            So6120SessionMetadata current = So6120WindowsSupervisedMetadata.ReadSessionMetadata();
            return ActiveSession(current) && !current.Interactive && current.ProcessSession == 0 && current.AcLineStatus == 1
                && !powerUnresolved && PowerObservationFresh() && thermalSafe && observedPowerSource == PowerSource.UsbC
                && clock.GetElapsedTime(thermalTimestamp) <= TimeSpan.FromSeconds(2);
        }
        catch { return false; }
    }

    internal bool ContinueStopAdmission()
    {
        // The parent owns typed-power polling on the same serialized worker.
        // Continuous admission has no HID/mailbox commands or durable I/O.
        if (powerUnresolved || observedPowerSource is not (PowerSource.UsbC or PowerSource.Battery))
        { EndReason = FanStopEndReason.PowerChanged; return false; }
        if (EndReason != FanStopEndReason.None) { return false; }
        if (continuationMonitor.Observe()) { return true; }
        if (!continuationMonitor.SampleFresh || EndReason == FanStopEndReason.None)
        { EndReason = FanStopEndReason.SafetyCondition; }
        return false;
    }

    private bool ReadContinuationAdmission()
    {
        var current = So6120WindowsSupervisedMetadata.CaptureServiceVolatile(clock);
        bool sessionAdmitted = ActiveSession(current.Session) && !current.Session.Interactive && current.Session.ProcessSession == 0;
        if (ownedStopStarted && !sessionAdmitted) { EndReason = FanStopEndReason.SessionChanged; }
        return current.StartedTimestamp <= current.CompletedTimestamp
            && clock.GetElapsedTime(current.StartedTimestamp) <= So6120ServiceContinuationMonitor.MaximumSampleAge
            && sessionAdmitted
            && current.Session.AcLineStatus is 0 or 1 && current.DetectedControllers.Count == 0
            && current.OpenBladeStatus == ServiceControllerStatus.Running
            && current.OpenBladeWriterPids.Count == 1 && current.OpenBladeWriterPids[0] == servicePid
            && OriginalServiceStillRunning();
    }

    private bool OriginalServiceStillRunning()
    {
        using var service = Process.GetProcessById(servicePid);
        return !service.HasExited && service.SessionId == 0 && service.StartTime.ToUniversalTime().Ticks == serviceStartUtcTicks;
    }

    private bool PowerObservationFresh() => powerTimestamp is long observed
        && clock.GetElapsedTime(observed) <= TimeSpan.FromSeconds(2);

    private bool ActiveSession(So6120SupervisedMetadata metadata) => ActiveSession(metadata.Session);
    private bool ActiveSession(So6120SessionMetadata session) => session.ConsoleSession is not (0 or uint.MaxValue)
        && session.ConsoleSession == Binding.ConsoleSession;

    private bool RefreshTemperature(bool continuation = false)
    {
        thermalSafe = false;
        thermalTimestamp = clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        FanTemperatureReadings reading = (settings.EndMode == FanStopEndMode.Custom
                ? temperatureSource.ReadForFanStopAsync(deadline.Token) : temperatureSource.ReadAsync(deadline.Token)).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        TimeSpan age = clock.GetElapsedTime(thermalTimestamp);
        if (continuation)
        {
            bool admitted = thermalPolicy.Observe(reading.CpuCelsius, reading.GpuCelsius,
                reading.GpuInactive, reading.GpuPoweredDown, age, deadline.IsCancellationRequested);
            if (!admitted) { EndReason = thermalPolicy.EndReason; }
            return admitted;
        }
        // Full entry remains strict before every zero command. Once owned,
        // command freshness checks may observe Custom heat without bypassing or
        // resetting the independent continuation timers. Auto cleanup does not
        // call these Stop-entry hooks.
        thermalSafe = ownedStopStarted
            ? thermalPolicy.AdmitsCommand(reading.CpuCelsius, reading.GpuCelsius, reading.GpuInactive,
                reading.GpuPoweredDown, age, deadline.IsCancellationRequested)
            : thermalPolicy.AdmitsEntry(reading.CpuCelsius, reading.GpuCelsius, reading.GpuInactive,
                reading.GpuPoweredDown, age, deadline.IsCancellationRequested);
        if (ownedStopStarted && !thermalSafe) { EndReason = thermalPolicy.EndReason; }
        return thermalSafe;
    }

    // Called on the owner's serialized worker outside mailbox A0/A1 work. A
    // unresolved native read throws and fences. A complete typed reply with an
    // unknown value returns ExternalUnknown and permits same-owner Auto cleanup.
    internal PowerSource ReadPowerSource()
    {
        RequireAdmission(ReadMetadata());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            long started = clock.GetTimestamp();
            observedPowerSource = So6120ServicePowerReadPolicy.ReadAsync(ReadAdapterPowerStateAsync, deadline.Token)
                .GetAwaiter().GetResult();
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
        (temperatureSource as IDisposable)?.Dispose();
    }

    private static void RetainUnresolved(So6120ServiceDutyPlatform owner)
    {
        lock (UnresolvedOwners)
        {
            if (!UnresolvedOwners.Contains(owner)) { UnresolvedOwners.Add(owner); }
        }
    }

}
