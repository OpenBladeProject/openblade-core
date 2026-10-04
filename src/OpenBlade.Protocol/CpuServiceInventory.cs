using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Protocol;

internal enum CpuServiceInventoryKind { Drivers, Win32 }
internal enum CpuServiceRegistryDisposition { RequireScmEntry, RegistryOnlyRecognizer }
internal enum CpuServiceImageProvenance { None, RegistryConfigured, ScmConfigured, DefaultDriverPath }
internal sealed record CpuServiceInventoryEntry(string Name, string DisplayName, uint Type, uint State, string Image)
{
    internal uint ConfiguredType { get; init; } = Type;
    internal CpuServiceImageProvenance ImageProvenance { get; init; }
}
// Single bounded SCM-controlled inventory + completed Registry64 crosscheck.
// Driver paths describe configuration, not a universal loaded-kernel inventory.
// No retry, paging, mutation, or assumption that an omitted service is stopped.
internal readonly record struct CpuServiceEnumeration(bool Success, int Error, uint Remaining, uint Count, uint Resume);
internal sealed record CpuServiceRegistryEntry(string Name, uint Type, string Image)
{
    internal CpuServiceImageProvenance ImageProvenance { get; init; }
}
internal readonly record struct CpuServiceRegistryName(int Status, string Name, uint Length);
internal readonly record struct CpuServiceTemplateStatus(uint Type, uint State);
internal interface ICpuServiceInventoryPlatform
{
    CpuServiceEnumeration Enumerate(nint buffer, int bytes);
    IReadOnlyList<CpuServiceRegistryEntry> ReadRegistryEntries();
    CpuServiceTemplateStatus ReadUnenumeratedTemplateStatus(string name);
    uint ReadConfiguredType(string name);
}
internal sealed class CpuServiceInventory
{
    internal const int BufferBytes = 256 * 1024, MaximumEntries = 4096;
    private readonly CpuServiceInventoryKind kind;
    private readonly ICpuServiceInventoryPlatform platform;
    internal CpuServiceInventory(CpuServiceInventoryKind kind) : this(kind, new NativeInventory(kind)) { }
    internal CpuServiceInventory(CpuServiceInventoryKind kind, ICpuServiceInventoryPlatform platform)
    {
        if (kind is not (CpuServiceInventoryKind.Drivers or CpuServiceInventoryKind.Win32)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        this.kind = kind; this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }
    public IReadOnlyList<CpuServiceInventoryEntry> ReadAll() => Read(null);

    // Getter-only activity check against a completed full inventory. Configuration
    // is verified again before accepting the getter; names/types/display and live
    // status stay fresh, including omitted registry-backed service templates.
    internal IReadOnlyList<CpuServiceInventoryEntry> ReadGetterActivity(IReadOnlyList<CpuServiceInventoryEntry> baseline) =>
        kind == CpuServiceInventoryKind.Win32 ? Read(baseline) : throw new InvalidOperationException();

    private IReadOnlyList<CpuServiceInventoryEntry> Read(IReadOnlyList<CpuServiceInventoryEntry>? baseline)
    {
        nint buffer = Marshal.AllocHGlobal(BufferBytes);
        try
        {
            Marshal.Copy(new byte[BufferBytes], 0, buffer, BufferBytes);
            var result = platform.Enumerate(buffer, BufferBytes);
            int stride = Marshal.SizeOf<NativeEntry>();
            if (!result.Success || result.Error != 0 || result.Remaining != 0 || result.Resume != 0
                || result.Count > MaximumEntries || result.Count > BufferBytes / stride)
            { throw new IOException("Driver inventory incomplete."); }
            var entries = new Dictionary<string, (NativeEntry Native, string Display)>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < result.Count; index++)
            {
                var entry = Marshal.PtrToStructure<NativeEntry>(buffer + index * stride);
                string name = ReadString(buffer, entry.Name, checked((int)result.Count * stride));
                string display = ReadString(buffer, entry.DisplayName, checked((int)result.Count * stride));
                if (!entries.TryAdd(name, (entry, display)) || !RuntimeBelongs(kind, entry.Type) || entry.State is < 1 or > 7)
                { throw new IOException("Driver inventory record denied."); }
            }
            if (baseline is not null)
            {
                if (baseline.Count > MaximumEntries) { throw new IOException("Getter inventory bound."); }
                var activity = new List<CpuServiceInventoryEntry>(baseline.Count);
                var seenBaseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prior in baseline)
                {
                    if (!seenBaseline.Add(prior.Name)) { throw new IOException("Getter inventory duplicate."); }
                    if (!entries.TryGetValue(prior.Name, out var current))
                    {
                        if (prior.ConfiguredType is not (0x50 or 0x60)) { throw new IOException("Getter service disappeared."); }
                        var status = platform.ReadUnenumeratedTemplateStatus(prior.Name);
                        if (status.Type != prior.Type || status.State != 1) { throw new IOException("Getter template changed."); }
                        current = (new NativeEntry { Type = status.Type, State = status.State }, prior.DisplayName);
                    }
                    else { entries.Remove(prior.Name); }
                    if (current.Native.Type != prior.Type || current.Display != prior.DisplayName
                        || !TypesAgree(kind, prior.ConfiguredType, current.Native.Type))
                    { throw new IOException("Getter service identity changed."); }
                    if ((Rz090528CpuControllerPresence.IsGetterControllerName(prior.Name)
                        || Rz090528CpuControllerPresence.IsGetterControllerName(prior.DisplayName))
                        && platform.ReadConfiguredType(prior.Name) != prior.ConfiguredType)
                    { throw new IOException("Getter controller configuration changed."); }
                    activity.Add(prior with { State = current.Native.State });
                }
                if (entries.Count != 0) { throw new IOException("Getter service inventory changed."); }
                return activity.AsReadOnly();
            }
            var registry = platform.ReadRegistryEntries();
            if (registry.Count < entries.Count || registry.Count > MaximumEntries)
            { throw new IOException("Driver inventory visibility mismatch."); }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var records = new List<CpuServiceInventoryEntry>();
            foreach (var value in registry)
            {
                if (kind == CpuServiceInventoryKind.Win32 && value.Type is 0x50 or 0x60 && !entries.ContainsKey(value.Name))
                {
                    // Microsoft documents registry templates omitted from the
                    // service list. Never infer inactive from their type: query
                    // the exact named object independently and require stopped.
                    var status = platform.ReadUnenumeratedTemplateStatus(value.Name);
                    if (status.Type != value.Type || status.State != 1) { throw new IOException("Service template status unavailable."); }
                    entries.Add(value.Name, (new NativeEntry { Type = status.Type, State = status.State }, value.Name));
                }
                if (!seen.Add(value.Name) || !entries.TryGetValue(value.Name, out var entry)
                    || !TypesAgree(kind, value.Type, entry.Native.Type)
                    || kind == CpuServiceInventoryKind.Win32 && value.Type != entry.Native.Type
                        && platform.ReadConfiguredType(value.Name) != value.Type)
                { throw new IOException("Driver inventory visibility mismatch."); }
                records.Add(new(value.Name, entry.Display, entry.Native.Type, entry.Native.State, value.Image)
                { ConfiguredType = value.Type, ImageProvenance = value.ImageProvenance });
            }
            if (entries.Count != registry.Count) { throw new IOException("Service inventory visibility mismatch."); }
            return records.AsReadOnly();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    // Per-user flags are preserved, never masked off or treated as stopped.
    // Exact registry/native agreement remains mandatory for templates/instances.
    // SERVICE_PKG_SERVICE=0x200 is documented in Microsoft's WinSDK winnt.h.
    // Preserve it in exact status/registry comparisons; never mask it away.
    internal static bool KnownType(uint type) => type is 1 or 2 or 4 or 8 or 0x10 or 0x20 or 0x110 or 0x120
        or 0x50 or 0x60 or 0xD0 or 0xE0 or 0x210 or 0x220;
    internal static bool Belongs(CpuServiceInventoryKind kind, uint type) => KnownType(type)
        && (kind == CpuServiceInventoryKind.Drivers ? type is 1 or 2 or 4 or 8 : (type & 0x30) != 0);
    internal static bool RuntimeBelongs(CpuServiceInventoryKind kind, uint type) => Belongs(kind, type)
        || kind == CpuServiceInventoryKind.Win32 && type is 0x30 or 0xF0;
    internal static bool TypesAgree(CpuServiceInventoryKind kind, uint configured, uint runtime) => Belongs(kind, configured)
        && (configured == runtime || kind == CpuServiceInventoryKind.Win32 && configured is 0x20 or 0xE0 && runtime == (configured | 0x10));
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
    internal static IReadOnlyList<string> ReadRegistryNames(Func<uint, CpuServiceRegistryName> read)
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
    internal static bool ExcludeNonServiceKey(object? registryType, Func<int> openServiceError)
    {
        if (registryType is int) { return false; }
        // Only truly missing Type plus a successful SCM connection followed by
        // exact named OpenService ERROR_SERVICE_DOES_NOT_EXIST can exclude it.
        // Wrong type, inaccessible key, manager failure or visible service denies.
        if (registryType is null && openServiceError() == 1060) { return true; }
        throw new IOException("Registry entry service classification unavailable.");
    }
    internal static object? DecodeRegistryValue(int status, uint kind, ReadOnlySpan<byte> data, uint bytes, bool image)
    {
        if (status == 2) { return null; }
        if (status != 0 || bytes > data.Length) { throw new IOException("Registry value unavailable."); }
        if (!image)
        {
            if (kind != 4 || bytes != 4) { throw new IOException("Registry Type malformed."); }
            return BinaryPrimitives.ReadInt32LittleEndian(data);
        }
        if (kind is not (1 or 2) || bytes is < 2 or > 65536 || (bytes & 1) != 0
            || data[(int)bytes - 2] != 0 || data[(int)bytes - 1] != 0)
        { throw new IOException("Registry ImagePath malformed."); }
        string value;
        try { value = new UnicodeEncoding(false, false, true).GetString(data[..((int)bytes - 2)]); }
        catch (DecoderFallbackException) { throw new IOException("Registry ImagePath malformed."); }
        if (value.Contains('\0')) { throw new IOException("Registry ImagePath malformed."); }
        return value;
    }
    internal static CpuServiceRegistryDisposition ClassifyRecognizer(uint type, object? decodedImage, Func<int> openServiceError)
    {
        if (type != 8 || decodedImage is not null) { return CpuServiceRegistryDisposition.RequireScmEntry; }
        // Only an absent ImagePath and independently absent exact named SCM
        // object exclude a registry-only recognizer. This assigns no run state.
        // The completed enum/registry set comparison still rejects a recognizer
        // returned by EnumServicesStatusEx but absent at this later boundary.
        return openServiceError() switch
        {
            1060 => CpuServiceRegistryDisposition.RegistryOnlyRecognizer,
            0 => CpuServiceRegistryDisposition.RequireScmEntry,
            _ => throw new IOException("Recognizer service classification unavailable.")
        };
    }
    internal static string ParseDriverImage(ReadOnlySpan<byte> bytes, ulong address, uint expectedType, bool allowEmpty = false)
    {
        if (bytes.Length != 8192 || address == 0 || address > ulong.MaxValue - (uint)bytes.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != expectedType || expectedType is not (1 or 2 or 4 or 8))
        { throw new IOException("Driver configuration denied."); }
        // QUERY_SERVICE_CONFIGW x64 is 64 bytes, BinaryPathName pointer at16.
        ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        if (pointer < address + 64 || pointer >= address + (uint)bytes.Length || (pointer & 1) != 0)
        { throw new IOException("Driver configuration path pointer denied."); }
        int start = checked((int)(pointer - address));
        for (int end = start; end + 1 < bytes.Length; end += 2)
        {
            if (bytes[end] != 0 || bytes[end + 1] != 0) { continue; }
            string image;
            try { image = new UnicodeEncoding(false, false, true).GetString(bytes[start..end]); }
            catch (DecoderFallbackException) { throw new IOException("Driver configuration path malformed."); }
            if (image.Length == 0 && allowEmpty) { return image; }
            return string.IsNullOrWhiteSpace(image) ? throw new IOException("Driver configuration path empty.") : image;
        }
        throw new IOException("Driver configuration path bound.");
    }
    internal static string DefaultDriverPath(string name, uint type, bool registryImageAbsent, string scmImage, string windowsDirectory)
    {
        // Historical Microsoft-authored KB103000 (Windows NT3.1) documents this
        // default. This is configured-path provenance, not loaded-kernel evidence.
        // https://www.betaarchive.com/wiki/index.php/Microsoft_KB_Archive/103000
        if (!registryImageAbsent || scmImage.Length != 0 || type is not (1 or 2 or 8)
            || name.Length is 0 or > 256 || name is "." or ".." || name.EndsWith('.')
            || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            || !Path.IsPathFullyQualified(windowsDirectory) || windowsDirectory.StartsWith(@"\\", StringComparison.Ordinal)
            || windowsDirectory != Path.GetFullPath(windowsDirectory)) { throw new IOException("Default driver path unavailable."); }
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9')
        { throw new IOException("Default driver name denied."); }
        return Path.Combine(windowsDirectory, "System32", "drivers", name + ".sys");
    }
    // x64 ENUM_SERVICE_STATUS_PROCESSW: two pointers + SERVICE_STATUS_PROCESS
    // (nine DWORDs), trailing alignment padding; size 56, Type offset 16.
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeEntry
    {
        internal nint Name, DisplayName;
        internal uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags;
    }
    internal sealed class NativeInventory(CpuServiceInventoryKind kind) : ICpuServiceInventoryPlatform
    {
        private static object? ReadRegistryValue(RegistryKey key, string name, bool image)
        {
            // Only native ERROR_FILE_NOT_FOUND proves absence. GetValue's null
            // default also represents present unsupported REG_NONE/REG_LINK.
            // https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regqueryvalueexw
            byte[] bytes = new byte[image ? 65536 : 4]; uint length = (uint)bytes.Length;
            int status = RegQueryValueExW(key.Handle, name, 0, out uint type, bytes, ref length);
            return DecodeRegistryValue(status, type, bytes, length, image);
        }
        public uint ReadConfiguredType(string name) => ReadConfiguration(name, (bytes, _) =>
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return KnownType(type) ? type : throw new IOException("Configured service type unknown.");
        });
        private static string ReadDriverImage(string name, uint type) =>
            ReadConfiguration(name, (bytes, address) => ParseDriverImage(bytes, address, type, allowEmpty: true));
        private static T ReadConfiguration<T>(string name, Func<byte[], ulong, T> parse)
        {
            nint manager = OpenSCManagerW(null, null, 1);
            if (manager == 0) { throw new IOException("Driver configuration SCM unavailable."); }
            try
            {
                nint service = OpenServiceW(manager, name, 1);
                if (service == 0) { throw new IOException("Driver configuration unavailable."); }
                try
                {
                    byte[] bytes = new byte[8192]; Array.Fill(bytes, (byte)0xFF);
                    nint buffer = Marshal.AllocHGlobal(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, buffer, bytes.Length);
                        if (!QueryServiceConfigW(service, buffer, (uint)bytes.Length, out _))
                        { throw new IOException("Driver configuration unavailable."); }
                        Marshal.Copy(buffer, bytes, 0, bytes.Length);
                        return parse(bytes, unchecked((ulong)buffer.ToInt64()));
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { if (!CloseServiceHandle(service)) { throw new IOException("Driver configuration cleanup failed."); } }
            }
            finally { if (!CloseServiceHandle(manager)) { throw new IOException("Driver configuration SCM cleanup failed."); } }
        }
        private static int ReadOpenServiceError(string name)
        {
            nint manager = OpenSCManagerW(null, null, 1);
            if (manager == 0) { throw new IOException("Service classification SCM unavailable."); }
            try
            {
                nint service = OpenServiceW(manager, name, 4);
                if (service == 0) { return Marshal.GetLastWin32Error(); }
                if (!CloseServiceHandle(service)) { throw new IOException("Service classification cleanup failed."); }
                return 0;
            }
            finally { if (!CloseServiceHandle(manager)) { throw new IOException("Service classification SCM cleanup failed."); } }
        }
        public CpuServiceTemplateStatus ReadUnenumeratedTemplateStatus(string name)
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) { throw new PlatformNotSupportedException(); }
            if (kind != CpuServiceInventoryKind.Win32 || string.IsNullOrWhiteSpace(name) || name.Length > 256
                || name.IndexOfAny(['\\', '/', '\0']) >= 0) { throw new IOException("Service template name denied."); }
            nint manager = OpenSCManagerW(null, null, 1);
            if (manager == 0) { throw new IOException("Service template SCM unavailable."); }
            try
            {
                nint service = OpenServiceW(manager, name, 4);
                if (service == 0) { throw new IOException("Service template query unavailable."); }
                try
                {
                    // pcbBytesNeeded is documented only for insufficient-buffer
                    // failure. BOOL success establishes this fixed structure.
                    if (!QueryServiceStatusEx(service, 0, out NativeStatus value, (uint)Marshal.SizeOf<NativeStatus>(), out _))
                    { throw new IOException("Service template status unavailable."); }
                    return new(value.Type, value.State);
                }
                finally { if (!CloseServiceHandle(service)) { throw new IOException("Service template cleanup failed."); } }
            }
            finally { if (!CloseServiceHandle(manager)) { throw new IOException("Service template SCM cleanup failed."); } }
        }
        public CpuServiceEnumeration Enumerate(nint buffer, int bytes)
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) { throw new PlatformNotSupportedException(); }
            nint manager = OpenSCManagerW(null, null, 4);
            if (manager == 0) { throw new IOException("Driver inventory SCM unavailable."); }
            try
            {
                uint resume = 0;
                bool success = EnumServicesStatusExW(manager, 0, kind == CpuServiceInventoryKind.Drivers ? 0xBu : 0x30u, 3, buffer, (uint)bytes, out uint remaining, out uint count, ref resume, null);
                int error = success ? 0 : Marshal.GetLastWin32Error();
                return new(success, error, remaining, count, resume);
            }
            finally { if (!CloseServiceHandle(manager)) { throw new IOException("Driver inventory SCM cleanup failed."); } }
        }
        public IReadOnlyList<CpuServiceRegistryEntry> ReadRegistryEntries()
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
            var drivers = new List<CpuServiceRegistryEntry>();
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.IndexOfAny(['\\', '/', '\0']) >= 0)
                { throw new IOException("Driver inventory name denied."); }
                using var key = services.OpenSubKey(name, false) ?? throw new IOException("Service inventory changed.");
                object? rawType = ReadRegistryValue(key, "Type", image: false);
                if (ExcludeNonServiceKey(rawType, () => ReadOpenServiceError(name))) { continue; }
                int type = (int)rawType!;
                if (!KnownType((uint)type)) { throw new IOException("Service inventory type unknown."); }
                if (!Belongs(kind, (uint)type)) { continue; }
                if (drivers.Count >= MaximumEntries) { throw new IOException("Service inventory bound."); }
                string image = "";
                CpuServiceImageProvenance provenance = CpuServiceImageProvenance.None;
                if (kind == CpuServiceInventoryKind.Drivers)
                {
                    object? rawImage = ReadRegistryValue(key, "ImagePath", image: true);
                    if (ClassifyRecognizer((uint)type, rawImage, () => ReadOpenServiceError(name))
                        == CpuServiceRegistryDisposition.RegistryOnlyRecognizer) { continue; }
                    if (rawImage is null)
                    {
                        image = ReadDriverImage(name, (uint)type); provenance = CpuServiceImageProvenance.ScmConfigured;
                        if (image.Length == 0)
                        {
                            var directory = new StringBuilder(512); uint length = GetWindowsDirectoryW(directory, 512);
                            if (length is 0 or >= 512) { throw new IOException("Windows directory unavailable."); }
                            image = DefaultDriverPath(name, (uint)type, true, image, directory.ToString());
                            AssertRegularDefaultImage(image); provenance = CpuServiceImageProvenance.DefaultDriverPath;
                        }
                    }
                    else if (rawImage is string value)
                    { image = value; provenance = CpuServiceImageProvenance.RegistryConfigured; }
                    else { throw new IOException("Driver inventory image unavailable."); }
                }
                drivers.Add(new(name, (uint)type, image) { ImageProvenance = provenance });
            }
            return drivers.AsReadOnly();
        }
        private static void AssertRegularDefaultImage(string path)
        {
            using SafeFileHandle file = CreateFileW(path, 0x80000000, 1, 0, 3, 0x00200000, 0);
            if (file.IsInvalid || GetFileType(file) != 1 || !GetFileInformationByHandle(file, out FileInformation info)
                || (info.Attributes & 0x410) != 0 || (info.SizeHigh == 0 && info.SizeLow == 0))
            { throw new IOException("Default driver file unavailable."); }
            var final = new StringBuilder(1024); uint length = GetFinalPathNameByHandleW(file, final, 1024, 0);
            if (length is 0 or >= 1024 || !string.Equals(final.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            { throw new IOException("Default driver file redirected."); }
            for (DirectoryInfo? parent = new FileInfo(path).Directory; parent is not null; parent = parent.Parent)
            { if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) { throw new IOException("Default driver parent redirected."); } }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
                VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint GetWindowsDirectoryW(StringBuilder buffer, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll")]
        private static extern uint GetFileType(SafeFileHandle file);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
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
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeStatus
        { internal uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern nint OpenServiceW(nint manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatusEx(nint service, uint level, out NativeStatus status, uint size, out uint needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceConfigW(nint service, nint configuration, uint size, out uint needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegEnumKeyExW(SafeRegistryHandle key, uint index, StringBuilder name,
            ref uint length, nint reserved, nint className, nint classLength, nint lastWriteTime);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegQueryValueExW(SafeRegistryHandle key, string name, nint reserved,
            out uint type, [Out] byte[] data, ref uint bytes);
    }
}
