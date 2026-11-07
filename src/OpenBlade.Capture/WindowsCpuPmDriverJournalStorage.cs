using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

// One native journal per parent process, rooted before CREATE_NEW. On uncertain
// create/write/flush there is no Dispose/reopen/retry path; the same handles and
// private ancestor pins survive until that process exits. Completed evidence also
// remains immutable under its held deny-write/delete pin through parent exit.
internal sealed class WindowsCpuPmDriverJournalStorage : ICpuPmDriverJournalStorage
{
    internal const uint WriteThroughOpenReparse = 0x80200000;
    private static CpuPmDurableDriverJournal? processRoot;
    private static int claimed;
    private readonly Guid run;
    private readonly WindowsCpuPmLaunchFiles pins = new();
    private SafeFileHandle? handle;
    private FileStream? file;
    private bool attempted, failed;
    private string? path;
    private WindowsCpuPmDriverJournalStorage(Guid run) => this.run = run;
    internal static CpuPmDurableDriverJournal CreateForFixedRun(CpuPmDriverOwnerTag owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) { throw new PlatformNotSupportedException(); }
        if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0) { throw new InvalidOperationException("Native PM journal already consumed."); }
        var storage = new WindowsCpuPmDriverJournalStorage(owner.Run);
        var journal = new CpuPmDurableDriverJournal(owner, storage); processRoot = journal; return journal;
    }
    public void CreateFreshAndPin()
    {
        if (attempted || failed) { failed = true; throw Denied(); }
        attempted = true;
        try
        {
            string root = Path.Combine(WindowsCpuPmCaptureEvidenceFiles.FixedRoot, run.ToString("N"));
            var ancestors = new Stack<string>();
            for (string? parent = Path.GetDirectoryName(root); parent is not null; parent = Path.GetDirectoryName(parent)) { ancestors.Push(parent); }
            while (ancestors.Count > 0) { pins.PinDirectory(ancestors.Pop(), false, false); }
            pins.PinDirectory(root, true, true); string control = Path.Combine(root, "control"); pins.PinDirectory(control, true, false);
            path = Path.Combine(control, "lifecycle.jsonl");
            handle = WindowsCpuPmCaptureEvidenceFiles.CreateFileW(path, 0xC0020000, 1, 0, 1, WriteThroughOpenReparse, 0);
            AssertHandle();
            file = new FileStream(handle, FileAccess.ReadWrite);
            if (file.Length != 0) { throw Denied(); }
            pins.AssertAllPins();
        }
        catch { failed = true; throw; }
    }
    public void AppendFlushAndVerify(byte[] line, int previousLength, string previousSha256, string nextSha256)
    {
        try
        {
            if (failed || file is null || line.Length is 0 or > CpuPmDurableDriverJournal.MaximumLineBytes || line[^1] != (byte)'\n'
                || previousLength < 0 || previousLength > CpuPmDurableDriverJournal.MaximumBytes - line.Length) { throw Denied(); }
            AssertHandle(); pins.AssertAllPins(); VerifyPrefix(previousLength, previousSha256);
            file.Position = previousLength; file.Write(line);
            // Flush OS/device buffers before observing success. Readback by itself
            // is insufficient; it could merely observe the process/kernel cache.
            file.Flush(flushToDisk: true);
            VerifyPrefix(previousLength + line.Length, nextSha256); AssertHandle(); pins.AssertAllPins();
        }
        catch { failed = true; throw; }
        finally { GC.KeepAlive(processRoot); }
    }
    private void VerifyPrefix(int length, string expected)
    {
        if (file is null || file.Length != length || length > CpuPmDurableDriverJournal.MaximumBytes) { throw Denied(); }
        byte[] bytes = new byte[length]; file.Position = 0; file.ReadExactly(bytes);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != expected) { throw Denied(); }
    }
    private void AssertHandle()
    {
        if (handle is null || handle.IsClosed || handle.IsInvalid || path is null || GetFileType(handle) != 1
            || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & (0x400 | 0x10)) != 0
            || info.NumberOfLinks != 1 || (((ulong)info.FileSizeHigh << 32) | info.FileSizeLow) > CpuPmDurableDriverJournal.MaximumBytes) { throw Denied(); }
        var final = new StringBuilder(512); uint length = GetFinalPathNameByHandleW(handle, final, 512, 0);
        if (length is 0 or >= 512 || !string.Equals(final.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase)) { throw Denied(); }
        if (GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out nint descriptor) != 0) { throw Denied(); }
        try
        {
            uint bytes = GetSecurityDescriptorLength(descriptor); if (bytes is 0 or > 8192) { throw Denied(); }
            byte[] acl = new byte[bytes]; Marshal.Copy(descriptor, acl, 0, acl.Length);
            if (!WindowsCpuPmCaptureEvidenceFiles.IsPrivateDescriptor(acl, requireProtected: false)) { throw Denied(); }
        }
        finally { _ = LocalFree(descriptor); }
    }
    private static IOException Denied() => new("Durable PM lifecycle journal denied.");
    // Existing CreateFileW ABI supplies CREATE_NEW=1, share READ=1 and the
    // documented FILE_FLAG_WRITE_THROUGH | OPEN_REPARSE_POINT flags above.
    // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createfilew
    // https://learn.microsoft.com/dotnet/api/system.io.filestream.flush
    [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsCpuPmCaptureEvidenceFiles.FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle file, uint type, uint information, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(nint descriptor);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint allocation);
}
