using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.SessionAgent;

internal static class WindowsThxHelperParentProcess
{
    internal static int ReadCurrentParentId()
    {
        using SafeSnapshotHandle snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot.IsInvalid)
        {
            return 0;
        }
        var entry = new ProcessEntry32
        {
            Size = checked((uint)Marshal.SizeOf<ProcessEntry32>()),
            ExecutableFile = string.Empty,
        };
        if (!Process32FirstW(snapshot, ref entry))
        {
            return 0;
        }
        int examined = 0;
        do
        {
            if (++examined > 65536)
            {
                return 0;
            }
            if (entry.ProcessId == (uint)Environment.ProcessId)
            {
                return entry.ParentProcessId is > 0 and <= int.MaxValue
                    ? (int)entry.ParentProcessId : 0;
            }
        }
        while (Process32NextW(snapshot, ref entry));
        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeSnapshotHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
