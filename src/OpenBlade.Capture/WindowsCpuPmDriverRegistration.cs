using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// Narrow primitives only. No lifecycle platform composition, active-driver scan,
// host/pin provider, journal, launcher or Program caller is supplied by this file.
// Never reopen/adopt a service as the owned registration. Raw SCM handles have no
// finalizer: uncertain calls retain their object (and the external safety pins).
internal sealed class WindowsCpuPmDriverRegistration : ICpuPmCreatedDriver
{
    internal const string Name = Rz090528InstalledCpuBackendInspector.ServiceName;
    internal const string Image = @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath;
    internal const string RegistryPath = @"SYSTEM\CurrentControlSet\Services\" + Name;
    internal const string OwnerValueName = "OpenBladePmOwner";
    internal const uint CreatedAccess = 0x0001 | 0x0004 | 0x0010 | 0x0020 | 0x00010000;
    internal const int ConfigurationBufferBytes = 8192;
    private static int creationAttempted;
    private static WindowsCpuPmDriverRegistration? rooted;
    private nint manager, service;
    private RegistryKey? ownerKey;
    private int tagAttempted, startAttempted, stopAttempted, deleteAttempted;
    private bool deleteReturned;
    private WindowsCpuPmDriverRegistration() { }

    // One allocation per process, rooted before the first possibly blocking call.
    // Only a future fully admitted reducer/platform may call this fixed factory.
    internal static WindowsCpuPmDriverRegistration? CreateFixed()
    {
        RequireWindowsX64();
        if (Interlocked.CompareExchange(ref creationAttempted, 1, 0) != 0) { throw new InvalidOperationException("Driver creation already attempted."); }
        var value = new WindowsCpuPmDriverRegistration(); rooted = value;
        value.manager = OpenSCManager(null, null, 0x0001 | 0x0002);
        if (value.manager == 0) { return null; }
        value.service = CreateService(value.manager, Name, Name, CreatedAccess, 1, 3, 1, Image, null, 0, null, null, null);
        if (value.service == 0) { return null; }
        // No registry write has occurred yet; a missing key is an admission error.
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        value.ownerKey = machine.OpenSubKey(RegistryPath, writable: true);
        return value;
    }
    internal static int ReadScmAbsenceError()
    {
        RequireWindowsX64();
        nint manager = OpenSCManager(null, null, 1);
        if (manager == 0) { throw new IOException("PM SCM manager unavailable."); }
        try
        {
            nint existing = OpenService(manager, Name, 4);
            int error = existing == 0 ? Marshal.GetLastWin32Error() : 0;
            if (existing != 0 && !CloseServiceHandle(existing)) { throw new IOException("SCM handle close failed."); }
            return error;
        }
        finally { if (!CloseServiceHandle(manager)) { throw new IOException("SCM manager close failed."); } }
    }
    internal static bool ReadRegistry64Absence()
    {
        RequireWindowsX64();
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(RegistryPath, writable: false);
        return key is null;
    }
    public CpuPmDriverSnapshot ReadFresh()
    {
        if (service == 0 || ownerKey is null) { return new(false, false, false, null, CpuPmDriverState.Unknown); }
        byte[] config = new byte[ConfigurationBufferBytes]; Array.Fill(config, (byte)0xFF);
        nint buffer = Marshal.AllocHGlobal(config.Length);
        bool configuration;
        try
        {
            Marshal.Copy(config, 0, buffer, config.Length);
            if (!QueryServiceConfig(service, buffer, checked((uint)config.Length), out _)) { throw new IOException("SCM configuration unavailable."); }
            // pcbBytesNeeded is documented only on insufficient-buffer failure.
            // Validate every pointer against our complete fixed allocation instead.
            Marshal.Copy(buffer, config, 0, config.Length);
            configuration = ConfigurationMatches(config, unchecked((ulong)buffer.ToInt64()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
        if (!QueryServiceStatusEx(service, 0, out var status, 36, out _)) { throw new IOException("SCM status unavailable."); }
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var current = machine.OpenSubKey(RegistryPath, writable: false);
        string? heldTag = ReadTag(ownerKey), currentTag = current is null ? null : ReadTag(current);
        bool registry = current is not null && RegistryMatches(current) && RegistryMatches(ownerKey) && currentTag == heldTag;
        var state = status.ServiceType == 1 && status.CurrentState is 1 or 3 or 4 ? (CpuPmDriverState)status.CurrentState : CpuPmDriverState.Unknown;
        return new(true, configuration, registry, heldTag, state);
    }
    public bool WriteNewOwnerTagAndFlush(CpuPmDriverOwnerTag tag)
    {
        if (service == 0 || ownerKey is null || Interlocked.CompareExchange(ref tagAttempted, 1, 0) != 0) { return false; }
        if (ownerKey.GetValueNames().Contains(OwnerValueName, StringComparer.OrdinalIgnoreCase)) { return false; }
        ownerKey.SetValue(OwnerValueName, tag.Value, RegistryValueKind.String);
        ownerKey.Flush();
        return ReadTag(ownerKey) == tag.Value;
    }
    public bool StartOnce() => service != 0 && Interlocked.CompareExchange(ref startAttempted, 1, 0) == 0 && StartService(service, 0, 0);
    public bool StopOnce() => service != 0 && Interlocked.CompareExchange(ref stopAttempted, 1, 0) == 0 && ControlService(service, 1, out _);
    public bool DeleteOnce()
    {
        if (service == 0 || Interlocked.CompareExchange(ref deleteAttempted, 1, 0) != 0) { return false; }
        deleteReturned = DeleteService(service); return deleteReturned;
    }
    public void CloseAfterDelete()
    {
        if (!deleteReturned) { throw new InvalidOperationException("Owned delete has not returned."); }
        ownerKey?.Dispose(); ownerKey = null;
        if (service != 0) { if (!CloseServiceHandle(service)) { throw new IOException("Owned service close failed."); } service = 0; }
        if (manager != 0) { if (!CloseServiceHandle(manager)) { throw new IOException("Owned manager close failed."); } manager = 0; }
        GC.KeepAlive(rooted); // Retain the single-use root; no second creation ever.
    }
    private static string? ReadTag(RegistryKey key)
    {
        if (!key.GetValueNames().Contains(OwnerValueName, StringComparer.OrdinalIgnoreCase)) { return null; }
        if (key.GetValueKind(OwnerValueName) != RegistryValueKind.String) { throw new IOException("Owner tag kind changed."); }
        return key.GetValue(OwnerValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string text && text.Length <= 256
            ? text : throw new IOException("Owner tag shape changed.");
    }
    private static bool RegistryMatches(RegistryKey key)
    {
        bool Dword(string name, int expected) => key.GetValueKind(name) == RegistryValueKind.DWord && key.GetValue(name) is int value && value == expected;
        bool EmptyOptional(string name, RegistryValueKind kind)
        {
            if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) { return true; }
            if (key.GetValueKind(name) != kind) { return false; }
            return key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch { string s => s.Length == 0, string[] a => a.Length == 0, int i => i == 0, _ => false };
        }
        return Dword("Type", 1) && Dword("Start", 3) && Dword("ErrorControl", 1)
            && key.GetValueKind("ImagePath") == RegistryValueKind.ExpandString
            && key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string image && image == Image
            && key.GetValueKind("DisplayName") == RegistryValueKind.String && key.GetValue("DisplayName") is string display && display == Name
            && EmptyOptional("Group", RegistryValueKind.String) && EmptyOptional("ObjectName", RegistryValueKind.String)
            && EmptyOptional("Tag", RegistryValueKind.DWord) && EmptyOptional("DependOnGroup", RegistryValueKind.MultiString)
            && EmptyOptional("DependOnService", RegistryValueKind.MultiString);
    }

    // Pure x64 parser. No PtrToString or dereference of an unvalidated SCM pointer.
    internal static bool ConfigurationMatches(ReadOnlySpan<byte> bytes, ulong address)
    {
        if (bytes.Length is < 64 or > ConfigurationBufferBytes || address == 0 || address > ulong.MaxValue - (ulong)bytes.Length) { return false; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 3
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]) != 0) { return false; }
        return Text(bytes, address, 16, false, out string? image) && image == Image
            && Text(bytes, address, 24, true, out string? group) && string.IsNullOrEmpty(group)
            && EmptyDependencies(bytes, address, 40)
            && Text(bytes, address, 48, true, out string? account) && string.IsNullOrEmpty(account)
            && Text(bytes, address, 56, false, out string? display) && display == Name;
    }
    private static bool PointerOffset(ReadOnlySpan<byte> bytes, ulong address, int field, out int offset)
    {
        ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(bytes[field..]);
        offset = 0;
        if (pointer < address || pointer - address < 64 || pointer - address >= (ulong)bytes.Length || ((pointer - address) & 1) != 0) { return false; }
        offset = checked((int)(pointer - address)); return true;
    }
    private static bool Text(ReadOnlySpan<byte> bytes, ulong address, int field, bool allowNull, out string? text)
    {
        text = null;
        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes[field..]) == 0) { return allowNull; }
        if (!PointerOffset(bytes, address, field, out int offset)) { return false; }
        for (int end = offset; end + 1 < bytes.Length; end += 2)
        {
            if (bytes[end] != 0 || bytes[end + 1] != 0) { continue; }
            try { text = new UnicodeEncoding(false, false, true).GetString(bytes[offset..end]); return true; }
            catch (DecoderFallbackException) { return false; }
        }
        return false;
    }
    private static bool EmptyDependencies(ReadOnlySpan<byte> bytes, ulong address, int field)
    {
        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes[field..]) == 0) { return true; }
        return PointerOffset(bytes, address, field, out int offset) && offset <= bytes.Length - 4
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) == 0;
    }
    private static void RequireWindowsX64()
    { if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) { throw new PlatformNotSupportedException(); } }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryConfig
    {
        internal uint Type, Start, Error;
        internal nint BinaryPath, Group;
        internal uint Tag;
        internal nint Dependencies, Account, Display;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    { internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusProcess
    { internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }

    // Fixed DWORD/SC_HANDLE/BOOL ABI. SCM handles use CloseServiceHandle.
    // https://learn.microsoft.com/windows/win32/api/winsvc/nf-winsvc-createservicew
    // https://learn.microsoft.com/windows/win32/api/winsvc/ns-winsvc-query_service_configw
    // https://learn.microsoft.com/windows/win32/api/winsvc/nf-winsvc-queryserviceconfigw
    // https://learn.microsoft.com/windows/win32/api/winsvc/nf-winsvc-startservicew
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateService(nint manager, string name, string display, uint access, uint type, uint start, uint error,
        string image, string? group, nint tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(nint service, uint argc, nint argv);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(nint service, uint command, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(nint service);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(nint service, nint buffer, uint bytes, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(nint service, uint level, out ServiceStatusProcess status, uint bytes, out uint needed);
}

internal sealed class WindowsCpuPmDriverMutex : ICpuPmDriverMutex
{
    private readonly Mutex mutex = new(false, @"Global\AMDRyzenMasterMutex");
    private readonly int thread = Environment.CurrentManagedThreadId;
    private bool owned, closed;
    public bool WasAbandoned { get; private set; }
    public bool IsOwnedByCurrentThread => owned && !closed && thread == Environment.CurrentManagedThreadId;
    private WindowsCpuPmDriverMutex() { }
    internal static WindowsCpuPmDriverMutex Acquire(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var value = new WindowsCpuPmDriverMutex();
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(2))
            {
                token.ThrowIfCancellationRequested();
                try { value.owned = value.mutex.WaitOne(TimeSpan.FromMilliseconds(50)); }
                catch (AbandonedMutexException) { value.owned = true; value.WasAbandoned = true; }
                if (value.owned) { break; }
            }
            token.ThrowIfCancellationRequested(); return value;
        }
        catch { value.ReleaseAndCloseOnCurrentThread(); throw; }
    }
    public void ReleaseAndCloseOnCurrentThread()
    {
        if (closed) { return; }
        if (thread != Environment.CurrentManagedThreadId) { throw new InvalidOperationException("Mutex owner thread required."); }
        if (owned) { mutex.ReleaseMutex(); owned = false; }
        mutex.Dispose(); closed = true;
    }
}
