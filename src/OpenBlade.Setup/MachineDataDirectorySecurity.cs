using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Setup;

public interface IMachineDataDirectorySecurity
{
    Task ProtectAsync(string directory, CancellationToken cancellationToken, bool includeDescendants = true);
}

public sealed class WindowsMachineDataDirectorySecurity : IMachineDataDirectorySecurity
{
    public Task ProtectAsync(string directory, CancellationToken cancellationToken, bool includeDescendants = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();

        DirectoryInfo machineData = new(directory);
        DirectorySecurity security = CreateProtectedSecurity();
        // New roots start protected. For an existing root, check and update the
        // same non-following handle: ProgramData permits users to create directories.
        machineData.Create(security);
        using SafeFileHandle handle = OpenMachineDataDirectory(machineData.FullName);
        var current = new DirectoryHandleSecurity(handle);
        if (current.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
        {
            throw new UnauthorizedAccessException("The machine data directory is not owned by SYSTEM or Administrators.");
        }

        current.Apply(security);
        DirectorySecurity applied = new();
        applied.SetSecurityDescriptorBinaryForm(new DirectoryHandleSecurity(handle).GetSecurityDescriptorBinaryForm());
        VerifyProtectedSecurity(applied);
        if (includeDescendants)
        {
            ProtectDescendants(machineData, cancellationToken);
        }
        return Task.CompletedTask;
    }

    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfo = 9;

    private static SafeFileHandle OpenMachineDataDirectory(string path)
    {
        const FileSystemRights rights = FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        SafeFileHandle handle = CreateFileW(path, (uint)rights, FileShare.Read, 0,
            OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileAttributeTagInfo,
                out FileAttributeTagInformation info, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Could not open the machine data directory for permission verification.");
        }

        const FileAttributes mask = FileAttributes.Directory | FileAttributes.ReparsePoint;
        if ((info.Attributes & mask) != FileAttributes.Directory)
        {
            handle.Dispose();
            throw new InvalidDataException(
                $"Refusing to secure a reparse point or non-directory at '{path}'.");
        }

        return handle;
    }

    private sealed class DirectoryHandleSecurity(SafeFileHandle handle)
        : ObjectSecurity<FileSystemRights>(true, ResourceType.FileObject, handle, Sections)
    {
        private const AccessControlSections Sections = AccessControlSections.Access | AccessControlSections.Owner;

        internal void Apply(DirectorySecurity security)
        {
            SetSecurityDescriptorBinaryForm(security.GetSecurityDescriptorBinaryForm(), Sections);
            Persist(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal FileAttributes Attributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string path, uint access, FileShare share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out FileAttributeTagInformation information, uint size);

    private static void ProtectDescendants(
        DirectoryInfo directory,
        CancellationToken cancellationToken)
    {
        foreach (DirectoryInfo child in directory.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Refusing to secure reparse-point machine data directory '{child.FullName}'.");
            }

            child.SetAccessControl(CreateProtectedSecurity());
            VerifyProtectedSecurity(child.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner));
            ProtectDescendants(child, cancellationToken);
        }

        foreach (FileInfo child in directory.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Refusing to secure reparse-point machine data file '{child.FullName}'.");
            }

            child.SetAccessControl(CreateProtectedFileSecurity());
            VerifyProtectedFileSecurity(child.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner));
        }
    }

    internal static DirectorySecurity CreateProtectedSecurity()
    {
        DirectorySecurity security = new();
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, administrators);
        return security;
    }

    internal static FileSecurity CreateProtectedFileSecurity()
    {
        FileSecurity security = new();
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, administrators);
        return security;
    }

    private static void AddFullControl(
        DirectorySecurity security,
        SecurityIdentifier identity) => security.AddAccessRule(
            new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

    private static void AddFullControl(
        FileSecurity security,
        SecurityIdentifier identity) => security.AddAccessRule(
            new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

    internal static void VerifyProtectedSecurity(DirectorySecurity security)
    {
        if (!security.AreAccessRulesProtected)
        {
            throw new InvalidDataException(
                "The OpenBlade machine data directory still inherits access rules.");
        }

        FileSystemAccessRule[] rules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        if (security.GetOwner(typeof(SecurityIdentifier)) != administrators
            || rules.Length != 2
            || !HasPropagatingFullControl(rules, localSystem)
            || !HasPropagatingFullControl(rules, administrators)
            || rules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow
                || (rule.IdentityReference != localSystem
                    && rule.IdentityReference != administrators)))
        {
            throw new InvalidDataException(
                "The OpenBlade machine data directory ACL was not applied exactly.");
        }
    }

    internal static void VerifyProtectedFileSecurity(FileSecurity security)
    {
        if (!security.AreAccessRulesProtected)
        {
            throw new InvalidDataException(
                "An OpenBlade machine data file still inherits access rules.");
        }

        FileSystemAccessRule[] rules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        if (security.GetOwner(typeof(SecurityIdentifier)) != administrators
            || rules.Length != 2
            || !HasFileFullControl(rules, localSystem)
            || !HasFileFullControl(rules, administrators)
            || rules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow
                || (rule.IdentityReference != localSystem
                    && rule.IdentityReference != administrators)))
        {
            throw new InvalidDataException(
                "An OpenBlade machine data file ACL was not applied exactly.");
        }
    }

    private static bool HasPropagatingFullControl(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity) => rules.Any(rule =>
            rule.IdentityReference == identity
            && rule.AccessControlType == AccessControlType.Allow
            && (rule.FileSystemRights & FileSystemRights.FullControl)
                == FileSystemRights.FullControl
            && (rule.InheritanceFlags
                    & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));

    private static bool HasFileFullControl(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity) => rules.Any(rule =>
            rule.IdentityReference == identity
            && rule.AccessControlType == AccessControlType.Allow
            && (rule.FileSystemRights & FileSystemRights.FullControl)
                == FileSystemRights.FullControl
            && rule.InheritanceFlags == InheritanceFlags.None
            && rule.PropagationFlags == PropagationFlags.None);
}
