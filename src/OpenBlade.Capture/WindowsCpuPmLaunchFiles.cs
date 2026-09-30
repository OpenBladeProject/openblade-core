using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

// Read/pin only. Reuses the reviewed evidence file ABI and strict private ACL
// parser. No directory creation, record write, payload loading or driver calls.
internal sealed class WindowsCpuPmLaunchFiles : ICpuPmLaunchFiles
{
    private readonly Dictionary<string, Pin> pins = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    public void PinDirectory(string path, bool privateAcl, bool protectedAcl) => Open(path, true, 0, privateAcl, protectedAcl);
    public ICpuPmLaunchFilePin PinFile(string path, long maximumBytes, bool privateAcl) => Open(path, false, maximumBytes, privateAcl, false);
    private Pin Open(string path, bool directory, long maximumBytes, bool privateAcl, bool protectedAcl)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || disposed || path.Length > 400
            || !path.StartsWith(@"C:\", StringComparison.Ordinal) || path != Path.GetFullPath(path)
            || path.Contains('/') || maximumBytes < 0 || maximumBytes > CpuPmLaunchSchema.MaximumFileBytes) { throw Denied(); }
        if (pins.TryGetValue(path, out var existing))
        {
            if (existing.Directory != directory || !directory && existing.Length > maximumBytes) { throw Denied(); }
            existing.PrivateAcl |= privateAcl; existing.ProtectedAcl |= protectedAcl; existing.AssertHeld(); return existing;
        }
        if (pins.Count >= 800) { throw Denied(); }
        SafeFileHandle handle = WindowsCpuPmCaptureEvidenceFiles.CreateFileW(path, directory ? 0x00020080u : 0x80020000u,
            1, 0, 3, directory ? 0x02200000u : 0x00200000u, 0);
        FileStream? stream = null;
        try
        {
            var pin = new Pin(path, directory, handle) { PrivateAcl = privateAcl, ProtectedAcl = protectedAcl };
            pin.AssertHeld();
            if (!directory)
            {
                stream = new FileStream(handle, FileAccess.Read); pin.Attach(stream, maximumBytes); stream = null;
            }
            pins.Add(path, pin); return pin;
        }
        catch { stream?.Dispose(); handle.Dispose(); throw; }
    }
    public IEnumerable<CpuPmLaunchEntry> Enumerate(string path)
    {
        if (disposed || !pins.TryGetValue(path, out var directory) || !directory.Directory) { throw Denied(); }
        directory.AssertHeld(); int count = 0;
        foreach (string entry in System.IO.Directory.EnumerateFileSystemEntries(path))
        {
            if (++count > 768) { throw Denied(); }
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { throw Denied(); }
            yield return new(Path.GetFileName(entry), (attributes & FileAttributes.Directory) != 0);
        }
        directory.AssertHeld();
    }
    public void AssertAllPins()
    {
        if (disposed || pins.Count == 0) { throw Denied(); }
        foreach (var pin in pins.Values) { pin.AssertHeld(); }
    }
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        foreach (var pin in pins.Values.Reverse()) { pin.Dispose(); }
    }
    private sealed class Pin(string path, bool directory, SafeFileHandle handle) : ICpuPmLaunchFilePin, IDisposable
    {
        private FileStream? stream;
        private bool measured;
        internal bool Directory => directory;
        internal bool PrivateAcl, ProtectedAcl;
        public long Length { get; private set; }
        public string Sha256 { get; private set; } = "";
        internal void Attach(FileStream value, long maximumBytes)
        {
            stream = value;
            if (value.Length < 0 || value.Length > maximumBytes) { throw Denied(); }
            Length = value.Length; Sha256 = Convert.ToHexString(SHA256.HashData(value)); measured = true; AssertHeld();
        }
        public byte[] ReadBounded(int maximumBytes)
        {
            AssertHeld();
            if (stream is null || Length > maximumBytes || maximumBytes is < 0 or > CpuPmLaunchSchema.ManifestLimit) { throw Denied(); }
            byte[] bytes = new byte[checked((int)Length)]; stream.Position = 0; stream.ReadExactly(bytes);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != Sha256) { throw Denied(); }
            AssertHeld(); return bytes;
        }
        internal void AssertHeld()
        {
            if (handle.IsInvalid || handle.IsClosed || GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info)
                || (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory
                || !directory && info.NumberOfLinks != 1
                || measured && (((ulong)info.FileSizeHigh << 32) | info.FileSizeLow) != (ulong)Length) { throw Denied(); }
            var final = new StringBuilder(512); uint size = GetFinalPathNameByHandleW(handle, final, 512, 0);
            if (size is 0 or >= 512 || !string.Equals(final.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase)) { throw Denied(); }
            if (PrivateAcl) { AssertPrivateAcl(handle, ProtectedAcl); }
        }
        public void Dispose() { if (stream is not null) { stream.Dispose(); } else { handle.Dispose(); } }
    }
    private static void AssertPrivateAcl(SafeFileHandle handle, bool protectedAcl)
    {
        if (GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out nint descriptor) != 0) { throw Denied(); }
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor); if (length is 0 or > 8192) { throw Denied(); }
            byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            if (!WindowsCpuPmCaptureEvidenceFiles.IsPrivateDescriptor(bytes, protectedAcl)) { throw Denied(); }
        }
        finally { _ = LocalFree(descriptor); }
    }
    private static IOException Denied() => new("PM launch file pin denied.");
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsCpuPmCaptureEvidenceFiles.FileInformation information);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle file, uint type, uint information, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(nint descriptor);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint allocation);
}
