using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

internal sealed record CpuPmDeviceDriverRecord(string Name, uint Type, uint State, string Image);
internal readonly record struct CpuPmActiveDriverEvidence(bool Complete, int MatchingActiveDrivers);
internal interface ICpuPmDeviceDriverSource { IEnumerable<CpuPmDeviceDriverRecord> ReadAll(); }
internal static class CpuPmActiveDriverScan
{
    internal const int MaximumDrivers = 4096;
    internal static CpuPmActiveDriverEvidence Read(ICpuPmDeviceDriverSource source)
    {
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int count = 0, active = 0;
            foreach (var driver in source.ReadAll())
            {
                if (++count > MaximumDrivers || driver is null || string.IsNullOrWhiteSpace(driver.Name) || driver.Name.Length > 256
                    || driver.Name.IndexOfAny(['\\', '/', '\0']) >= 0 || !seen.Add(driver.Name)
                    || driver.Type is not (1 or 2 or 4 or 8) || driver.State is < 1 or > 7
                    || string.IsNullOrWhiteSpace(driver.Image) || driver.Image.Length > 32768 || driver.Image.Contains('\0')) { return new(false, -1); }
                string inspectedImage = driver.Image;
                if (inspectedImage.StartsWith("%SystemRoot%", StringComparison.OrdinalIgnoreCase)) { inspectedImage = inspectedImage[12..]; }
                if (inspectedImage.Contains('%')) { return new(false, -1); }
                // Catch the exact service and aliases/other versions using the same
                // AMD driver family. An alternate image/name is never adopted.
                bool candidate = driver.Name.Contains("AMDRyzenMasterDriver", StringComparison.OrdinalIgnoreCase)
                    || driver.Image.Contains("AMDRyzenMasterDriver", StringComparison.OrdinalIgnoreCase)
                    || driver.Image.Equals(WindowsCpuPmDriverRegistration.Image, StringComparison.OrdinalIgnoreCase);
                if (candidate && driver.Type != 1) { return new(false, -1); }
                if (candidate && driver.State != 1) { active++; }
            }
            return new(true, active);
        }
        // Includes errors while advancing or disposing the complete enumeration.
        catch (Exception) { return new(false, -1); }
    }
}

