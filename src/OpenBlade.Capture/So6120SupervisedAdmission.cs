using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using Microsoft.Win32;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record So6120AdmissionStamp(Guid RunId, uint ConsoleSession, TimeProvider Clock, long Timestamp);
internal sealed record So6120HidMetadata(string Path, HidHandleIdentity? Identity, int? OpenError = null);
internal sealed record So6120SessionMetadata(bool Interactive, int ProcessSession, uint ConsoleSession, byte AcLineStatus);
internal sealed record So6120DriverMetadata(string WindowsDirectory, string ConfiguredPath, string? FilePath,
    ServiceType ServiceType, ServiceControllerStatus ServiceStatus, long? Bytes, string? Sha256, string? DosTarget);
internal sealed record So6120SupervisedMetadata(So6120AdmissionStamp Stamp, long StartedTimestamp,
    Architecture ProcessArchitecture, HostIdentityReadResult Host, Version? ReportedEcVersion,
    IReadOnlyList<So6120HidMetadata> HidCandidates, So6120SessionMetadata Session, So6120DriverMetadata Driver,
    IReadOnlyList<string> DetectedControllers, ServiceControllerStatus? OpenBladeStatus, IReadOnlyList<int> OpenBladeWriterPids);

// Supplied by the outer verifier. Digests bind its evidence and observed scope;
// comparing them here neither authenticates that evidence nor grants consent.
internal sealed record So6120ChallengeScope(string HidPath, string BiosVersion, Version EcVersion, string DriverSha256,
    string PeripheralConfigurationSha256, string ControllerSoftwareSha256);
[Flags]
internal enum So6120AcceptedResiduals { None = 0, UnnamedFileHandles = 1, UncoveredAcpiPaths = 2, UnobservedWriters = 4 }
internal sealed record So6120CallerChallengeContext(So6120AdmissionStamp Stamp, Guid DecisionId, string EvidenceSha256,
    So6120ChallengeScope ReviewedScope, So6120ChallengeScope ObservedScope,
    So6120AcceptedResiduals ResidualsRequiringAcceptance, So6120AcceptedResiduals AcceptedResiduals);
internal enum So6120HandoffOrigin { AlreadyStopped, StoppedByThisRun }
internal sealed record So6120OriginalProcessExit(int Pid, long StartUtcTicks, long ObservedExitTimestamp);
internal sealed record So6120OpenBladeHandoff(So6120AdmissionStamp Stamp, So6120HandoffOrigin Origin,
    So6120OriginalProcessExit? OriginalExit);

internal enum So6120AdmissionAssessment
{
    InputsMatched, StaleMetadata, HostMismatch, EcVersionMismatch, HidIdentityUnavailable, SessionOrPowerMismatch,
    DriverMismatch, ControllerDetected, CallerChallengeMissingOrMismatched, OpenBladeHandoffMissingOrMismatched,
}

internal static class So6120SupervisedAdmission
{
    internal const string Manufacturer = "Razer";
    internal const string Product = "Blade 16 - RZ09-0581";
    internal const string Sku = "RZ09-05819EN4";
    internal const string Bios = "4.01";
    internal const string DriverName = "inpoutx64";
    internal const string DriverRelativePath = @"System32\Drivers\inpoutx64.sys";
    internal const string DriverTarget = @"\Device\inpoutx64";
    internal const long DriverBytes = 15008;
    internal const string DriverHash = "F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B";
    internal static readonly Version EcVersion = new(1, 4);
    private static readonly TimeSpan MetadataAge = TimeSpan.FromSeconds(5);
    // Applies to verification in the current run, independent of the age of
    // the source and caller-challenge artifacts referenced by that verification.
    private static readonly TimeSpan ContextAge = TimeSpan.FromMinutes(15);

