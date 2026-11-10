using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Setup;

// Synthetic fixtures exercise pinning mechanics without package provenance.
internal sealed record SetupFixtureServiceBuild(string Directory, long Length, string Sha256);

internal sealed class WindowsSetupServiceImagePin : IDisposable
{
    private const long MaximumImageBytes = 64 * 1024 * 1024;
    private const int PathCapacity = 512;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    internal const string FixtureDirectoryPrefix = "OpenBlade.Setup.ImagePin-";
    internal const string ServiceFileName = "OpenBlade.Service.exe";

    private readonly List<PinnedPath> paths = [];
    private readonly SecurityIdentifier fixtureOwner;
    private readonly SetupFixtureServiceBuild expected;
    private readonly byte[] digest;

    private WindowsSetupServiceImagePin(SetupFixtureServiceBuild expected, SecurityIdentifier fixtureOwner)
    {
        this.expected = expected;
        this.fixtureOwner = fixtureOwner;
        digest = Convert.FromHexString(expected.Sha256);
        ExecutablePath = Path.Combine(expected.Directory, ServiceFileName);
    }

    internal string ExecutablePath { get; }

#if OPENBLADE_PACKAGE_PROOF_FIXTURE
    internal static WindowsSetupServiceImagePin OpenPackageFixture(
        string directory, OpenBlade.Deployment.WindowsNativePackageRoleProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var service = proof.Service;
        if (service.InstalledName != ServiceFileName)
        {
            throw new InvalidDataException("The package proof does not map to the Setup Service image.");
        }

        return OpenPinnedImage(new(directory, service.Length, service.Sha256), cancellationToken);
    }
#endif

    internal static WindowsSetupServiceImagePin OpenSyntheticFixture(
        SetupFixtureServiceBuild expected, CancellationToken cancellationToken) =>
        OpenPinnedImage(expected, cancellationToken);

