using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenBlade.Setup;

public interface IMachineDataDirectorySecurity
{
    Task ProtectAsync(string directory, CancellationToken cancellationToken);
}

public sealed class WindowsMachineDataDirectorySecurity : IMachineDataDirectorySecurity
{
    public Task ProtectAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();

        DirectoryInfo machineData = Directory.CreateDirectory(directory);
        if ((machineData.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Refusing to secure reparse-point machine data directory '{directory}'.");
        }

        DirectorySecurity security = CreateProtectedSecurity();
        machineData.SetAccessControl(security);

        DirectorySecurity applied = machineData.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        VerifyProtectedSecurity(applied);
        ProtectDescendants(machineData, cancellationToken);
        return Task.CompletedTask;
    }

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
