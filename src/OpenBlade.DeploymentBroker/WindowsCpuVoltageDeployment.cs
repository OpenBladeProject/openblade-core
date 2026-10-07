using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

public interface IBrokerCpuVoltageDeployment
{
    IDisposable AcquireInstallLease();
    void Prepare(Guid requestId, BrokerEnrollment enrollment, OpenBladeReleaseManifest manifest);
    void Launch(Guid requestId, BrokerEnrollment enrollment, OpenBladeReleaseManifest manifest, IDisposable installLease);
    void AbandonVerifiedRollback(Guid requestId);
    bool TryReconcile();
    IDisposable AcquireClosedReconciliation(BrokerInFlightDeployment state, OpenBladeReleaseManifest manifest);
    void CompleteClosedReconciliation(Guid requestId);
}

internal sealed record CpuVoltageLaunchRecord(Guid RequestId, string Source, string Manifest,
    string ServiceVersion, string UserSid, string Phase, int? ProcessId = null,
    long? Created = null, int? WindowsSession = null, string? Image = null);

// One fixed package and one launch. This record survives broker restarts; missing process
// observations cannot erase a launch intent or authorize a replacement installation.
public sealed class WindowsCpuVoltageDeployment(BrokerPaths paths) : IBrokerCpuVoltageDeployment
{
    internal const string LaunchFileName = "cpu-voltage-launch.json";
    private CpuVoltageClosureLease? retainedClosure;
    private IDisposable? retainedProcess;
    private string StatePath => Path.Combine(paths.RootDirectory, LaunchFileName);

    public static IDisposable RequirePriorClosedAndAcquireLease(bool requireExistingClosedHost = false)
        => Acquire(BrokerPaths.CreateDefault(), requireExistingClosedHost);

