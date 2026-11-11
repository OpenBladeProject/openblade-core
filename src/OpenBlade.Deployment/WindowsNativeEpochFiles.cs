
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Deployment;

internal enum NativeFileBoundary { Created, Written, Flushed, Verified, Renamed }

// Retained non-reparse NTFS files and namespaces for native package/epoch evidence.
// Private fixtures explicitly use the current-token owner; product paths require privileged owners.
[SupportedOSPlatform("windows")]
internal sealed class WindowsNativeEpochFiles : IDisposable
{
    private const string RootPrefix = "OpenBlade.NativeEpochFixture-";
    private const int PathCapacity = 512;
    private const int MaximumRecordBytes = 16384;
    private readonly SecurityIdentifier? owner;
    private readonly List<IDisposable> retained = [];

    private WindowsNativeEpochFiles(string root, SecurityIdentifier? owner)
    {
        Root = root;
        this.owner = owner;
    }

    internal string Root { get; }
#if OPENBLADE_NATIVE_PACKAGE_TESTS
    internal NativeFileBoundary? CrashBoundaryForTests { get; set; }
#endif
    private void AtBoundary(NativeFileBoundary boundary)
    {
#if OPENBLADE_NATIVE_PACKAGE_TESTS
        if (CrashBoundaryForTests == boundary) { throw new IOException("Injected private-filesystem publication failure."); }
#endif
    }

    // Product callers use only the fixed protected namespace. Its provisioning is
    // an installer operation; this reader never creates an unprotected product root.
    internal static string ProductRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBlade", "NativeEpochs");
    internal static WindowsNativeEpochFiles OpenProduct() => OpenProtected(ProductRoot);

    internal static WindowsNativeEpochFiles OpenProtected(string root)
    {
        var value = new WindowsNativeEpochFiles(Path.GetFullPath(root), null);
        try
        {
            var lease = value.PinDirectory(value.Root);
            value.retained.Add(lease);
            RequireNtfs(lease);
            return value;
        }
        catch { value.Dispose(); throw; }
    }

