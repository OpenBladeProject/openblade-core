using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenBlade.Protocol;

internal static class WindowsControllerProcessParents
{
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    internal static IReadOnlyDictionary<int, int> Read()
    {
        Dictionary<int, int> parents = [];
        nint snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandleValue)
        {
            return parents;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = checked((uint)Marshal.SizeOf<ProcessEntry32>()),
            };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return parents;
            }

            do
            {
                if (entry.ProcessId is > 0 and <= int.MaxValue
                    && entry.ParentProcessId is > 0 and <= int.MaxValue)
                {
                    parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                }
            }
            while (Process32NextW(snapshot, ref entry));

            return parents;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

internal static class WindowsControllerProcessIdentity
{
    internal static bool CanUseParentSnapshot(DateTimeOffset? processStartedAtUtc, DateTimeOffset snapshotStartedAtUtc) =>
        processStartedAtUtc is { } started && started < snapshotStartedAtUtc;

    private const string LampArrayHelperPathSuffix =
        @"\Razer\RazerAppEngine\User Data\Apps\Common\LampArray\razerwdl.exe";

    internal static bool IsVerifiedLampArrayHelper(string? imagePath)
    {
        if (imagePath is null)
        {
            return false;
        }

        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(imagePath);
            return IsLampArrayHelper(
                imagePath,
                version.ProductName,
                version.OriginalFilename,
                version.CompanyName);
        }
        catch (Exception exception) when (exception is
            ArgumentException or FileNotFoundException or IOException
            or UnauthorizedAccessException or InvalidOperationException
            or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsLampArrayHelper(
        string? imagePath,
        string? productName,
        string? originalFilename,
        string? companyName)
    {
        if (imagePath is null || !Path.IsPathFullyQualified(imagePath))
        {
            return false;
        }

        try
        {
            string canonicalPath = Path.GetFullPath(imagePath);
            return canonicalPath.EndsWith(LampArrayHelperPathSuffix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(productName, "RazerWDL", StringComparison.OrdinalIgnoreCase)
                && string.Equals(originalFilename, "RazerWDL", StringComparison.OrdinalIgnoreCase)
                && string.Equals(companyName, "Razer Inc.", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