    // One supervised run in the fixed AC/awake scope. A matching assessment is
    // only an input check. It grants no mailbox authority or writer exclusion.
    internal static So6120AdmissionAssessment Assess(TimeProvider clock, So6120SupervisedMetadata metadata,
        So6120CallerChallengeContext? challenge, So6120OpenBladeHandoff? handoff)
    {
        long now = clock.GetTimestamp();
        var stamp = metadata.Stamp;
        if (stamp.RunId == Guid.Empty || !ReferenceEquals(stamp.Clock, clock) || metadata.StartedTimestamp < 0 || stamp.Timestamp > now
            || metadata.StartedTimestamp > stamp.Timestamp || clock.GetElapsedTime(metadata.StartedTimestamp, now) > MetadataAge)
        { return So6120AdmissionAssessment.StaleMetadata; }
        if (metadata.ProcessArchitecture != Architecture.X64 || metadata.Host.Failure is not null
            || metadata.Host.Evidence is not { Manufacturer: Manufacturer, ProductName: Product, Sku: Sku, BiosVersion: Bios })
        { return So6120AdmissionAssessment.HostMismatch; }
        if (metadata.ReportedEcVersion != EcVersion) { return So6120AdmissionAssessment.EcVersionMismatch; }
        if (metadata.HidCandidates.Any(candidate => !IsTargetHidPath(candidate.Path)
            || candidate.Identity is not { } identity || !TargetBladeDevice.Matches(identity.VendorId, identity.ProductId)))
        { return So6120AdmissionAssessment.HidIdentityUnavailable; }
        So6120HidMetadata[] exact = metadata.HidCandidates.Where(candidate => candidate.Identity is { } identity
            && identity.UsagePage == TargetBladeDevice.UsagePage && identity.Usage == TargetBladeDevice.Usage
            && TargetBladeDevice.CanCarryProtocolReport(identity.FeatureReportByteLength)).ToArray();
        if (exact.Length != 1) { return So6120AdmissionAssessment.HidIdentityUnavailable; }
        var session = metadata.Session;
        if (!session.Interactive || session.ConsoleSession is 0 or uint.MaxValue
            || session.ProcessSession != session.ConsoleSession || session.ConsoleSession != stamp.ConsoleSession
            || session.AcLineStatus != 1)
        { return So6120AdmissionAssessment.SessionOrPowerMismatch; }
        if (!MatchesDriver(metadata.Driver)) { return So6120AdmissionAssessment.DriverMismatch; }
        if (metadata.DetectedControllers.Count != 0) { return So6120AdmissionAssessment.ControllerDetected; }

        bool MatchesContext(So6120AdmissionStamp context) => ReferenceEquals(context.Clock, clock)
            && context.RunId == stamp.RunId && context.ConsoleSession == stamp.ConsoleSession
            && context.Timestamp >= 0 && context.Timestamp <= stamp.Timestamp && clock.GetElapsedTime(context.Timestamp, now) <= ContextAge;

        if (challenge is null || !MatchesContext(challenge.Stamp) || challenge.DecisionId == Guid.Empty
            || !IsSha256(challenge.EvidenceSha256) || !MatchesChallengeScope(challenge, exact[0].Path))
        { return So6120AdmissionAssessment.CallerChallengeMissingOrMismatched; }
        if (handoff is null || !MatchesContext(handoff.Stamp) || handoff.Stamp.Timestamp > metadata.StartedTimestamp
            || metadata.OpenBladeStatus != ServiceControllerStatus.Stopped || metadata.OpenBladeWriterPids.Count != 0
            || !MatchesHandoff(handoff, clock, now))
        { return So6120AdmissionAssessment.OpenBladeHandoffMissingOrMismatched; }
        return So6120AdmissionAssessment.InputsMatched;
    }

    private static bool MatchesChallengeScope(So6120CallerChallengeContext challenge, string exactHidPath)
    {
        const So6120AcceptedResiduals known = So6120AcceptedResiduals.UnnamedFileHandles
            | So6120AcceptedResiduals.UncoveredAcpiPaths | So6120AcceptedResiduals.UnobservedWriters;
        var observed = challenge.ObservedScope;
        return challenge.ReviewedScope == observed && Equal(observed.HidPath, exactHidPath)
            && observed.BiosVersion == Bios && observed.EcVersion == EcVersion && Equal(observed.DriverSha256, DriverHash)
            && IsSha256(observed.PeripheralConfigurationSha256) && IsSha256(observed.ControllerSoftwareSha256)
            && (challenge.ResidualsRequiringAcceptance & So6120AcceptedResiduals.UnobservedWriters) != 0
            && (challenge.ResidualsRequiringAcceptance & challenge.AcceptedResiduals) == challenge.ResidualsRequiringAcceptance
            && (challenge.AcceptedResiduals & ~known) == 0;
    }

