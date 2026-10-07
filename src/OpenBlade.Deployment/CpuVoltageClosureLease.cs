using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Deployment;

// A bounded, read-only pin of the signed fixed closure. No file is loaded as code.
public sealed class CpuVoltageClosureLease : IDisposable
{
    private readonly List<IDisposable> pins = [];
    private readonly Dictionary<string, FileStream> files = new(StringComparer.Ordinal);
    public IReadOnlyList<CpuVoltageDeploymentFile> Manifest { get; private set; } = [];
    public byte[] ManifestBytes { get; private set; } = [];
    private CpuVoltageClosureLease() { }

    public static CpuVoltageClosureLease Open(string root, string source, string digest, string version)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        var value = new CpuVoltageClosureLease();
        try
        {
            root = Path.GetFullPath(root);
            var ancestors = new Stack<string>();
            for (string? path = root; path is not null; path = Path.GetDirectoryName(path)) { ancestors.Push(path); }
            while (ancestors.Count > 0) { value.pins.Add(OpenHandle(ancestors.Pop(), directory: true)); }
            FileStream manifest = value.PinFile(Path.Combine(root, "manifest.json"), "manifest.json");
            if (manifest.Length is <= 0 or > CpuVoltageDeploymentClosure.MaximumManifestBytes) { throw new InvalidDataException("CPU manifest size invalid."); }
            value.ManifestBytes = new byte[checked((int)manifest.Length)]; manifest.ReadExactly(value.ManifestBytes); manifest.Position = 0;
            value.Manifest = CpuVoltageDeploymentClosure.Parse(value.ManifestBytes, source, digest);
            var expected = value.Manifest.Select(f => f.Name).Append("manifest.json").ToHashSet(StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in expected)
            {
                for (int slash = name.LastIndexOf('/'); slash > 0; slash = name.LastIndexOf('/', slash - 1)) { directories.Add(name[..slash]); }
            }
            foreach (string directory in directories.OrderBy(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal))
            { value.pins.Add(OpenHandle(Path.Combine(root, directory.Replace('/', '\\')), directory: true)); }
            var actual = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    string name = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (actual.Count >= CpuVoltageDeploymentClosure.MaximumEntries || !actual.Add(name)
                        || !expected.Contains(name) && !directories.Contains(name))
                    { throw new InvalidDataException("Unexpected CPU closure entry."); }
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0
                        || ((attributes & FileAttributes.Directory) != 0) != directories.Contains(name))
                    { throw new InvalidDataException("Redirected or mismatched CPU closure entry."); }
                    if (directories.Contains(name)) { pending.Push(path); }
                }
            }
            if (!actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Concat(directories).Order(StringComparer.Ordinal)))
            { throw new InvalidDataException("CPU closure entries differ."); }
            foreach (var file in value.Manifest)
            { CpuVoltageDeploymentClosure.VerifyFile(value.PinFile(Path.Combine(root, file.Name.Replace('/', '\\')), file.Name), file); }
            foreach (string assembly in new[] { "OpenBlade.Capture.dll", "firmware-smoke/OpenBlade.FirmwareSmoke.dll", "service/OpenBlade.Service.dll" })
            { CpuVoltageDeploymentClosure.VerifyAssembly(value.files[assembly], source, version, assembly.StartsWith("service/", StringComparison.Ordinal)); }
            return value;
        }
        catch { value.Dispose(); throw; }
    }

    public void CopyTo(string destination, Action<string> protectDirectory, Action<string> protectFile)
    {
        if (Directory.Exists(destination) || File.Exists(destination)) { throw new IOException("CPU closure destination already exists."); }
        Directory.CreateDirectory(destination); protectDirectory(destination);
        foreach (var pair in files)
        {
            string path = Path.Combine(destination, pair.Key.Replace('/', '\\'));
            string directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory))
            {
                var missing = new Stack<string>();
                for (string current = directory; !Directory.Exists(current); current = Path.GetDirectoryName(current)!) { missing.Push(current); }
                while (missing.Count > 0) { string current = missing.Pop(); Directory.CreateDirectory(current); protectDirectory(current); }
            }
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                pair.Value.Position = 0;
                pair.Value.CopyTo(output);
                output.Flush(true);
            }
            protectFile(path);
        }
    }
    public FileStream GetFile(string name) { var stream = files[name]; stream.Position = 0; return stream; }
    private FileStream PinFile(string path, string name)
    {
        var stream = new FileStream(OpenHandle(path, false), FileAccess.Read);
        pins.Add(stream); files.Add(name, stream); return stream;
    }
    private static SafeFileHandle OpenHandle(string path, bool directory)
    {
        var handle = CreateFileW(path, directory ? 0x80u : 0x80000000u, directory ? 3u : 1u, 0, 3,
            0x00200000u | (directory ? 0x02000000u : 0), 0);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("CPU closure pin unavailable.", new System.ComponentModel.Win32Exception(error)); }
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0
            || ((info.Attributes & 0x10) != 0) != directory || !directory && info.Links != 1)
        { handle.Dispose(); throw new IOException("CPU closure pin is not the expected regular object."); }
        return handle;
    }
    public void Dispose() { for (int i = pins.Count - 1; i >= 0; i--) { pins[i].Dispose(); } pins.Clear(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    { internal uint Attributes; internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written; internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}
