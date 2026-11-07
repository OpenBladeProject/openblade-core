using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

// Future-launcher-only native implementation. No production entry point creates
// an authenticated run. This class never creates directories or deletes files.
internal sealed class WindowsCpuPmCaptureEvidenceFiles : ICpuPmCaptureEvidenceFiles
{
    internal const string FixedRoot = @"C:\OpenBlade\validation-archive\cpu-tuning-re\pm-observation-runs";
    internal const uint CreateNew = 1, OpenExisting = 3, MoveWriteThrough = 8;
    private readonly ICpuPmAuthenticatedCaptureRun run;
    private readonly Guid frozenRunId;
    private readonly string runPath, evidencePath, controlPath;
    private readonly List<(SafeFileHandle Handle, string Path, bool Directory)> pins = [];
    private readonly HashSet<string> expectedEvidence = new(StringComparer.Ordinal);
    private bool begun, failed, released;

    internal WindowsCpuPmCaptureEvidenceFiles(ICpuPmAuthenticatedCaptureRun? authenticatedRun)
    {
        run = authenticatedRun ?? throw new InvalidOperationException("Authenticated PM run unavailable.");
        run.AssertPinnedRun();
        frozenRunId = run.RunId;
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || frozenRunId == Guid.Empty)
        { throw new InvalidOperationException("PM evidence platform denied."); }
        runPath = Path.Combine(FixedRoot, frozenRunId.ToString("N"));
        // Trusted creator supplies O:BA (or O:SY) and a protected SY/BA full
        // control run DACL with OICI inheritance, e.g.
        // O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA). Child directories inherit
        // those private grants; never rely on the invoking user's default DACL.
        evidencePath = Path.Combine(runPath, "evidence"); controlPath = Path.Combine(runPath, "control");
        try
        {
            var paths = new Stack<string>();
            for (string? path = runPath; path is not null; path = Path.GetDirectoryName(path)) { paths.Push(path); }
            while (paths.Count > 0) { PinDirectory(paths.Pop()); }
            PinDirectory(controlPath); PinDirectory(evidencePath);
            Validate();
        }
        catch { foreach (var pin in pins) { pin.Handle.Dispose(); } throw; }
    }

    public void BeginFreshRun(Guid runId)
    {
        if (begun || failed || released || runId != frozenRunId) { failed = true; throw Denied(); }
        begun = true;
        try
        {
            Validate(); ValidateEntries();
            // Control marker is outside the ten evidence slots. Never adopt it.
            string marker = Path.Combine(controlPath, "capture-start");
            byte[] bytes = Encoding.UTF8.GetBytes($"OpenBlade-PM-capture-start-v1\n{runId:N}\n");
            Store(marker, bytes, Convert.ToHexString(SHA256.HashData(bytes)), rename: false);
        }
        catch { failed = true; throw; }
    }

    public void StoreImmutable(string name, byte[] bytes, string sha256)
    {
        if (!begun || failed || released || !AllowedName(name)) { failed = true; throw Denied(); }
        try
        {
            Validate(); ValidateEntries();
            if (expectedEvidence.Contains(name) || sha256 != Convert.ToHexString(SHA256.HashData(bytes))) { throw Denied(); }
            Store(Path.Combine(evidencePath, name), (byte[])bytes.Clone(), sha256, rename: true);
            expectedEvidence.Add(name);
        }
        catch { failed = true; throw; }
    }

    private void Store(string final, byte[] bytes, string hash, bool rename)
    {
        string partial = rename ? final + ".partial" : final;
        Validate();
        using (SafeFileHandle handle = CreateFileW(partial, 0xC0020000, 1, IntPtr.Zero, CreateNew, 0x00200000, IntPtr.Zero))
        {
            if (handle.IsInvalid) { throw Denied(); }
            // Inherited private ACL must be present on the returned object too.
            VerifyShape(handle, partial, false); VerifyPrivateAcl(handle);
            using var stream = new FileStream(handle, FileAccess.ReadWrite);
            stream.Write(bytes); stream.Flush(true); stream.Position = 0;
            VerifyContent(stream, bytes.Length, hash);
            VerifyShape(handle, partial, false); Validate();
        }
        if (rename)
        {
            Validate();
            if (!MoveFileExW(partial, final, MoveWriteThrough)) { throw Denied(); }
        }
        Validate();
        var retained = CreateFileW(final, 0x80020000, 1, IntPtr.Zero, OpenExisting, 0x00200000, IntPtr.Zero);
        try
        {
            if (retained.IsInvalid) { throw Denied(); }
            VerifyShape(retained, final, false); VerifyPrivateAcl(retained);
            // Do not transfer ownership of the pin to a disposable verification stream.
            using var stream = new FileStream(new SafeFileHandle(retained.DangerousGetHandle(), ownsHandle: false), FileAccess.Read);
            VerifyContent(stream, bytes.Length, hash); Validate();
            pins.Add((retained, final, false));
        }
        catch { retained.Dispose(); throw; }
    }

    private void PinDirectory(string path)
    {
        var handle = CreateFileW(path, 0x00020080, 3, IntPtr.Zero, OpenExisting, 0x02200000, IntPtr.Zero);
        try { if (handle.IsInvalid) { throw Denied(); } VerifyShape(handle, path, true); pins.Add((handle, path, true)); }
        catch { handle.Dispose(); throw; }
    }
    private void Validate()
    {
        if (released) { throw Denied(); }
        run.AssertPinnedRun();
        if (run.RunId != frozenRunId) { throw Denied(); }
        foreach (var pin in pins)
        {
            VerifyShape(pin.Handle, pin.Path, pin.Directory);
            if (pin.Path == runPath || pin.Path == controlPath || pin.Path == evidencePath || !pin.Directory)
            { VerifyPrivateAcl(pin.Handle, requireProtected: pin.Path == runPath); }
        }
    }
    private void ValidateEntries()
    {
        // Bounded refusal: an eleventh or unknown entry ends the enumeration.
        int count = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(evidencePath))
        {
            if (++count > 10 || !expectedEvidence.Contains(Path.GetFileName(path))) { throw Denied(); }
        }
        if (count != expectedEvidence.Count) { throw Denied(); }
        foreach (string path in Directory.EnumerateFileSystemEntries(runPath))
        { if (path != controlPath && path != evidencePath) { throw Denied(); } }
    }
    internal static bool AllowedName(string name) => name == "capture-result.json"
        || Enumerable.Range(0, 3).Any(i => name == $"sample-{i:00}.reply.bin" || name == $"sample-{i:00}.payload.bin" || name == $"sample-{i:00}.json");
    private static void VerifyContent(FileStream stream, int length, string hash)
    {
        if (stream.Length != length || Convert.ToHexString(SHA256.HashData(stream)) != hash) { throw Denied(); }
    }
    internal static void VerifyShape(SafeFileHandle handle, string path, bool directory)
    {
        if (handle.IsClosed || handle.IsInvalid || GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info)
            || (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory
            || !directory && info.NumberOfLinks != 1) { throw Denied(); }
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity || !string.Equals(buffer.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase)) { throw Denied(); }
    }
    internal static void VerifyPrivateAcl(SafeFileHandle handle, bool requireProtected = false)
    {
        // Security descriptor is obtained from the held handle, never a path lookup.
        if (GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out IntPtr descriptor) != 0) { throw Denied(); }
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor);
            if (length is 0 or > 8192) { throw Denied(); }
            byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            if (!IsPrivateDescriptor(bytes, requireProtected)) { throw Denied(); }
        }
        finally { LocalFree(descriptor); }
    }
    internal static bool IsPrivateDescriptor(byte[] bytes, bool requireProtected)
    {
        try
        {
            if (bytes.Length is 0 or > 8192) { return false; }
            var security = new RawSecurityDescriptor(bytes, 0);
            if (requireProtected && (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0
                || security.Owner?.Value is not ("S-1-5-18" or "S-1-5-32-544")
                || security.DiscretionaryAcl is not { Count: > 0 } acl) { return false; }
            bool system = false, administrators = false;
            foreach (GenericAce ace in acl)
            {
                if (ace is not CommonAce common || common.AceQualifier != AceQualifier.AccessAllowed
                    || (common.AceFlags & AceFlags.InheritOnly) != 0
                    || common.SecurityIdentifier.Value is not ("S-1-5-18" or "S-1-5-32-544")
                    || (common.AccessMask & 0x1F01FF) != 0x1F01FF) { return false; }
                if (common.SecurityIdentifier.Value == "S-1-5-18") { system = true; } else { administrators = true; }
            }
            return system && administrators;
        }
        catch (ArgumentException) { return false; }
    }
    public void ReleaseAfterWorkerTerminal()
    {
        if (run.RunId != frozenRunId) { failed = true; throw Denied(); }
        run.AssertWorkerTerminal();
        if (run.RunId != frozenRunId) { failed = true; throw Denied(); }
        if (released) { return; }
        released = true;
        foreach (var pin in pins) { pin.Handle.Dispose(); }
    }
    private static IOException Denied() => new("PM evidence persistence denied.");
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    // BOOL(LPCWSTR,LPCWSTR,DWORD); only WRITE_THROUGH=8, no copy/replace/reboot.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MoveFileExW(string existing, string destination, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