    private static bool MatchesHandoff(So6120OpenBladeHandoff handoff, TimeProvider clock, long now) =>
        handoff.Origin switch
        {
            So6120HandoffOrigin.AlreadyStopped => handoff.OriginalExit is null,
            So6120HandoffOrigin.StoppedByThisRun => handoff.OriginalExit is { Pid: > 0, StartUtcTicks: > 0, ObservedExitTimestamp: >= 0 } exit
                && exit.StartUtcTicks <= DateTime.MaxValue.Ticks && exit.ObservedExitTimestamp <= handoff.Stamp.Timestamp
                && clock.GetElapsedTime(exit.ObservedExitTimestamp, now) <= ContextAge,
            _ => false,
        };

    internal static bool IsTargetHidPath(string path)
    {
        const string prefix = @"\\?\hid#vid_1532&pid_02e0";
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && path[prefix.Length] is '&' or '#';
    }

    internal static bool IsConfiguredDriverPath(string path, string expected) => Equal(path, expected)
        || Equal(path, DriverRelativePath) || Equal(path, @"\SystemRoot\" + DriverRelativePath)
        || Equal(path, @"%SystemRoot%\" + DriverRelativePath);

    private static bool MatchesDriver(So6120DriverMetadata driver)
    {
        string expected = Path.Combine(driver.WindowsDirectory, DriverRelativePath);
        return Path.IsPathFullyQualified(driver.WindowsDirectory) && Equal(driver.FilePath, expected)
            && IsConfiguredDriverPath(driver.ConfiguredPath, expected) && driver.ServiceType == ServiceType.KernelDriver
            && driver.ServiceStatus == ServiceControllerStatus.Running && driver.Bytes == DriverBytes
            && Equal(driver.Sha256, DriverHash) && Equal(driver.DosTarget, DriverTarget);
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Equal(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

internal enum So6120MetadataStage { Platform, Host, Smbios, Hid, ConsoleAndPower, Driver, Controllers }
internal sealed record So6120MetadataRead(So6120SupervisedMetadata? Metadata, So6120MetadataStage? FailedStage, int? Error,
    HostIdentityReadResult? HostFailure = null);

// Collects OS and short-lived zero-access HID metadata only. No InpOut device
// open, mailbox access, service mutation or vendor DLL loading occurs here.
internal static class So6120WindowsSupervisedMetadata
{
    private const uint RawSmbiosProvider = 0x52534D42; // RSMB
    private const int MaxSmbiosBytes = 1024 * 1024;
    private const int MaxHidListCharacters = 128 * 1024;
    private static readonly HidOpenParameters MetadataOnlyHid = new(
        DesiredAccess: 0, ShareMode: 3, CreationDisposition: 3, FlagsAndAttributes: 0, Inheritable: false);
    private static readonly HashSet<string> AdditionalControllerProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "RazerAppEngine", "razerwdl", "GameManagerService3", "razer_elevation_service",
    };

    internal static So6120MetadataRead Capture(Guid runId, TimeProvider clock)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        { return new(null, So6120MetadataStage.Platform, null); }
        long started = clock.GetTimestamp();
        So6120MetadataStage stage = So6120MetadataStage.Host;
        try
        {
            var registry = new WindowsRegistryValueSource();
            var host = new WindowsHostIdentitySource(registry, new RuntimeWindowsPlatformEvidenceSource(registry),
                new ExpectedHostIdentity(So6120SupervisedAdmission.Manufacturer, So6120SupervisedAdmission.Product, So6120SupervisedAdmission.Sku,
                    [So6120SupervisedAdmission.Bios])).ReadExactIdentity();
            if (host.Failure is not null) { return new(null, stage, null, host); }
            stage = So6120MetadataStage.Smbios;
            uint size = GetSystemFirmwareTable(RawSmbiosProvider, 0, null, 0);
            if (size is < 8 or > MaxSmbiosBytes) { return new(null, stage, size == 0 ? Marshal.GetLastWin32Error() : null); }
            byte[] smbios = new byte[size];
            if (GetSystemFirmwareTable(RawSmbiosProvider, 0, smbios, size) != size)
            { return new(null, stage, Marshal.GetLastWin32Error()); }
            Version? ec = ReadReportedEcVersion(smbios);
            stage = So6120MetadataStage.Hid;
            var hid = ReadHidMetadata(ReadPresentHidPaths(), new WindowsHidNative());
            stage = So6120MetadataStage.ConsoleAndPower;
            if (!GetSystemPowerStatus(out var power)) { return new(null, stage, Marshal.GetLastWin32Error()); }
            using var process = Process.GetCurrentProcess();
            uint console = WTSGetActiveConsoleSessionId();
            var session = new So6120SessionMetadata(Environment.UserInteractive, process.SessionId, console, power.AcLineStatus);
            stage = So6120MetadataStage.Driver;
            var driver = ReadDriverMetadata(registry);
            stage = So6120MetadataStage.Controllers;
            var controllers = ReadControllerMetadata();
            var stamp = new So6120AdmissionStamp(runId, console, clock, clock.GetTimestamp());
            return new(new(stamp, started, RuntimeInformation.ProcessArchitecture, host, ec, hid, session, driver,
                controllers.Detected, controllers.OpenBlade, controllers.WriterPids), null, null);
        }
        catch (Exception exception) { return new(null, stage, exception.HResult); }
    }

    private static IReadOnlyList<string> ReadPresentHidPaths()
    {
        // A successful present-only MULTI_SZ is required. A size race or an
        // invalid result invalidates this snapshot instead of returning a prefix.
        Guid hid = new("4d1e55b2-f16f-11cf-88cb-001111000030");
        uint result = CM_Get_Device_Interface_List_SizeW(out uint size, ref hid, IntPtr.Zero, 0);
        if (result != 0) { throw new IOException("HID interface list size query failed.", (int)result); }
        if (size is 0 or > MaxHidListCharacters) { throw new IOException("Invalid HID interface list size."); }
        char[] buffer = new char[size];
        result = CM_Get_Device_Interface_ListW(ref hid, IntPtr.Zero, buffer, size, 0);
        if (result != 0) { throw new IOException("HID interface list query failed.", (int)result); }
        return ParsePresentHidPaths(buffer);
    }

    internal static IReadOnlyList<string> ParsePresentHidPaths(ReadOnlySpan<char> buffer)
    {
        List<string> paths = [];
        while (!buffer.IsEmpty)
        {
            int end = buffer.IndexOf('\0');
            if (end == 0 && buffer.IndexOfAnyExcept('\0') < 0) { return paths.AsReadOnly(); }
            if (end <= 0) { break; }
            paths.Add(buffer[..end].ToString());
            buffer = buffer[(end + 1)..];
        }
        throw new IOException("Invalid HID interface list terminator.");
    }

    internal static IReadOnlyList<So6120HidMetadata> ReadHidMetadata(IReadOnlyList<string> paths, IHidCandidateNative native)
    {
        List<So6120HidMetadata> candidates = [];
        foreach (string path in paths.Where(So6120SupervisedAdmission.IsTargetHidPath))
        {
            // Keep any relevant unopenable candidate, so one good collection
            // cannot turn an incomplete identity inspection into uniqueness.
            var opened = native.TryOpen(path, MetadataOnlyHid);
            using var candidate = opened.Candidate;
            candidates.Add(new(path, candidate is null ? null : native.ReadIdentity(candidate), opened.Win32Error));
        }
        return candidates.AsReadOnly();
    }

    // RawSMBIOSData header + DMTF DSP0134 Type 0 firmware release fields 16h/17h.
    // These bytes are platform-reported versions, not a hash of live EC code.
    internal static Version? ReadReportedEcVersion(ReadOnlySpan<byte> raw)
    {
        if (raw.Length is < 8 or > MaxSmbiosBytes || BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]) != raw.Length - 8) { return null; }
        ReadOnlySpan<byte> table = raw[8..];
        ReadOnlySpan<byte> endOfStrings = [0, 0];
        Version? version = null;
        while (table.Length >= 4)
        {
            byte type = table[0], length = table[1];
            if (length < 4 || length > table.Length) { return null; }
            int terminator = table[length..].IndexOf(endOfStrings);
            if (terminator < 0) { return null; }
            if (type == 0)
            {
                if (version is not null || length < 0x18 || table[0x16] == 0xFF || table[0x17] == 0xFF) { return null; }
                version = new(table[0x16], table[0x17]);
            }
            if (type == 127) { return length == 4 ? version : null; }
            table = table[(length + terminator + 2)..];
        }
        return null;
    }

    private static So6120DriverMetadata ReadDriverMetadata(IRegistryValueSource registry)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!Path.IsPathFullyQualified(windows)) { throw new IOException("Windows directory is unavailable."); }
        string expected = Path.Combine(windows, So6120SupervisedAdmission.DriverRelativePath);
        string configured = registry.Read(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SYSTEM\CurrentControlSet\Services\inpoutx64", "ImagePath") as string ?? "";
        using var service = new ServiceController(So6120SupervisedAdmission.DriverName);
        ServiceType type = service.ServiceType;
        ServiceControllerStatus status = service.Status;
        if (!So6120SupervisedAdmission.IsConfiguredDriverPath(configured, expected)
            || (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0)
        { return new(windows, configured, null, type, status, null, null, null); }
        using var file = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = file.Length;
        string? hash = size == So6120SupervisedAdmission.DriverBytes ? Convert.ToHexString(SHA256.HashData(file)) : null;
        char[] target = new char[512];
        uint chars = QueryDosDevice(So6120SupervisedAdmission.DriverName, target, (uint)target.Length);
        int firstNull = chars <= target.Length ? Array.IndexOf(target, '\0', 0, (int)chars) : -1;
        return new(windows, configured, expected, type, status, size, hash,
            firstNull > 0 ? new string(target, 0, firstNull) : null);
    }

    private static (IReadOnlyList<string> Detected, ServiceControllerStatus? OpenBlade, IReadOnlyList<int> WriterPids)
        ReadControllerMetadata()
    {
        List<string> detected = [];
        List<int> writers = [];
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                // A process using a service alias may exist without a matching
                // SCM registration. An unreadable name invalidates this sample.
                string name = process.ProcessName;
                if (name.Equals("OpenBlade.Service", StringComparison.OrdinalIgnoreCase)) { writers.Add(process.Id); }
                else if (IsKnownControllerProcess(name)) { detected.Add(name); }
            }
        }
        finally { foreach (Process process in processes) { process.Dispose(); } }
        ServiceControllerStatus? openBlade = null;
        ServiceController[] services = ServiceController.GetServices();
        try
        {
            foreach (ServiceController service in services)
            {
                if (service.ServiceName.Equals("OpenBlade", StringComparison.OrdinalIgnoreCase)) { openBlade = service.Status; }
                if (RazerControllerServiceAliases.MatchesService(service.ServiceName, service.DisplayName)
                    && service.Status != ServiceControllerStatus.Stopped) { detected.Add(service.ServiceName); }
            }
        }
        finally { foreach (ServiceController service in services) { service.Dispose(); } }
        return (detected.AsReadOnly(), openBlade, writers.AsReadOnly());
    }

    internal static bool IsKnownControllerProcess(string name) =>
        AdditionalControllerProcesses.Contains(name) || RazerControllerServiceAliases.Contains(name);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint table, [Out] byte[]? buffer, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, [Out] char[] target, uint capacity);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, IntPtr device, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClass, IntPtr device,
        [Out] char[] buffer, uint length, uint flags);
}
