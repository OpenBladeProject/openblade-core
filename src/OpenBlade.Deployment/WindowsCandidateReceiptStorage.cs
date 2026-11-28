#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Deployment;

// This storage policy is scoped to a nonadministrator-owned scratch tree.
// The current token stands in for the privileged owner in this rehearsal.
[SupportedOSPlatform("windows")]
internal sealed class WindowsCandidateReceiptStorage
{
    private const string RootPrefix = "OpenBlade.CandidateReceipt-";
    private const int PathCapacity = 512;
    private const int MaximumRecordBytes = 4096;
    private readonly SecurityIdentifier owner;

    private WindowsCandidateReceiptStorage(string root, SecurityIdentifier owner)
    {
        Root = root;
        this.owner = owner;
    }

    internal string Root { get; }

    internal static WindowsCandidateReceiptStorage CreateScratch() => OpenScratch(Guid.NewGuid(), create: true);

    internal static WindowsCandidateReceiptStorage OpenScratch(Guid scratchId) => OpenScratch(scratchId, create: false);

    private static WindowsCandidateReceiptStorage OpenScratch(Guid scratchId, bool create)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } user || identity.IsSystem
            || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("Candidate receipt rehearsal requires a standard Windows token.");
        }

        if (scratchId == Guid.Empty)
        {
            throw new InvalidDataException("Candidate receipt rehearsal requires a scratch identifier.");
        }
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string temp = Path.Combine(localData, "Temp");
        if (temp.Length < 3 || temp[1..3] != @":\" || GetDriveTypeW(temp[..3]) != 3)
        {
            throw new IOException("Candidate receipt rehearsal requires a fixed local scratch drive.");
        }

        string root = Path.Combine(temp, RootPrefix + scratchId.ToString("N"));
        if (create)
        {
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            using var ancestry = new DirectoryLease();
            PinAncestors(temp, ancestry, scope: null);
            RequireAbsent(root);
            new DirectoryInfo(root).Create(security);
        }
        var storage = new WindowsCandidateReceiptStorage(root, user);
        using var verified = storage.PinDirectory(root);
        var fileSystem = new StringBuilder(16);
        if (!GetVolumeInformationByHandleW(verified.Handles[^1], null, 0, out _, out _, out _,
                fileSystem, (uint)fileSystem.Capacity) || fileSystem.ToString() != "NTFS")
        {
            throw new IOException("Candidate receipt rehearsal requires NTFS.");
        }
        return storage;
    }

    internal Guid ScratchId => Guid.ParseExact(Path.GetFileName(Root)[RootPrefix.Length..], "N");

    internal DirectoryLease PinDirectory(string path)
    {
        RequireInRoot(path);
        var lease = new DirectoryLease();
        try
        {
            PinAncestors(path, lease, this);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void PinAncestors(string path, DirectoryLease lease, WindowsCandidateReceiptStorage? scope)
    {
        var ancestors = new Stack<string>();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            ancestors.Push(current);
        }
        foreach (string current in ancestors)
        {
            bool inScope = scope is not null && scope.IsInRoot(current);
            lease.Handles.Add(OpenChecked(current, directory: true, inScope ? scope : null));
        }
    }

    // Caller retains the containing DirectoryLease through the read and its decision.
    internal FileStream OpenFile(string path)
    {
        RequireInRoot(path);
        return new FileStream(OpenChecked(path, directory: false, this), FileAccess.Read);
    }

    internal void Publish(string path, byte[] bytes, Action? beforePublish = null)
    {
        if (bytes.Length is < 1 or > MaximumRecordBytes)
        {
            throw new InvalidDataException("Candidate receipt exceeds the publication size bound.");
        }
        RequireInRoot(path);
        string directory = Path.GetDirectoryName(path)!;
        using var lease = PinDirectory(directory);
        RequireAbsent(path);
        string temporary = Path.Combine(directory, "receipt-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                created = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            using var retained = new FileStream(OpenChecked(temporary, directory: false, this, delete: true),
                FileAccess.Read);
            RequireExact(retained, bytes);
            // Publish the verified object itself while its handle and the
            // containing directory are retained. The rename never replaces.
            beforePublish?.Invoke();
            PublishByHandle(retained.SafeFileHandle, path);
            created = false;
        }
        catch (Exception failure)
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
            }
            throw;
        }
    }

    private static void PublishByHandle(SafeFileHandle handle, string destination)
    {
        const int fileRenameInfo = 3;
        int nameOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName));
        int lengthOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileNameLength));
        byte[] name = Encoding.Unicode.GetBytes(destination + '\0');
        int size = nameOffset + name.Length;
        nint information = Marshal.AllocHGlobal(size);
        try
        {
            // Zero ReplaceIfExists and RootDirectory. The exact absolute
            // destination is below the directory lease held by Publish.
            Marshal.Copy(new byte[nameOffset], 0, information, nameOffset);
            Marshal.WriteInt32(information, lengthOffset, name.Length - sizeof(char));
            Marshal.Copy(name, 0, information + nameOffset, name.Length);
            if (!SetFileInformationByHandle(handle, fileRenameInfo, information, (uint)size))
            {
                throw new IOException("The verified candidate record could not be published.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(information);
        }
    }

    // Caller retains the checked containing directory. Verify and remove the
    // same object, with sharing that excludes concurrent receipt readers.
    internal void Retire(string path, byte[] expected)
    {
        RequireInRoot(path);
        using var stream = new FileStream(OpenChecked(path, directory: false, this, delete: true), FileAccess.Read);
        RequireExact(stream, expected);
        const int fileDispositionInfo = 4;
        byte disposition = 1;
        if (!SetFileInformationByHandle(stream.SafeFileHandle, fileDispositionInfo, ref disposition, sizeof(byte)))
        {
            throw new IOException("The provisional candidate record could not be retired.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    internal static void RequireAbsent(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        throw new IOException("Candidate receipt storage already contains the fixed name.");
    }

    internal static void RequireExact(FileStream stream, byte[] expected)
    {
        if (expected.Length is < 1 or > MaximumRecordBytes || stream.Length != expected.Length)
        {
            throw new InvalidDataException("Candidate receipt length differs from the issued record.");
        }
        Span<byte> bytes = stackalloc byte[expected.Length];
        stream.ReadExactly(bytes);
        if (!bytes.SequenceEqual(expected))
        {
            throw new InvalidDataException("Candidate receipt differs from the issued record.");
        }
    }

    internal static byte[] ReadBounded(FileStream stream, int maximumBytes = MaximumRecordBytes)
    {
        if (stream.Length < 1 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("The protected candidate record has an invalid size.");
        }
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private bool IsInRoot(string path) =>
        path == Root || path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private void RequireInRoot(string path)
    {
        if (path.Length >= PathCapacity - 4 || !IsInRoot(path)
            || path != Path.GetFullPath(path) || path.AsSpan(2).Contains(':') || path.Contains('/'))
        {
            throw new UnauthorizedAccessException("The candidate receipt path is outside its exact scratch tree.");
        }
    }

    private static SafeFileHandle OpenChecked(string path, bool directory, WindowsCandidateReceiptStorage? scope,
        bool delete = false)
    {
        const uint listDirectoryAndReadControl = 0x00020001;
        const uint genericRead = 0x80000000;
        const uint deleteAccess = 0x00010000;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        const uint backupSemantics = 0x02000000;
        uint access = directory ? listDirectoryAndReadControl : genericRead;
        SafeFileHandle handle = CreateFileW(path, delete ? access | deleteAccess : access,
            directory ? FileShare.ReadWrite : FileShare.Read, 0, openExisting,
            openReparsePoint | (directory ? backupSemantics : 0), 0);
        try
        {
            if (handle.IsInvalid)
            {
                throw new IOException("The candidate receipt path could not be retained.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            const FileAttributes types = FileAttributes.Directory | FileAttributes.ReparsePoint;
            var finalPath = new StringBuilder(PathCapacity);
            uint length = GetFinalPathNameByHandleW(handle, finalPath, (uint)finalPath.Capacity, 0);
            if (!GetFileInformationByHandle(handle, out FileInformation info)
                || (info.Attributes & types) != (directory ? FileAttributes.Directory : 0)
                || (!directory && info.Links != 1) || (info.IndexHigh | info.IndexLow) == 0
                || length == 0 || length >= finalPath.Capacity
                || !string.Equals(finalPath.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Candidate receipt storage requires its exact non-reparse path and a single-link file.");
            }
            scope?.VerifyAccess(handle, directory, path == scope.Root, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private void VerifyAccess(SafeFileHandle handle, bool directory, bool isRoot, string path)
    {
        var descriptor = new RawSecurityDescriptor(
            new HandleSecurity(handle, directory).GetSecurityDescriptorBinaryForm(), 0);

        if (descriptor.Owner != owner || descriptor.DiscretionaryAcl is not { Count: >= 1 and <= 2 } acl
            || (directory && acl.Count != 1)
            || (isRoot && !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)))
        {
            throw new UnauthorizedAccessException("Candidate receipt storage has an unexpected owner or DACL.");
        }

        // File.Replace in the broker state store can preserve an explicit copy
        // of the same inherited grant. Both entries must grant the same access
        // to the fixture owner, and the two flags must be distinct.
        AceFlags expected = directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None;
        AceFlags inheritedFlag = isRoot ? AceFlags.None : AceFlags.Inherited;
        foreach (GenericAce entry in acl)
        {
            if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed
                || ace.SecurityIdentifier != owner || ace.AccessMask != (int)FileSystemRights.FullControl
                || (ace.AceFlags & ~inheritedFlag) != expected
                || (acl.Count == 2 && acl[0].AceFlags == acl[1].AceFlags))
            {
                throw new UnauthorizedAccessException("Candidate receipt storage grants unexpected access.");
            }
        }
    }

    internal sealed class DirectoryLease : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];

        public void Dispose()
        {
            for (int index = Handles.Count - 1; index >= 0; index--)
            {
                Handles[index].Dispose();
            }
        }
    }

    private sealed class HandleSecurity(SafeFileHandle handle, bool directory) : ObjectSecurity<FileSystemRights>(
        directory, ResourceType.FileObject, handle, AccessControlSections.Owner | AccessControlSections.Access)
    {
        internal void WriteAccess(SafeFileHandle target) => Persist(target, AccessControlSections.Access);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RenameInformation
    {
        internal byte ReplaceIfExists;
        internal nint RootDirectory;
        internal uint FileNameLength;
        internal char FileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string path, uint access, FileShare share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref byte information, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        nint information, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? volumeName,
        uint volumeNameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
}
#endif
