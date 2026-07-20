using System.Security.AccessControl;
using System.Security.Principal;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class MachineDataDirectorySecurityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.MachineDataDirectorySecurityTests",
        Guid.NewGuid().ToString("N"));
    private string MachineData => Path.Combine(root, "OpenBlade");

    [Fact]
    public void ProtectedSecurity_UsesAdministratorOwnerAndOnlyPrivilegedFullControl()
    {
        DirectorySecurity security =
            WindowsMachineDataDirectorySecurity.CreateProtectedSecurity();
        WindowsMachineDataDirectorySecurity.VerifyProtectedSecurity(security);
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
        SecurityIdentifier users = new(WellKnownSidType.BuiltinUsersSid, null);

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            administrators,
            security.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        });
        Assert.Contains(rules, rule => rule.IdentityReference == localSystem);
        Assert.Contains(rules, rule => rule.IdentityReference == administrators);
        Assert.DoesNotContain(rules, rule => rule.IdentityReference == users);
    }

    [Fact]
    public void ProtectedFileSecurity_UsesAdministratorOwnerAndOnlyPrivilegedFullControl()
    {
        FileSecurity security =
            WindowsMachineDataDirectorySecurity.CreateProtectedFileSecurity();

        WindowsMachineDataDirectorySecurity.VerifyProtectedFileSecurity(security);
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

        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        });
        Assert.Contains(rules, rule => rule.IdentityReference == localSystem);
        Assert.Contains(rules, rule => rule.IdentityReference == administrators);
    }

    [Fact]
    public async Task ProtectAsync_CancellationBeforeMutation_DoesNotCreateDirectory()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WindowsMachineDataDirectorySecurity().ProtectAsync(
                MachineData,
                cancellation.Token));

        Assert.False(Directory.Exists(MachineData));
    }

    public void Dispose()
    {
        if (Directory.Exists(MachineData))
        {
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current user SID unavailable for ACL cleanup.");
            DirectorySecurity cleanup = new();
            cleanup.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            cleanup.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(MachineData).SetAccessControl(cleanup);
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