    public IDisposable AcquireInstallLease()
    {
        IDisposable lease = Acquire(paths, false);
        try
        {
            if (File.Exists(StatePath))
            {
                CpuVoltageLaunchRecord previous = ReadRecord(paths);
                retainedProcess?.Dispose(); retainedProcess = null;
                retainedClosure?.Dispose(); retainedClosure = null;
                paths.DeleteRequestStagingDirectory(previous.RequestId);
                File.Delete(StatePath);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static IDisposable Acquire(BrokerPaths paths, bool requireExisting)
    {
        // A first enrollment may have no host directory. Only the broker creates it,
        // and Capture later grants its enrolled user read access without mutation.
        if (!Directory.Exists(CpuVoltageHostState.Root))
        {
            if (requireExisting) { throw new IOException("A closed CPU host enrollment is required."); }
            ProtectedBrokerStorage.EnsureDirectory(CpuVoltageHostState.Root);
        }
        CpuVoltageHostState.RequireProtected(CpuVoltageHostState.Root, true);
        string leasePath = Path.Combine(CpuVoltageHostState.Root, "host.lock");
        if (File.Exists(leasePath)) { CpuVoltageHostState.RequireProtected(leasePath, false); }
        FileStream lease = new(leasePath, requireExisting ? FileMode.Open : FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            CpuVoltageHostEnrollment? previous = CpuVoltageHostState.ReadActive();
            if (previous is null && requireExisting)
            { throw new IOException("The previous CPU host enrollment is missing."); }
            if (previous is not null) { CpuVoltageHostState.RequireClosedAndTerminal(previous); }
            if (File.Exists(Path.Combine(paths.RootDirectory, LaunchFileName))
                || Directory.Exists(Path.Combine(paths.RootDirectory, LaunchFileName)))
            {
                CpuVoltageLaunchRecord record = ReadRecord(paths);
                if (previous is null || !MatchesClosedHost(record, previous))
                { throw new IOException("A CPU host launch remains unresolved."); }
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static bool MatchesClosedHost(CpuVoltageLaunchRecord record, CpuVoltageHostEnrollment host)
        => record.RequestId != Guid.Empty && record.Phase is "Bound" or "Resumed"
            && record.ProcessId == host.HostPid && record.Created == host.HostCreated
            && record.WindowsSession == host.WindowsSession && record.Image == host.HostImage
            && record.Source == host.Source && string.Equals(record.Manifest, host.Manifest, StringComparison.OrdinalIgnoreCase)
            && record.ServiceVersion == host.ServiceVersion && record.UserSid == host.UserSid;

    public void Prepare(Guid requestId, BrokerEnrollment enrollment, OpenBladeReleaseManifest manifest)
    {
        if (!enrollment.CpuVoltageValidationEnabled || manifest.SchemaVersion != 2 || retainedClosure is not null)
        { throw new InvalidOperationException("CPU validation enrollment is unavailable."); }
        string closure = ClosurePath(requestId);
        retainedClosure = CpuVoltageClosureLease.Open(closure, manifest.SourceRevision,
            manifest.CpuVoltageManifestSha256!, manifest.CpuVoltageServiceVersion!);
        Write(new(requestId, manifest.SourceRevision, manifest.CpuVoltageManifestSha256!,
            manifest.CpuVoltageServiceVersion!, enrollment.EnrolledAccountSid, "Prepared"), create: true);
    }

    public void Launch(Guid requestId, BrokerEnrollment enrollment, OpenBladeReleaseManifest manifest, IDisposable installLease)
    {
        CpuVoltageLaunchRecord record = ReadRecord(paths);
        if (record.RequestId != requestId || record.Phase != "Prepared" || retainedClosure is null)
        { throw new IOException("CPU launch preparation is unavailable."); }
        VerifyInstalledService();
        record = record with { Phase = "Intent" }; Write(record);
        // The host acquires this same machine-wide lease. No other deployment is
        // admitted after durable intent, including after a broker restart.
        installLease.Dispose();
        retainedProcess = WindowsCpuVoltageHostLauncher.Launch(ClosurePath(requestId), manifest,
            enrollment.EnrolledAccountSid, (pid, created, session, image) =>
            {
                record = record with { Phase = "Bound", ProcessId = pid, Created = created, WindowsSession = session, Image = image };
                Write(record);
            });
        Write(record with { Phase = "Resumed" });
    }

    private void VerifyInstalledService()
    {
        // Compare every installed Service byte before launch or closed reconciliation.
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenBlade");
        foreach (var file in retainedClosure!.Manifest.Where(f => f.Name.StartsWith("service/", StringComparison.Ordinal)))
        {
            string path = Path.Combine(installed, file.Name[8..].Replace('/', '\\'));
            CpuVoltageHostState.RequireProtected(path, false);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            CpuVoltageDeploymentClosure.VerifyFile(stream, file);
        }
    }

    public IDisposable AcquireClosedReconciliation(BrokerInFlightDeployment state, OpenBladeReleaseManifest manifest)
    {
        IDisposable lease = Acquire(paths, true);
        try
        {
            CpuVoltageLaunchRecord record = ReadRecord(paths);
            if (record.RequestId != state.RequestId || record.Source != state.TargetSourceRevision
                || record.ServiceVersion != state.CpuVoltageServiceVersion
                || record.Manifest != manifest.CpuVoltageManifestSha256)
            { throw new InvalidDataException("Closed CPU launch differs from the unresolved installer."); }
            retainedClosure ??= CpuVoltageClosureLease.Open(ClosurePath(record.RequestId), record.Source, record.Manifest, record.ServiceVersion);
            VerifyInstalledService();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void CompleteClosedReconciliation(Guid requestId)
    {
        if (ReadRecord(paths).RequestId != requestId) { throw new InvalidDataException("CPU reconciliation request changed."); }
        retainedProcess?.Dispose(); retainedProcess = null;
        retainedClosure?.Dispose(); retainedClosure = null;
        paths.DeleteRequestStagingDirectory(requestId);
        File.Delete(StatePath);
    }

    public bool TryReconcile()
    {
        if (!File.Exists(StatePath)) { return true; }
        try
        {
            // The only automatic reconciliation is a matching durable idle closure
            // and terminal process, under the same global exclusion lease.
            using IDisposable lease = AcquireInstallLease();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    public void AbandonVerifiedRollback(Guid requestId)
    {
        CpuVoltageLaunchRecord record = ReadRecord(paths);
        if (record.RequestId != requestId || record.Phase != "Prepared"
            || record.ProcessId is not null || record.Created is not null)
        { throw new IOException("Only an unlaunched preparation can close after verified installer rollback."); }
        retainedClosure?.Dispose(); retainedClosure = null;
        paths.DeleteRequestStagingDirectory(requestId);
        File.Delete(StatePath);
    }

    private string ClosurePath(Guid id) => Path.Combine(paths.StagingDirectory, id.ToString("N"), CpuVoltageDeploymentClosure.DirectoryName);
    private static CpuVoltageLaunchRecord ReadRecord(BrokerPaths paths)
    {
        string path = Path.Combine(paths.RootDirectory, LaunchFileName);
        paths.VerifyFile(path);
        var record = JsonSerializer.Deserialize<CpuVoltageLaunchRecord>(CpuVoltageHostState.ReadProtected(path))
            ?? throw new InvalidDataException("Malformed CPU launch state.");
        if (record.RequestId == Guid.Empty || record.Source is not { Length: 40 }
            || record.Manifest is not { Length: 64 } || string.IsNullOrEmpty(record.UserSid)
            || string.IsNullOrEmpty(record.ServiceVersion) || record.Phase is not ("Prepared" or "Intent" or "Bound" or "Resumed"))
        { throw new InvalidDataException("Malformed CPU launch binding."); }
        return record;
    }
    private void Write(CpuVoltageLaunchRecord record, bool create = false)
    {
        string temporary = StatePath + ".tmp";
        using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, record); stream.Flush(true); }
        paths.ProtectFile(temporary);
        File.Move(temporary, StatePath, overwrite: !create);
        paths.VerifyFile(StatePath);
    }
}
