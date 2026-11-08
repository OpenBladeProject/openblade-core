using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using Microsoft.Win32;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.FanStop;

internal enum So6120MetadataStage { Platform, Host, Smbios, Hid, ConsoleAndPower, Driver, Controllers }
internal sealed record So6120MetadataRead(So6120SupervisedMetadata? Metadata, So6120MetadataStage? FailedStage, int? Error,
    HostIdentityReadResult? HostFailure = null);
internal sealed record So6120ServiceVolatileMetadata(long StartedTimestamp, long CompletedTimestamp,
    So6120SessionMetadata Session, IReadOnlyList<string> DetectedControllers,
    ServiceControllerStatus? OpenBladeStatus, IReadOnlyList<int> OpenBladeWriterPids);

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

    internal static So6120MetadataRead Capture(Guid runId, TimeProvider clock) =>
        Capture(runId, clock, new ExpectedHostIdentity(
            So6120SupervisedAdmission.Manufacturer, So6120SupervisedAdmission.Product, So6120SupervisedAdmission.Sku,
            [So6120SupervisedAdmission.Bios]));

    internal static So6120MetadataRead CaptureForService(Guid runId, TimeProvider clock,
        FanStopModelContract contract) =>
        Capture(runId, clock, contract.Host, serviceContract: contract);

    // SMBIOS is the boot's reported table, not a live EC measurement. Reusing
    // its admitted version adds no weaker live-firmware claim. The deny-write/
    // delete driver pin was independently hashed before this overload is used.
    // Host registry, HID, controller, configured-driver and DOS-target identity
    // are still captured afresh for every actual command admission.
    internal static So6120MetadataRead CaptureForService(Guid runId, TimeProvider clock,
        FanStopModelContract contract, FileStream driverPin, Version bootReportedEcVersion) =>
        Capture(runId, clock, contract.Host, driverPin, bootReportedEcVersion, contract);

    private static So6120MetadataRead Capture(Guid runId, TimeProvider clock, ExpectedHostIdentity expectedHost,
        FileStream? driverPin = null, Version? bootReportedEcVersion = null,
        FanStopModelContract? serviceContract = null)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        { return new(null, So6120MetadataStage.Platform, null); }
        long started = clock.GetTimestamp();
        So6120MetadataStage stage = So6120MetadataStage.Host;
        try
        {
            var registry = new WindowsRegistryValueSource();
            var host = new WindowsHostIdentitySource(registry, new RuntimeWindowsPlatformEvidenceSource(registry),
                expectedHost).ReadExactIdentity();
            if (host.Failure is not null) { return new(null, stage, null, host); }
            stage = So6120MetadataStage.Smbios;
            Version? ec = bootReportedEcVersion;
            if (ec is null)
            {
                uint size = GetSystemFirmwareTable(RawSmbiosProvider, 0, null, 0);
                if (size is < 8 or > MaxSmbiosBytes) { return new(null, stage, size == 0 ? Marshal.GetLastWin32Error() : null); }
                byte[] smbios = new byte[size];
                if (GetSystemFirmwareTable(RawSmbiosProvider, 0, smbios, size) != size)
                { return new(null, stage, Marshal.GetLastWin32Error()); }
                ec = ReadReportedEcVersion(smbios);
            }
            stage = So6120MetadataStage.Hid;
            var hid = ReadHidMetadata(ReadPresentHidPaths(), new WindowsHidNative(), serviceContract);
            stage = So6120MetadataStage.ConsoleAndPower;
            So6120SessionMetadata session = ReadSessionMetadata();
            uint console = session.ConsoleSession;
            stage = So6120MetadataStage.Driver;
            var driver = ReadDriverMetadata(registry, driverPin);
            stage = So6120MetadataStage.Controllers;
            var controllers = ReadControllerMetadata();
            var stamp = new So6120AdmissionStamp(runId, console, clock, clock.GetTimestamp());
            return new(new(stamp, started, RuntimeInformation.ProcessArchitecture, host, ec, hid, session, driver,
                controllers.Detected, controllers.OpenBlade, controllers.WriterPids), null, null);
        }
        catch (Exception exception) { return new(null, stage, exception.HResult); }
    }

    // Continuous monitoring has no command authority. Avoid SMBIOS, HID opens,
    // file hashing and filesystem ACL walks while the owner is simply idle.
    internal static So6120ServiceVolatileMetadata CaptureServiceVolatile(TimeProvider clock)
    {
        long started = clock.GetTimestamp();
        So6120SessionMetadata session = ReadSessionMetadata();
        var controllers = ReadControllerMetadata();
        return new(started, clock.GetTimestamp(), session, controllers.Detected, controllers.OpenBlade, controllers.WriterPids);
    }

    internal static So6120SessionMetadata ReadSessionMetadata()
    {
        if (!GetSystemPowerStatus(out var power))
        { throw new IOException("System session/power metadata is unavailable.", Marshal.GetLastWin32Error()); }
        using var process = Process.GetCurrentProcess();
        return new(Environment.UserInteractive, process.SessionId, WTSGetActiveConsoleSessionId(), power.AcLineStatus);
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

    internal static IReadOnlyList<So6120HidMetadata> ReadHidMetadata(IReadOnlyList<string> paths, IHidCandidateNative native,
        FanStopModelContract? serviceContract = null)
    {
        List<So6120HidMetadata> candidates = [];
        // Developer capture retains its exact 0581 boundary. The installed
        // owner supplies its separately reviewed immutable model contract.
        foreach (string path in paths.Where(path => serviceContract is null
                     ? So6120SupervisedAdmission.IsTargetHidPath(path) : serviceContract.IsTargetHidPath(path)))
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

    private static So6120DriverMetadata ReadDriverMetadata(IRegistryValueSource registry, FileStream? driverPin = null)
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
        long size;
        string? hash;
        if (driverPin is not null)
        {
            // Only the shared reviewed acquisition supplies this live pin. Its
            // FileShare.Read contract prevents writes and path replacement.
            if (driverPin.SafeFileHandle.IsClosed || driverPin.SafeFileHandle.IsInvalid
                || !string.Equals(driverPin.Name, expected, StringComparison.OrdinalIgnoreCase))
            { throw new IOException("The admitted driver file pin is no longer held."); }
            size = driverPin.Length;
            hash = size == So6120SupervisedAdmission.DriverBytes ? So6120SupervisedAdmission.DriverHash : null;
        }
        else
        {
            using var file = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
            size = file.Length;
            hash = size == So6120SupervisedAdmission.DriverBytes ? Convert.ToHexString(SHA256.HashData(file)) : null;
        }
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