    private static WindowsSetupServiceImagePin OpenPinnedImage(
        SetupFixtureServiceBuild expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        string directory = expected.Directory;
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string leaf = Path.GetFileName(directory);
        if (directory.Length < 4 || !char.IsAsciiLetter(directory[0])
            || directory[1..3] != @":\" || directory.AsSpan(2).Contains(':') || directory.Contains('/')
            || directory != Path.GetFullPath(directory)
            || !leaf.StartsWith(FixtureDirectoryPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(leaf[FixtureDirectoryPrefix.Length..], "N", out _)
            || expected.Length is <= 0 or > MaximumImageBytes
            || expected.Sha256 is not { Length: 64 } || !expected.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("An exact scratch directory and independent Service-role build are required.",
                nameof(expected));
        }

        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (caller.User is not { } owner || caller.IsSystem
            || new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("The image-pin fixture requires a nonadministrator caller.");
        }

        var pin = new WindowsSetupServiceImagePin(expected, owner);
        if (pin.ExecutablePath.Length >= PathCapacity - 4)
        {
            throw new ArgumentException("The scratch image path exceeds the fixture bound.", nameof(expected));
        }

        const uint driveFixed = 3;
        if (GetDriveTypeW(directory[..3]) != driveFixed)
        {
            throw new IOException("The scratch image requires a fixed local drive.");
        }

        var ancestors = new Stack<string>();
        for (string? path = directory; path is not null; path = Path.GetDirectoryName(path))
        {
            ancestors.Push(path);
        }

        try
        {
            foreach (string path in ancestors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pin.Pin(path, directory: true, upstream: path != directory);
            }
            var fileSystem = new StringBuilder(16);
            if (!GetVolumeInformationByHandleW(pin.paths[0].Handle, null, 0, out _, out _, out _,
                    fileSystem, (uint)fileSystem.Capacity) || fileSystem.ToString() != "NTFS")
            {
                throw new IOException("The scratch image requires a local NTFS volume.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            pin.Pin(pin.ExecutablePath, directory: false, upstream: false);
            pin.Verify(cancellationToken);
            return pin;
        }
        catch
        {
            pin.Dispose();
            throw;
        }
    }

    private void Pin(string path, bool directory, bool upstream)
    {
        // FILE_LIST_DIRECTORY participates in sharing checks. READ_ATTRIBUTES alone
        // would allow a metadata-only handle that doesn't deny a competing delete.
        const uint listDirectoryAndReadControl = 0x00020001;
        const uint genericRead = 0x80000000;
        SafeFileHandle handle = CreateFileW(path, directory ? listDirectoryAndReadControl : genericRead,
            directory ? FileShare.ReadWrite : FileShare.Read, 0, OpenExisting,
            OpenReparsePoint | (directory ? BackupSemantics : 0), 0);
        try
        {
            if (handle.IsInvalid)
            {
                throw new IOException("The scratch image or an ancestor could not be pinned.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            FileSnapshot snapshot = Inspect(handle, path, directory, upstream);
            if (paths.Count != 0 && snapshot.Volume != paths[0].Snapshot.Volume)
            {
                throw new IOException("The scratch image crossed an NTFS volume boundary.");
            }
            paths.Add(new(path, handle, directory, upstream, snapshot));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal string Verify(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertHeld(cancellationToken);
        PinnedPath image = paths[^1];
        if (image.Snapshot.Length != expected.Length)
        {
            throw new IOException("The pinned image length differs from the Service-role build.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[16 * 1024];
        long offset = 0;
        while (offset < expected.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(image.Handle, buffer[..(int)Math.Min(buffer.Length, expected.Length - offset)], offset);
            if (read == 0)
            {
                throw new IOException("The pinned image ended before the expected length.");
            }
            hash.AppendData(buffer[..read]);
            offset += read;
        }

        byte[] actual = hash.GetHashAndReset();
        AssertHeld(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CryptographicOperations.FixedTimeEquals(actual, digest))
        {
            throw new IOException("The pinned image digest differs from the Service-role build.");
        }
        return Convert.ToHexString(actual);
    }

    private void AssertHeld(CancellationToken cancellationToken)
    {
        foreach (PinnedPath pin in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Inspect(pin.Handle, pin.Path, pin.Directory, pin.Upstream) != pin.Snapshot)
            {
                throw new IOException("The pinned image or ancestor identity changed.");
            }

            // A metadata-only name lookup confirms that the name still reaches
            // the retained object. All digest reads use the original image handle.
            const uint readAttributes = 0x80;
            using SafeFileHandle current = CreateFileW(pin.Path, readAttributes, FileShare.ReadWrite | FileShare.Delete,
                0, OpenExisting, OpenReparsePoint | (pin.Directory ? BackupSemantics : 0), 0);
            if (ReadSnapshot(current, pin.Directory) != pin.Snapshot)
            {
                throw new IOException("The pinned image or ancestor name was rebound.");
            }
        }
    }

    private FileSnapshot Inspect(SafeFileHandle handle, string path, bool directory, bool upstream)
    {
        FileSnapshot snapshot = ReadSnapshot(handle, directory);
        var finalPath = new StringBuilder(PathCapacity);
        uint length = GetFinalPathNameByHandleW(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (length == 0 || length >= finalPath.Capacity
            || !string.Equals(finalPath.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The image pin resolved to a different path.");
        }

        VerifyAccess(handle, directory, upstream);
        return snapshot;
    }

    private static FileSnapshot ReadSnapshot(SafeFileHandle handle, bool directory)
    {
        const FileAttributes types = FileAttributes.Directory | FileAttributes.ReparsePoint;
        if (handle.IsClosed || handle.IsInvalid || !GetFileInformationByHandle(handle, out FileInformation info)
            || (info.Attributes & types) != (directory ? FileAttributes.Directory : 0)
            || (!directory && info.Links != 1) || (info.IndexHigh | info.IndexLow) == 0)
        {
            throw new IOException("The image pin requires the original non-reparse directory or single-link file.");
        }

        return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
            directory ? 0 : ((long)info.SizeHigh << 32) | info.SizeLow);
    }

    private void VerifyAccess(SafeFileHandle handle, bool directory, bool upstream)
    {
        var security = new RawSecurityDescriptor(new HandleSecurity(handle, directory).GetSecurityDescriptorBinaryForm(), 0);
        if (security.Owner is not { } owner || !IsTrusted(owner) || (!upstream && owner != fixtureOwner)
            || security.DiscretionaryAcl is not { } acl)
        {
            throw new UnauthorizedAccessException("The scratch image or ancestor has an untrusted owner or DACL.");
        }

        // Windows' volume root may let ordinary users create subdirectories.
        // That permission neither permits changing an existing child nor applies
        // to the scratch directory or image.
        const int genericReadExecute = unchecked((int)0xA0000000);
        const int createSubdirectory = 0x4;
        int harmless = genericReadExecute | (int)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
        if (upstream)
        {
            harmless |= createSubdirectory;
        }
        foreach (GenericAce ace in acl)
        {
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0)
            {
                continue;
            }
            if (ace is not QualifiedAce rule || (rule.AceQualifier != AceQualifier.AccessDenied
                && !IsTrusted(rule.SecurityIdentifier) && (rule.AccessMask & ~harmless) != 0))
            {
                throw new UnauthorizedAccessException("The scratch image or ancestor grants untrusted write access.");
            }
        }
    }

    private bool IsTrusted(SecurityIdentifier sid) =>
        sid == fixtureOwner || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid == TrustedInstaller;

    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public void Dispose()
    {
        // Retain ancestry until the image is released, including on partial acquisition.
        for (int index = paths.Count - 1; index >= 0; index--)
        {
            paths[index].Handle.Dispose();
        }
    }

    private sealed class HandleSecurity(SafeFileHandle handle, bool directory) : ObjectSecurity<FileSystemRights>(
        directory, ResourceType.FileObject, handle, AccessControlSections.Owner | AccessControlSections.Access);

    private sealed record PinnedPath(string Path, SafeFileHandle Handle, bool Directory, bool Upstream, FileSnapshot Snapshot);
    private readonly record struct FileSnapshot(uint Volume, ulong Index, long Length);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string path, uint access, FileShare share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? volumeName,
        uint volumeNameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
}
