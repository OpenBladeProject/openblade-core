using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record CpuVoltageSessionEnrollment(Guid Session, string UserSid, int WindowsSession,
    int HostPid, long HostCreated, string HostImage, string Source, string Manifest, string ServiceVersion);

internal sealed class WindowsCpuVoltageSessionPlatform : IDisposable
{
    internal const string Root = @"C:\ProgramData\OpenBladeCpuVoltageSessions";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private readonly WindowsCpuPmLaunchFiles pins = new(voltageValidation: true);
    private readonly CpuVoltageOwnedSmokeCommand.Options options;
    private readonly IReadOnlyList<CpuPmLaunchFile> manifest;
    private WindowsCpuVoltageServiceProcess? preparedService;
    private WindowsCpuVoltageReadOnlyCleanup? retainedCleanup;
    private readonly FileStream hostLease;
    internal string Image => Path.Combine(options.Payload, "OpenBlade.Capture.exe");
    internal ICpuPmLaunchFiles Pins => pins;
    internal CpuVoltageSessionEnrollment Enrollment { get; }
    internal string DirectoryPath => Path.Combine(Root, Enrollment.Session.ToString("N"));
    internal WindowsCpuVoltageSessionPlatform(CpuVoltageOwnedSmokeCommand.Options options)
    {
        this.options = options;
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            || identity.User is null || identity.IsSystem || process.SessionId == 0
            || WTSGetActiveConsoleSessionId() != process.SessionId)
        { throw new UnauthorizedAccessException("Start the host once from an elevated active local console; no automatic elevation is supported."); }
        CpuPmAuthenticatedChildRun.PinAncestors(pins, options.Payload);
        pins.PinDirectory(options.Payload, true, true);
        var manifestPin = pins.PinFile(Path.Combine(options.Payload, "manifest.json"), CpuPmLaunchSchema.VoltageManifestLimit, true);
        manifest = CpuPmLaunchSchema.ParseManifest(manifestPin.ReadBounded(CpuPmLaunchSchema.VoltageManifestLimit),
            options.Source, options.Manifest,
            ["OpenBlade.Capture.exe", "OpenBlade.Capture.dll", "OpenBlade.Capture.deps.json", "OpenBlade.Capture.runtimeconfig.json",
                "firmware-smoke/OpenBlade.FirmwareSmoke.exe", "firmware-smoke/OpenBlade.FirmwareSmoke.dll",
                "firmware-smoke/OpenBlade.FirmwareSmoke.deps.json", "firmware-smoke/OpenBlade.FirmwareSmoke.runtimeconfig.json",
                "service/OpenBlade.Service.exe", "service/OpenBlade.Service.dll", "service/OpenBlade.Service.deps.json", "service/OpenBlade.Service.runtimeconfig.json"], true);
        CpuPmAuthenticatedChildRun.ValidatePayload(pins, options.Payload, manifest, CancellationToken.None, voltageValidation: true);
        using var self = WindowsCpuPmProcessProbe.Current();
        var current = self.Read(true);
        if (current.Image != Image || current.State != CpuPmChildState.Running) { throw new IOException("Run the host from its reviewed private package."); }
        VerifyInstalled();
        Enrollment = new(Guid.NewGuid(), identity.User.Value, process.SessionId, current.Pid, current.Created,
            Image, options.Source, options.Manifest, options.ServiceVersion);
        CreateProtectedDirectory(Root, identity.User);
        string leasePath = Path.Combine(Root, "host.lock");
        try { RequireProtected(leasePath, false); } catch (FileNotFoundException) { }
        hostLease = OpenExclusiveLease(leasePath);
        try
        {
            string active = Path.Combine(Root, "active-session.json");
            if (File.Exists(active))
            {
                RequireProtected(active, false);
                Guid previous = ReadSessionMarker(ReadSmall(active));
                string closed = Path.Combine(Root, previous.ToString("N"), "closed.json");
                RequireProtected(closed, false);
                RequireMatchingClosure(previous, ReadSmall(closed));
            }
            CreateProtectedDirectory(DirectoryPath, identity.User);
            WriteNew("enrollment.json", Enrollment);
            string temporary = Path.Combine(Root, Enrollment.Session.ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, Enrollment.Session); stream.Flush(true); }
            File.Move(temporary, active, overwrite: true);
        }
        catch { hostLease.Dispose(); throw; }
    }
    internal void VerifyInstalled()
    {
        string installed = Path.GetDirectoryName(WindowsCpuVoltageServiceProcess.Image)!;
        CpuPmAuthenticatedChildRun.PinAncestors(pins, installed); pins.PinDirectory(installed, false, false);
        RequireProtected(installed, true);
        foreach (var entry in manifest.Where(f => f.Name.StartsWith("service/", StringComparison.Ordinal)))
        {
            string path = Path.Combine(installed, entry.Name[8..].Replace('/', '\\'));
            CpuPmAuthenticatedChildRun.PinAncestors(pins, path); RequireProtected(path, false);
            var file = pins.PinFile(path, CpuPmLaunchSchema.MaximumFileBytes, false);
            if (file.Length != entry.Length || file.Sha256 != entry.Sha256) { throw new IOException("Installed Service closure differs from enrollment."); }
        }
        if (FileVersionInfo.GetVersionInfo(Path.Combine(installed, "OpenBlade.Service.dll")).ProductVersion != options.ServiceVersion)
        { throw new IOException("Installed validation Service version differs."); }
        pins.AssertAllPins();
    }
    internal void PrepareService(CancellationToken token)
    {
        VerifyInstalled();
        if (!new WindowsCpuPmDriverLifecyclePlatform().ReadIndependentAbsence().IsAbsent)
        { throw new IOException("A temporary driver or unresolved driver lifetime already exists."); }
        using var service = new ServiceController(OpenBladeWindowsService.Name);
        RequireServiceConfiguration(service.ServiceHandle.DangerousGetHandle());
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            // One fixed start. No install, command line, account change, stop or retry.
            service.Start();
            service.Refresh();
            long started = Stopwatch.GetTimestamp();
            while (service.Status == ServiceControllerStatus.StartPending && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
            { token.ThrowIfCancellationRequested(); Thread.Sleep(100); service.Refresh(); }
        }
        if (service.Status != ServiceControllerStatus.Running) { throw new IOException("Exact Service did not reach Running; no retry."); }
        RequireServiceConfiguration(service.ServiceHandle.DangerousGetHandle()); VerifyInstalled();
        preparedService = WindowsCpuVoltageServiceProcess.Bind(pins, options.ServiceVersion, token, value => preparedService = value);
    }
    internal void ResolveBeforeOwner(Guid request, bool preparationAttempted, CancellationToken token)
    {
        VerifyInstalled();
        var drivers = new WindowsCpuPmDriverLifecyclePlatform();
        if (!drivers.ReadIndependentAbsence().IsAbsent) { throw new IOException("Driver absence required before unlaunched cleanup."); }
        if (preparationAttempted)
        {
            using var service = new ServiceController(OpenBladeWindowsService.Name);
            RequireServiceConfiguration(service.ServiceHandle.DangerousGetHandle());
            if (preparedService is null) { throw new IOException("No exact retained Service binding; unlaunched cleanup cannot adopt a process."); }
            if (preparedService.ReadState() != CpuPmChildState.Terminal)
            {
                preparedService.AssertState(CpuPmChildState.Running);
                preparedService.ShutdownOnce(token, requireNoCpuRecovery: true);
            }
            while (preparedService.ReadState() != CpuPmChildState.Terminal)
            { token.ThrowIfCancellationRequested(); Thread.Sleep(50); }
            preparedService.AssertState(CpuPmChildState.Terminal);
        }
        if (!drivers.ReadIndependentAbsence().IsAbsent) { throw new IOException("Driver absence changed during unlaunched cleanup."); }
        token.ThrowIfCancellationRequested();
        WriteNew("unlaunched-closed-" + request.ToString("N") + ".json", new
        {
            Request = request,
            Enrollment.Source,
            Enrollment.Manifest,
            Enrollment.ServiceVersion,
            OwnerLaunchAttempted = false,
            ServicePreparationAttempted = preparationAttempted,
            Service = preparedService?.Identity,
            ServiceTerminal = preparationAttempted,
            OwnedDriverAbsent = true,
        });
        if (preparationAttempted) { ReleaseVerifiedTerminalService(); }
    }
    internal void ReleaseVerifiedTerminalService()
    {
        if (preparedService is null) { throw new IOException("No retained Service identity."); }
        preparedService.ReleaseAfterTerminal(); preparedService = null;
    }
    internal ProcessStartInfo OwnerStart(CpuVoltageSessionAction action, Guid request)
    {
        var start = new ProcessStartInfo(Image)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = options.Payload, RedirectStandardOutput = true };
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase) || key == "__COMPAT_LAYER").ToArray())
        { start.Environment.Remove(key); }
        foreach (string arg in OwnerArguments(options, action)) { start.ArgumentList.Add(arg); }
        start.ArgumentList.Add("--session-job");
        start.ArgumentList.Add(Enrollment.Session.ToString("N") + ":" + request.ToString("N"));
        return start;
    }
    internal CpuVoltageJobDeadline WriteJobDeadline(Guid request, long started)
    {
        var value = new CpuVoltageJobDeadline(Enrollment.Session, request, Enrollment.HostPid, Enrollment.HostCreated,
            Enrollment.HostImage, started, checked(started + CpuVoltageJobDeadline.BudgetMilliseconds),
            Enrollment.Source, Enrollment.Manifest, Enrollment.ServiceVersion);
        if (!value.IsCurrent(Environment.TickCount64)) { throw new IOException("Validation job deadline is unavailable."); }
        WriteNew(value.FileName, value);
        return value;
    }
    internal static string[] OwnerArguments(CpuVoltageOwnedSmokeCommand.Options options, CpuVoltageSessionAction action)
    {
        if (action is not (CpuVoltageSessionAction.ObserveOnly or CpuVoltageSessionAction.VoltageCycle))
        { throw new ArgumentException("Only fixed observation or cycle jobs may launch."); }
        return [CpuVoltageOwnedSmokeCommand.Name, .. action == CpuVoltageSessionAction.ObserveOnly ? new[] { "--observe-only" } : [],
            "--payload", options.Payload, "--source", options.Source, "--manifest-sha256", options.Manifest,
            "--expected-service-version", options.ServiceVersion, "--confirm-target",
            action == CpuVoltageSessionAction.ObserveOnly ? CpuVoltageOwnedSmokeCommand.ObserveConfirmation : CpuVoltageOwnedSmokeCommand.Confirmation];
    }
    internal ICpuPmLaunchFilePin OwnerImage() => pins.PinFile(Image, CpuPmLaunchSchema.MaximumFileBytes, true);
    internal Dictionary<string, JsonElement> ReadEvidence(Guid run)
    {
        var records = new Dictionary<string, JsonElement>();
        string root = Path.Combine(WindowsCpuPmCaptureEvidenceFiles.FixedRoot, run.ToString("N"));
        CpuPmAuthenticatedChildRun.PinAncestors(pins, root); pins.PinDirectory(root, true, true);
        int bytes = 0;
        foreach (string name in new[] { "evidence/cpu-voltage-optimizer.json", "evidence/cpu-voltage-optimizer-baseline.json",
            "control/voltage-owner-completed.json", "control/voltage-observation-recorded.json", "control/voltage-restoration-verified.json",
            "control/voltage-context.json", "control/voltage-child.json" })
        {
            string path = Path.Combine(root, name.Replace('/', '\\'));
            if (!File.Exists(path)) { continue; }
            CpuPmAuthenticatedChildRun.PinAncestors(pins, path);
            var pin = pins.PinFile(path, CpuPmLaunchSchema.ManifestLimit, true);
            byte[] content = pin.ReadBounded(CpuPmLaunchSchema.ManifestLimit); bytes += content.Length;
            if (bytes > 192 * 1024) { throw new IOException("Evidence exceeds session export bound."); }
            using var json = JsonDocument.Parse(content); records.Add(name, json.RootElement.Clone());
        }
        try { records.Add("control/lifecycle.jsonl", JsonSerializer.SerializeToElement(System.Text.Encoding.UTF8.GetString(ReadJournal(run)))); }
        catch (IOException) { /* A live incomplete append is unavailable, never cleanup authority. */ }
        return records;
    }
    internal byte[] ReadJournal(Guid run)
    {
        string path = Path.Combine(WindowsCpuPmCaptureEvidenceFiles.FixedRoot, run.ToString("N"), "control", "lifecycle.jsonl");
        CpuPmAuthenticatedChildRun.PinAncestors(pins, path);
        using var handle = WindowsCpuPmCaptureEvidenceFiles.CreateFileW(path, 0x80020000, 3, 0, 3, 0x00200000, 0);
        WindowsCpuPmCaptureEvidenceFiles.VerifyShape(handle, path, false);
        WindowsCpuPmCaptureEvidenceFiles.VerifyPrivateAcl(handle);
        using var stream = new FileStream(handle, FileAccess.Read);
        byte[] bytes = ReadCoherentJournal(stream);
        WindowsCpuPmCaptureEvidenceFiles.VerifyShape(handle, path, false);
        WindowsCpuPmCaptureEvidenceFiles.VerifyPrivateAcl(handle);
        return bytes;
    }
    internal static byte[] ReadCoherentJournal(Stream stream)
    {
        long length = stream.Length;
        if (length is <= 0 or > CpuPmDurableDriverJournal.MaximumBytes) { throw new IOException("Lifecycle size unavailable."); }
        byte[] first = new byte[(int)length], second = new byte[(int)length];
        stream.Position = 0; stream.ReadExactly(first);
        if (stream.Length != length || first[^1] != (byte)'\n') { throw new IOException("Lifecycle append in progress."); }
        stream.Position = 0; stream.ReadExactly(second);
        if (stream.Length != length || !first.AsSpan().SequenceEqual(second)) { throw new IOException("Lifecycle snapshot changed."); }
        return first;
    }
    internal void PinTerminalJournal(Guid run, string expectedHash)
    {
        string path = Path.Combine(WindowsCpuPmCaptureEvidenceFiles.FixedRoot, run.ToString("N"), "control", "lifecycle.jsonl");
        if (pins.PinFile(path, CpuPmDurableDriverJournal.MaximumBytes, true).Sha256 != expectedHash)
        { throw new IOException("Terminal owner journal changed."); }
    }
    internal WindowsCpuVoltageReadOnlyCleanup BeginReadOnlyCleanup(Guid request, Guid run, DateTimeOffset launched,
        Process owner, CpuPmRetainedLaunchProcess retainedOwner, Task ownerOutput, Func<bool> contextCurrent, Func<string?> ownerReason)
    {
        if (preparedService is null) { throw new IOException("Exact retained Service unavailable."); }
        var records = ReadEvidence(run);
        string childImage = Path.Combine(options.Payload, "firmware-smoke", "OpenBlade.FirmwareSmoke.exe");
        string childHash = manifest.Single(file => file.Name == "firmware-smoke/OpenBlade.FirmwareSmoke.exe").Sha256;
        if (pins.PinFile(childImage, CpuPmLaunchSchema.MaximumFileBytes, true).Sha256 != childHash)
        { throw new IOException("Child image differs."); }
        var proof = CpuVoltageReadOnlyCleanup.Verify(Enrollment, run, preparedService.Identity, childImage, childHash,
            records, launched, DateTimeOffset.UtcNow);
        retainedCleanup = new(this, preparedService, owner, retainedOwner, ownerOutput, proof, run, request, contextCurrent, ownerReason);
        return retainedCleanup;
    }
    internal void VerifyCleanupVendorFiles()
    {
        foreach (var expected in Rz090528InstalledCpuBackendInspector.ExpectedHashes)
        {
            CpuPmAuthenticatedChildRun.PinAncestors(pins, expected.Key);
            if (pins.PinFile(expected.Key, CpuPmLaunchSchema.MaximumFileBytes, false).Sha256 != expected.Value)
            { throw new IOException("Cleanup vendor file hash differs."); }
        }
        if (!WindowsRz090528CpuQueryAdapter.FilesExact(WindowsInstalledCpuBackendSnapshotSource.ReadFileMetadata()))
        { throw new IOException("Cleanup vendor signatures differ."); }
        pins.AssertAllPins();
    }
    internal void WriteNew(string name, object value)
    {
        RequireProtected(DirectoryPath, true);
        using var stream = new FileStream(Path.Combine(DirectoryPath, name), FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, value, value.GetType(), CpuVoltageSessionProtocol.Json); stream.Flush(true);
    }
    internal JsonElement ReadCleanupReceipt(Guid request)
    {
        string path = Path.Combine(DirectoryPath, "cleanup-" + request.ToString("N") + ".json");
        RequireProtected(path, false);
        var pin = pins.PinFile(path, CpuPmLaunchSchema.ManifestLimit, false);
        using var document = JsonDocument.Parse(pin.ReadBounded(CpuPmLaunchSchema.ManifestLimit));
        return document.RootElement.Clone();
    }
    internal static CpuVoltageSessionEnrollment ReadEnrollment(Guid session)
    {
        string path = Path.Combine(Root, session.ToString("N"), "enrollment.json");
        foreach (string directory in new[] { Root, Path.GetDirectoryName(path)! }) { RequireProtected(directory, true); }
        RequireProtected(path, false);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 4096) { throw new IOException("Invalid enrollment size."); }
        var result = JsonSerializer.Deserialize<CpuVoltageSessionEnrollment>(file, CpuVoltageSessionProtocol.Json)
            ?? throw new IOException("Missing enrollment.");
        using var identity = WindowsIdentity.GetCurrent(); using var process = Process.GetCurrentProcess();
        if (result.Session != session || result.UserSid != identity.User?.Value || result.WindowsSession != process.SessionId)
        { throw new IOException("Enrollment belongs to another user/session."); }
        return result;
    }
    private static void CreateProtectedDirectory(string path, SecurityIdentifier user)
    {
        if (!Directory.Exists(path))
        {
            var acl = new DirectorySecurity(); acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            { acl.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)); }
            acl.AddAccessRule(new(user, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(acl);
        }
        RequireProtected(path, true);
    }
    internal static void RequireProtected(string path, bool directory)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { throw new IOException("Redirected session path."); } }
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        RequireSecurityDescriptor(new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0), (int)writes, allowInstaller: true);
    }
    internal static void RequireSecurityDescriptor(RawSecurityDescriptor security, int writes, bool allowInstaller)
    {
        bool Trusted(SecurityIdentifier? sid) => sid?.Value is "S-1-5-18" or "S-1-5-32-544"
            || allowInstaller && sid?.Value == TrustedInstaller;
        if (!Trusted(security.Owner) || security.DiscretionaryAcl is null)
        { throw new IOException("Missing protected owner or explicit DACL."); }
        foreach (GenericAce entry in security.DiscretionaryAcl)
        {
            if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
            { throw new IOException("Unsupported protection ACE."); }
            if (ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AceFlags & AceFlags.InheritOnly) == 0
                && (ace.AccessMask & (writes | 0x10000000 | 0x40000000)) != 0 && !Trusted(ace.SecurityIdentifier))
            { throw new IOException("Protected object grants untrusted mutation rights."); }
        }
    }
    private static void RequireServiceConfiguration(nint service)
    {
        nint buffer = Marshal.AllocHGlobal(8192);
        try
        {
            if (!QueryServiceConfigW(service, buffer, 8192, out _)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var value = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            if (value.Type != 0x10 || value.Start != 2 || Marshal.PtrToStringUni(value.Account) != "LocalSystem"
                || Marshal.PtrToStringUni(value.Image) != "\"" + WindowsCpuVoltageServiceProcess.Image + "\"")
            { throw new IOException("Fixed installed Service configuration differs."); }
            byte[] descriptor = new byte[8192];
            if (!QueryServiceObjectSecurity(service, 5, descriptor, descriptor.Length, out _))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var security = new RawSecurityDescriptor(descriptor, 0);
            RequireSecurityDescriptor(security, 0x0002 | 0x10000 | 0x40000 | 0x80000, allowInstaller: false);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static FileStream OpenExclusiveLease(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private static byte[] ReadSmall(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 4096) { throw new IOException("Invalid session marker size."); }
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static Guid ReadSessionMarker(byte[] bytes)
    {
        Guid result = JsonSerializer.Deserialize<Guid>(bytes);
        return result != Guid.Empty ? result : throw new IOException("Invalid prior session.");
    }
    internal static void RequireMatchingClosure(Guid previous, byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("session").GetGuid() != previous || !root.GetProperty("idleResolved").GetBoolean())
        { throw new IOException("Prior session has no exact resolved closure; explicit reconciliation required."); }
    }
    public void Dispose()
    {
        // Only the idle/resolved host path calls this. Process absence alone is
        // never a replacement for this durable closure on a later enrollment.
        WriteNew("closed.json", new { session = Enrollment.Session, idleResolved = true });
        pins.Dispose(); hostLease.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    { internal uint Type, Start, Error; internal nint Image, Group; internal uint Tag; internal nint Dependencies, Account, Display; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(nint service, nint buffer, int size, out int required);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceObjectSecurity(nint service, uint information, byte[] descriptor, int size, out int required);
    [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
}
