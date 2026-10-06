using System.Runtime.InteropServices;

namespace OpenBlade.Capture;

// Exactly one CREATE_NEW-equivalent directory attempt for the fixed fresh GUID.
// No recursive creation/adoption, ACL repair, deletion or recovery is supported.
internal static class WindowsCpuPmFreshRunTree
{
    private static WindowsCpuPmLaunchFiles? processPins;
    internal static void Create(Guid run)
    {
        if (run == Guid.Empty || processPins is not null) { throw new IOException("Fresh run unavailable."); }
        var pins = new WindowsCpuPmLaunchFiles(); processPins = pins;
        string root = WindowsCpuPmCaptureEvidenceFiles.FixedRoot;
        CpuPmAuthenticatedChildRun.PinAncestors(pins, root);
        string path = Path.Combine(root, run.ToString("N"));
        nint descriptor = 0;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", 1, out descriptor, out _))
            { throw new IOException("Run ACL unavailable."); }
            var attributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            bool missing = false;
            try { _ = File.GetAttributes(root); }
            catch (FileNotFoundException) { missing = true; }
            catch (DirectoryNotFoundException) { missing = true; }
            if (missing && !CreateDirectoryW(root, ref attributes)) { throw new IOException("Fixed private root unavailable."); }
            pins.PinDirectory(root, true, true);
            if (!CreateDirectoryW(path, ref attributes)) { throw new IOException("Run already exists or cannot be created."); }
            pins.PinDirectory(path, true, true);
            foreach (string name in new[] { "control", "evidence" })
            {
                string child = Path.Combine(path, name);
                if (!CreateDirectoryW(child, ref attributes)) { throw new IOException("Fresh run child unavailable."); }
                pins.PinDirectory(child, true, true);
            }
            pins.AssertAllPins();
        }
        finally { if (descriptor != 0) { _ = LocalFree(descriptor); } }
    }
    // SECURITY_ATTRIBUTES: DWORD, pointer, BOOL; x64 size24/descriptor offset8.
    // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createdirectoryw
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes { internal uint Length; internal nint Descriptor; internal int InheritHandle; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes security);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
