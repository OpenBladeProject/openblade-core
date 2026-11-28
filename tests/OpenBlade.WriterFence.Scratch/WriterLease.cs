using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.WriterFence.Scratch;

internal sealed class WriterLease(PinnedRoot root, SafeFileHandle file) : IDisposable
{
    internal const string Name = "configuration.writer-lock";

    internal static WriterLease Acquire(string path, string rootId, string lockId) =>
        AcquireCore(path, rootId, lockId, PinnedRoot.Open);

    internal static WriterLease AcquireExactRoot(string path, string rootId, string lockId) =>
        AcquireCore(path, rootId, lockId, PinnedRoot.OpenPath);

    private static WriterLease AcquireCore(
        string path, string rootId, string lockId, Func<string, PinnedRoot> pin)
    {
        if (!Regex.IsMatch(rootId, @"\A[0-9a-f]{8}:[0-9a-f]{16}\z") ||
            !Regex.IsMatch(lockId, @"\A[0-9a-f]{8}:[0-9a-f]{16}\z") ||
            rootId[9..] == "0000000000000000" || lockId[9..] == "0000000000000000" ||
            rootId[..8] != lockId[..8])
        {
            throw new IOException("Malformed scratch identity.");
        }

        PinnedRoot root = pin(path);
        try
        {
            if (root.Info.Identity != rootId)
            {
                throw new IOException("Scratch root identity mismatch.");
            }

            SafeFileHandle file = Native.OpenRelative(root.Handle, Name, directory: false);
            try
            {
                var info = Native.Inspect(file, directory: false);
                if (info.Volume != root.Info.Volume || info.Identity != lockId ||
                    info.Links != 1 || info.SizeHigh != 0 || info.SizeLow != 0)
                {
                    throw new IOException("Scratch writer lease identity or shape mismatch.");
                }
                return new WriterLease(root, file);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        file.Dispose();
        root.Dispose();
    }
}

internal sealed class PinnedRoot(List<SafeFileHandle> pins, Native.FileInformation info) : IDisposable
{
    internal SafeFileHandle Handle => pins[^1];
    internal Native.FileInformation Info { get; } = info;

    internal static PinnedRoot Open(string path)
    {
        if (!Regex.IsMatch(Path.GetFileName(path), @"\Aopenblade-writer-fence-[0-9a-f]{32}\z"))
        {
            throw new IOException("Expected a named scratch writer-fence directory.");
        }
        return OpenPath(path);
    }

    internal static PinnedRoot OpenPath(string path)
    {
        if (!Regex.IsMatch(path, @"\A[A-Za-z]:\\") || path.Length <= 3 ||
            !string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal))
        {
            throw new IOException("Expected an exact local scratch path.");
        }

        string drive = path[..3];
        var pins = new List<SafeFileHandle>();
        try
        {
            SafeFileHandle driveHandle = Native.OpenDrive(drive);
            pins.Add(driveHandle);
            Native.FileInformation info = Native.Inspect(driveHandle, directory: true);
            uint volume = info.Volume;
            foreach (string component in path[3..].Split('\\'))
            {
                if (component.Length == 0 || component.EndsWith('.') || component.EndsWith(' ') ||
                    component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    Regex.IsMatch(component.Split('.')[0].TrimEnd(' '),
                        @"\A(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(COM|LPT)[1-9¹²³])\z",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    throw new IOException("Ambiguous scratch path component.");
                }

                SafeFileHandle directory = Native.OpenRelative(pins[^1], component, directory: true);
                pins.Add(directory);
                info = Native.Inspect(directory, directory: true);
                if (info.Volume != volume)
                {
                    throw new IOException("Scratch path changed volumes.");
                }
            }
            return new PinnedRoot(pins, info);
        }
        catch
        {
            Close(pins);
            throw;
        }
    }

    private static void Close(List<SafeFileHandle> handles)
    {
        for (int i = handles.Count - 1; i >= 0; i--)
        {
            handles[i].Dispose();
        }
    }

    public void Dispose() => Close(pins);
}

internal static unsafe class Native
{
    // Windows GENERIC access, file-share flags, and NtCreateFile dispositions/options.
    private const uint Read = 0x80000000, Write = 0x40000000, Synchronize = 0x00100000, Delete = 0x10000;
    private const uint ShareReadWrite = 3, OpenExisting = 3, NtOpen = 1, NtCreate = 2;
    private const uint OpenReparse = 0x00200000, BackupSemantics = 0x02000000;
    private const uint NtDirectory = 1, NtNonDirectory = 0x40, NtSynchronous = 0x20;
    private const uint CaseInsensitive = 0x40, DontReparse = 0x1000;

    internal static SafeFileHandle OpenDrive(string drive)
    {
        if (GetDriveTypeW(drive) != 3)
        {
            throw new IOException("Scratch drive is not fixed.");
        }
        SafeFileHandle handle = CreateFileW(drive, Read, ShareReadWrite, 0, OpenExisting,
            BackupSemantics | OpenReparse, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            Span<char> finalPath = stackalloc char[16];
            Span<char> filesystem = stackalloc char[16];
            fixed (char* pathBuffer = finalPath, fsBuffer = filesystem)
            {
                uint length = GetFinalPathNameByHandleW(handle, pathBuffer, (uint)finalPath.Length, 0);
                if (length >= finalPath.Length ||
                    !finalPath[..(int)length].Equals(@"\\?\" + drive, StringComparison.OrdinalIgnoreCase) ||
                    !GetVolumeInformationByHandleW(handle, 0, 0, 0, 0, 0, fsBuffer, (uint)filesystem.Length) ||
                    !filesystem[..5].SequenceEqual("NTFS\0"))
                {
                    throw new IOException("Expected an unredirected NTFS drive.");
                }
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool directory,
        bool create = false, bool delete = false)
    {
        fixed (char* text = name)
        {
            ushort length = checked((ushort)(name.Length * sizeof(char)));
            var child = new UnicodeString { Length = length, MaximumLength = length, Buffer = text };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = &child,
                Attributes = CaseInsensitive | DontReparse,
            };
            uint access = Read | Synchronize | (directory ? 0 : Write) | (delete ? Delete : 0);
            int status = NtCreateFile(out nint handle, access,
                &attributes, out _, 0, directory ? 0x10u : 0, directory ? ShareReadWrite : 0,
                create ? NtCreate : NtOpen,
                NtSynchronous | OpenReparse | (directory ? NtDirectory : NtNonDirectory), 0, 0);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                throw new Win32Exception((int)RtlNtStatusToDosError(status));
            }
            return new SafeFileHandle(handle, ownsHandle: true);
        }
    }

    internal static void DeleteAdmitted(SafeFileHandle handle, string expectedId, bool directory)
    {
        if (Inspect(handle, directory).Identity != expectedId)
        {
            throw new IOException("Scratch cleanup identity mismatch.");
        }
        byte delete = 1;
        const int fileDispositionInfo = 4;
        if (!SetFileInformationByHandle(handle, fileDispositionInfo, &delete, sizeof(byte)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal static FileInformation Inspect(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out FileInformation info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        const uint directoryAttribute = 0x10, reparseAttribute = 0x400;
        if (GetFileType(handle) != 1 ||
            (info.Attributes & (directoryAttribute | reparseAttribute)) != (directory ? directoryAttribute : 0) ||
            (info.IndexHigh == 0 && info.IndexLow == 0))
        {
            throw new IOException("Scratch entry is not a plain disk object.");
        }
        return info;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInformation
    {
        public uint Attributes;
        private System.Runtime.InteropServices.ComTypes.FILETIME creation, access, write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        internal readonly string Identity => $"{Volume:x8}:{IndexHigh:x8}{IndexLow:x8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status, Information;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out nint handle, uint access, ObjectAttributes* attributes,
        out IoStatusBlock status, nint allocation, uint fileAttributes, uint share, uint disposition,
        uint options, nint ea, uint eaLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint length, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, nint name, uint nameLength,
        nint serial, nint maxComponent, nint flags, char* filesystem, uint length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte* info, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetFileType(SafeFileHandle file);
}