// A single successful bounded SCM call plus an independently complete Registry64
// driver inventory is required. .NET GetDevices does not verify native completion.
internal readonly record struct CpuPmDriverEnumeration(bool Success, int Error, uint Remaining, uint Count, uint Resume);
internal sealed record CpuPmRegistryDriver(string Name, uint Type, string Image);
internal readonly record struct CpuPmRegistryName(int Status, string Name, uint Length);
internal interface ICpuPmDriverInventoryPlatform
{
    CpuPmDriverEnumeration Enumerate(nint buffer, int bytes);
    IReadOnlyList<CpuPmRegistryDriver> ReadRegistryDrivers();
}
internal sealed class WindowsCpuPmDeviceDriverSource : ICpuPmDeviceDriverSource
{
    internal const int BufferBytes = 256 * 1024;
    private readonly ICpuPmDriverInventoryPlatform platform;
    internal WindowsCpuPmDeviceDriverSource() : this(new NativeInventory()) { }
    internal WindowsCpuPmDeviceDriverSource(ICpuPmDriverInventoryPlatform platform) => this.platform = platform;
    public IEnumerable<CpuPmDeviceDriverRecord> ReadAll()
    {
        nint buffer = Marshal.AllocHGlobal(BufferBytes);
        try
        {
            Marshal.Copy(new byte[BufferBytes], 0, buffer, BufferBytes);
            var result = platform.Enumerate(buffer, BufferBytes);
            int stride = Marshal.SizeOf<NativeEntry>();
            if (!result.Success || result.Error != 0 || result.Remaining != 0 || result.Resume != 0
                || result.Count > CpuPmActiveDriverScan.MaximumDrivers || result.Count > BufferBytes / stride)
            { throw new IOException("Driver inventory incomplete."); }
            var entries = new Dictionary<string, NativeEntry>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < result.Count; index++)
            {
                var entry = Marshal.PtrToStructure<NativeEntry>(buffer + index * stride);
                string name = ReadString(buffer, entry.Name, checked((int)result.Count * stride));
                _ = ReadString(buffer, entry.DisplayName, checked((int)result.Count * stride));
                if (!entries.TryAdd(name, entry) || entry.Type is not (1 or 2 or 4 or 8) || entry.State is < 1 or > 7)
                { throw new IOException("Driver inventory record denied."); }
            }
            var registry = platform.ReadRegistryDrivers();
            if (registry.Count != entries.Count || registry.Count > CpuPmActiveDriverScan.MaximumDrivers)
            { throw new IOException("Driver inventory visibility mismatch."); }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var records = new List<CpuPmDeviceDriverRecord>();
            foreach (var value in registry)
            {
                if (!seen.Add(value.Name) || !entries.TryGetValue(value.Name, out var entry) || entry.Type != value.Type)
                { throw new IOException("Driver inventory visibility mismatch."); }
                records.Add(new(value.Name, entry.Type, entry.State, value.Image));
            }
            return records.AsReadOnly();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string ReadString(nint allocation, nint pointer, int tableBytes)
    {
        long offset = pointer.ToInt64() - allocation.ToInt64();
        if (offset < tableBytes || offset >= BufferBytes || (offset & 1) != 0) { throw new IOException("Driver inventory pointer denied."); }
        for (int chars = 0; chars <= 256 && offset + chars * 2 + 2 <= BufferBytes; chars++)
        {
            if (Marshal.ReadInt16(pointer, chars * 2) == 0)
            { return chars == 0 ? throw new IOException("Driver inventory string empty.") : Marshal.PtrToStringUni(pointer, chars)!; }
        }
        throw new IOException("Driver inventory string bound.");
    }
    internal static IReadOnlyList<string> ReadRegistryNames(Func<uint, CpuPmRegistryName> read)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            var reply = read(index);
            if (reply.Status == 259) { return names.AsReadOnly(); }
            if (reply.Status != 0 || index >= 16384 || reply.Length is 0 or > 256
                || reply.Name.Length != reply.Length || !seen.Add(reply.Name))
            { throw new IOException("Service inventory incomplete."); }
            names.Add(reply.Name);
        }
    }
    // x64 ENUM_SERVICE_STATUS_PROCESSW: two pointers + SERVICE_STATUS_PROCESS
    // (nine DWORDs), trailing alignment padding; size 56, Type offset 16.
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeEntry
    {
        internal nint Name, DisplayName;
        internal uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags;
    }
    private sealed class NativeInventory : ICpuPmDriverInventoryPlatform
    {
        public CpuPmDriverEnumeration Enumerate(nint buffer, int bytes)
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) { throw new PlatformNotSupportedException(); }
            nint manager = OpenSCManagerW(null, null, 4);
            if (manager == 0) { throw new IOException("Driver inventory SCM unavailable."); }
            try
            {
                uint resume = 0;
                bool success = EnumServicesStatusExW(manager, 0, 0xB, 3, buffer, (uint)bytes, out uint remaining, out uint count, ref resume, null);
                int error = success ? 0 : Marshal.GetLastWin32Error();
                return new(success, error, remaining, count, resume);
            }
            finally { if (!CloseServiceHandle(manager)) { throw new IOException("Driver inventory SCM cleanup failed."); } }
        }
        public IReadOnlyList<CpuPmRegistryDriver> ReadRegistryDrivers()
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", false)
                ?? throw new IOException("Driver inventory registry unavailable.");
            var names = ReadRegistryNames(index =>
            {
                var name = new StringBuilder(257); uint capacity = 257;
                int status = RegEnumKeyExW(services.Handle, index, name, ref capacity, 0, 0, 0, 0);
                return new(status, name.ToString(), capacity);
            });
            var drivers = new List<CpuPmRegistryDriver>();
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.IndexOfAny(['\\', '/', '\0']) >= 0)
                { throw new IOException("Driver inventory name denied."); }
                using var key = services.OpenSubKey(name, false) ?? throw new IOException("Service inventory changed.");
                if (key.GetValue("Type") is not int type) { throw new IOException("Service inventory type unknown."); }
                if ((type & 0xF) == 0) { continue; }
                if (type is not (1 or 2 or 4 or 8) || drivers.Count >= CpuPmActiveDriverScan.MaximumDrivers
                    || key.GetValueKind("ImagePath") is not (RegistryValueKind.String or RegistryValueKind.ExpandString)
                    || key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string image)
                { throw new IOException("Driver inventory image unavailable."); }
                drivers.Add(new(name, (uint)type, image));
            }
            return drivers.AsReadOnly();
        }
        // https://learn.microsoft.com/windows/win32/api/winsvc/nf-winsvc-enumservicesstatusexw
        // Success requires resume zero. Access-denied services may be silently
        // omitted; exact independent Registry64 name/type agreement detects this.
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumServicesStatusExW(nint manager, uint level, uint type, uint state,
            nint buffer, uint size, out uint needed, out uint count, ref uint resume, string? group);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(nint service);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegEnumKeyExW(SafeRegistryHandle key, uint index, StringBuilder name,
            ref uint length, nint reserved, nint className, nint classLength, nint lastWriteTime);
    }
}
// Actual fixed native composition. Its constructor performs no API calls. Only
// the admitted lifecycle reducer may call these operations; Program has no caller.
internal sealed class WindowsCpuPmDriverLifecyclePlatform : ICpuPmDriverLifecyclePlatform
{
    public ICpuPmDriverMutex AcquireMutex(CancellationToken token) => WindowsCpuPmDriverMutex.Acquire(token);
    public ICpuPmCreatedDriver? CreateFixedDriver() => WindowsCpuPmDriverRegistration.CreateFixed();
    public CpuPmDriverAbsence ReadIndependentAbsence()
    {
        int scm = WindowsCpuPmDriverRegistration.ReadScmAbsenceError();
        bool registry = WindowsCpuPmDriverRegistration.ReadRegistry64Absence();
        var active = CpuPmActiveDriverScan.Read(new WindowsCpuPmDeviceDriverSource());
        return new(scm, registry, active.Complete, active.MatchingActiveDrivers);
    }
}