    // Private-filesystem fixtures have their own current-token owner, never a
    // product path or a persistent machine trust-store change.
    internal static WindowsNativeEpochFiles CreatePrivateFixture()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException();
        string root = Path.Combine(Path.GetTempPath(), RootPrefix + Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).Create(security);
        var value = new WindowsNativeEpochFiles(root, user);
        var lease = value.PinDirectory(root);
        value.retained.Add(lease);
        RequireNtfs(lease);
        return value;
    }

    internal static WindowsNativeEpochFiles CreateOrOpenProductForInstall()
    {
        string parent = Path.GetDirectoryName(ProductRoot)!;
        using var protectedParent = OpenProtected(parent);
        if (!Directory.Exists(ProductRoot))
        {
            RequireAbsent(ProductRoot);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var security = new DirectorySecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(ProductRoot).Create(security);
        }
        return OpenProduct();
    }

    private static void RequireNtfs(DirectoryLease lease)
    {
        var fileSystem = new StringBuilder(16);
        if (!GetVolumeInformationByHandleW(lease.Handles[^1], null, 0, out _, out _, out _, fileSystem, (uint)fileSystem.Capacity)
            || fileSystem.ToString() != "NTFS")
        { throw new IOException("Native epoch storage requires NTFS."); }
    }

    public void Dispose()
    {
        for (int i = retained.Count - 1; i >= 0; i--) { retained[i].Dispose(); }
        retained.Clear();
    }

    internal DirectoryLease RetainDirectory(string path) => PinDirectory(path);
    internal void CreateDirectoryOnce(string path)
    {
        RequireInRoot(path);
        using var parent = PinDirectory(Path.GetDirectoryName(path)!);
        // Reserve the name permanently before constructing children. Reservation
        // files are never removed or reused, including failed preparations.
        string claim = path + ".reserved";
        using (var file = new FileStream(claim, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Flush(true); }
        RequireAbsent(path);
        Directory.CreateDirectory(path);
        using var check = PinDirectory(path);
    }

    internal FileStream AcquireWriter()
    {
        string path = Path.Combine(Root, "writer.lock");
        if (!File.Exists(path))
        {
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created.Flush(true);
        }
        // Native lifecycle authority is not inferred from this filesystem lock.
        var handle = OpenChecked(path, false, this, exclusive: true);
        return new FileStream(handle, FileAccess.Read);
    }

    internal byte[]? ReadCurrent()
    {
        string path = Path.Combine(Root, "current.bin");
        if (!File.Exists(path)) { return null; }
        using var file = new FileStream(OpenChecked(path, false, this, mutable: true), FileAccess.Read);
        return ReadBounded(file, 16384);
    }

    internal void PublishCurrent(byte[] bytes) => Publish(Path.Combine(Root, "current.bin"), bytes, replace: true);

    internal void WriteEvidence(string path, Stream source)
    {
        RequireInRoot(path);
        using var directory = PinDirectory(Path.GetDirectoryName(path)!);
        using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.WriteThrough))
        {
            source.CopyTo(destination);
            destination.Flush(true);
        }
        using var check = OpenFile(path);
    }


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

    private static void PinAncestors(string path, DirectoryLease lease, WindowsNativeEpochFiles? scope)
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

    internal void Publish(string path, byte[] bytes, bool replace = false)
    {
        if (bytes.Length is < 1 or > MaximumRecordBytes)
        {
            throw new InvalidDataException("Native epoch exceeds the publication size bound.");
        }
        RequireInRoot(path);
        string directory = Path.GetDirectoryName(path)!;
        using var lease = PinDirectory(directory);
        if (!replace) { RequireAbsent(path); }
        string temporary = Path.Combine(directory, "receipt-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                created = true;
                AtBoundary(NativeFileBoundary.Created);
                stream.Write(bytes);
                AtBoundary(NativeFileBoundary.Written);
                stream.Flush(flushToDisk: true);
                AtBoundary(NativeFileBoundary.Flushed);
            }
            using var retained = new FileStream(OpenChecked(temporary, directory: false, this, delete: true),
                FileAccess.Read);
            RequireExact(retained, bytes);
            AtBoundary(NativeFileBoundary.Verified);
            // Publish the verified object itself while its handle and the
            // containing directory are retained. Only current.bin may replace.
            PublishByHandle(retained.SafeFileHandle, path, replace);
            created = false;
            AtBoundary(NativeFileBoundary.Renamed);
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

    private static void PublishByHandle(SafeFileHandle handle, string destination, bool replace)
    {
        const int fileRenameInfo = 3;
        int nameOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName));
        int lengthOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileNameLength));
        byte[] name = Encoding.Unicode.GetBytes(destination + '\0');
        int size = nameOffset + name.Length;
        nint information = Marshal.AllocHGlobal(size);
        try
        {
            // Initialize ReplaceIfExists and zero RootDirectory. The exact absolute
            // destination is below the directory lease held by Publish.
            Marshal.Copy(new byte[nameOffset], 0, information, nameOffset);
            Marshal.WriteByte(information, replace ? (byte)1 : (byte)0);
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

    internal static void RequireAbsent(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        throw new IOException("Native epoch storage already contains the fixed name.");
    }

    internal static void RequireExact(FileStream stream, byte[] expected)
    {
        if (expected.Length is < 1 or > MaximumRecordBytes || stream.Length != expected.Length)
        {
            throw new InvalidDataException("Native epoch length differs from the issued record.");
        }
        Span<byte> bytes = stackalloc byte[expected.Length];
        stream.ReadExactly(bytes);
        if (!bytes.SequenceEqual(expected))
        {
            throw new InvalidDataException("Native epoch differs from the issued record.");
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
            throw new UnauthorizedAccessException("The native epoch path is outside its exact scratch tree.");
        }
    }

    private static SafeFileHandle OpenChecked(string path, bool directory, WindowsNativeEpochFiles? scope,
        bool delete = false, bool mutable = false, bool exclusive = false)
    {
        const uint listDirectoryAndReadControl = 0x00020001;
        const uint genericRead = 0x80000000;
        const uint deleteAccess = 0x00010000;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        const uint backupSemantics = 0x02000000;
        uint access = directory ? listDirectoryAndReadControl : genericRead;
        SafeFileHandle handle = CreateFileW(path, delete ? access | deleteAccess : access,
            exclusive ? FileShare.None : directory ? FileShare.ReadWrite : mutable ? FileShare.Read | FileShare.Delete : FileShare.Read, 0, openExisting,
            openReparsePoint | (directory ? backupSemantics : 0), 0);
        try
        {
            if (handle.IsInvalid)
            {
                throw new IOException("The native epoch path could not be retained.",
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
                throw new IOException("Native epoch storage requires its exact non-reparse path and a single-link file.");
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

        if (owner is null)
        {
            static bool Trusted(SecurityIdentifier? sid) => sid?.Value is "S-1-5-18" or "S-1-5-32-544"
                or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
            if (!Trusted(descriptor.Owner) || descriptor.DiscretionaryAcl is null)
            { throw new UnauthorizedAccessException("Native epoch owner or DACL is untrusted."); }
            const int writes = 0x40000000 | 0x10000000 | 0x000D0156;
            foreach (GenericAce entry in descriptor.DiscretionaryAcl)
            {
                if (entry is not CommonAce ace || ace.IsCallback
                    || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                { throw new UnauthorizedAccessException("Unsupported native epoch ACL."); }
                if (ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AceFlags & AceFlags.InheritOnly) == 0
                    && (ace.AccessMask & writes) != 0 && !Trusted(ace.SecurityIdentifier))
                { throw new UnauthorizedAccessException("Native epoch permits unprivileged mutation."); }
            }
            return;
        }
        if (descriptor.Owner != owner || descriptor.DiscretionaryAcl is not { Count: >= 1 and <= 2 } acl
            || (directory && acl.Count != 1)
            || (isRoot && !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)))
        { throw new UnauthorizedAccessException("Private native epoch fixture owner or DACL differs."); }
        AceFlags expected = directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None;
        AceFlags inheritedFlag = isRoot ? AceFlags.None : AceFlags.Inherited;
        foreach (GenericAce entry in acl)
        {
            if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed
                || ace.SecurityIdentifier != owner || ace.AccessMask != (int)FileSystemRights.FullControl
                || (ace.AceFlags & ~inheritedFlag) != expected
                || (acl.Count == 2 && acl[0].AceFlags == acl[1].AceFlags))
            { throw new UnauthorizedAccessException("Private native epoch fixture grants unexpected access."); }
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
