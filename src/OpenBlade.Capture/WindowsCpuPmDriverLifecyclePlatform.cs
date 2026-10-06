using System.Runtime.InteropServices;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record CpuPmDeviceDriverRecord(string Name, uint Type, uint State, string Image)
{
    internal CpuServiceImageProvenance ImageProvenance { get; init; }
}
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
// SCM-controlled driver inventory is required. This is configured-path evidence,
// not universal loaded-kernel enumeration. .NET GetDevices does not verify completion.
internal readonly record struct CpuPmDriverEnumeration(bool Success, int Error, uint Remaining, uint Count, uint Resume);
internal sealed record CpuPmRegistryDriver(string Name, uint Type, string Image)
{
    internal CpuServiceImageProvenance ImageProvenance { get; init; }
}
internal readonly record struct CpuPmRegistryName(int Status, string Name, uint Length);
internal interface ICpuPmDriverInventoryPlatform
{
    CpuPmDriverEnumeration Enumerate(nint buffer, int bytes);
    IReadOnlyList<CpuPmRegistryDriver> ReadRegistryDrivers();
}
internal sealed class WindowsCpuPmDeviceDriverSource : ICpuPmDeviceDriverSource
{
    internal const int BufferBytes = CpuServiceInventory.BufferBytes;
    private readonly ICpuPmDriverInventoryPlatform platform;
    internal WindowsCpuPmDeviceDriverSource() : this(new NativeInventory()) { }
    internal WindowsCpuPmDeviceDriverSource(ICpuPmDriverInventoryPlatform platform) => this.platform = platform;
    public IEnumerable<CpuPmDeviceDriverRecord> ReadAll() => new CpuServiceInventory(CpuServiceInventoryKind.Drivers,
        new Adapter(platform)).ReadAll().Select(value => new CpuPmDeviceDriverRecord(value.Name, value.Type, value.State, value.Image)
        { ImageProvenance = value.ImageProvenance });
    internal static IReadOnlyList<string> ReadRegistryNames(Func<uint, CpuPmRegistryName> read) =>
        CpuServiceInventory.ReadRegistryNames(index => { var value = read(index); return new(value.Status, value.Name, value.Length); });
    // Retained fake-buffer ABI for existing regression fixtures. Production uses
    // the shared Protocol NativeEntry parser and native provider exclusively.
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeEntry
    {
        internal nint Name, DisplayName;
        internal uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags;
    }
    private sealed class Adapter(ICpuPmDriverInventoryPlatform inner) : ICpuServiceInventoryPlatform
    {
        public uint ReadConfiguredType(string name) => throw new IOException("Driver type must match its native status exactly.");
        public CpuServiceTemplateStatus ReadUnenumeratedTemplateStatus(string name) => throw new IOException("Driver inventory cannot query user templates.");
        public CpuServiceEnumeration Enumerate(nint buffer, int bytes)
        { var value = inner.Enumerate(buffer, bytes); return new(value.Success, value.Error, value.Remaining, value.Count, value.Resume); }
        public IReadOnlyList<CpuServiceRegistryEntry> ReadRegistryEntries() => inner.ReadRegistryDrivers()
            .Select(value => new CpuServiceRegistryEntry(value.Name, value.Type, value.Image) { ImageProvenance = value.ImageProvenance }).ToArray();
    }
    private sealed class NativeInventory : ICpuPmDriverInventoryPlatform
    {
        private readonly CpuServiceInventory.NativeInventory inner = new(CpuServiceInventoryKind.Drivers);
        public CpuPmDriverEnumeration Enumerate(nint buffer, int bytes)
        { var value = inner.Enumerate(buffer, bytes); return new(value.Success, value.Error, value.Remaining, value.Count, value.Resume); }
        public IReadOnlyList<CpuPmRegistryDriver> ReadRegistryDrivers() => inner.ReadRegistryEntries()
            .Select(value => new CpuPmRegistryDriver(value.Name, value.Type, value.Image) { ImageProvenance = value.ImageProvenance }).ToArray();
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
