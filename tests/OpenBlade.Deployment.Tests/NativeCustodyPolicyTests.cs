using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NativeCustodyPolicyTests
{
    [Fact]
    public void SharedCannedDescriptorsRequirePrivateCustodyWithoutChangingPublicRecordPolicy()
    {
        using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-recovery-lifetime-v1.json")));
        Assert.Equal(WindowsNativeAttemptHistory.MaximumAttempts, data.RootElement.GetProperty("maximumAttempts").GetInt32());
        foreach (var item in data.RootElement.GetProperty("descriptors").EnumerateArray())
        {
            var descriptor = new RawSecurityDescriptor(Convert.FromBase64String(item.GetProperty("bytes").GetString()!), 0);
            Assert.Equal(item.GetProperty("privateCustody").GetBoolean(), WindowsNativeEpochFiles.AcceptsPrivateCustody(descriptor));
        }
    }

    [Fact]
    public async Task PrivateCustodyConflictingHandleThenReleasePreservesPublicNeighborAndRefusesLegacyAcl()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        string directory;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid())) { directory = Path.Combine(files.Root, tx.Record.Epoch); }
        string path = Path.Combine(directory, "attempts", "custody.lock");
        string neighbor = Path.Combine(directory, "startup-policy.bin");
        byte[] neighborAcl = new FileInfo(neighbor).GetAccessControl().GetSecurityDescriptorBinaryForm();
        byte[] neighborBytes = File.ReadAllBytes(neighbor);
        using var identity = WindowsIdentity.GetCurrent();
        var descriptor = new RawSecurityDescriptor(new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
        Assert.True(WindowsNativeEpochFiles.AcceptsPrivateCustody(descriptor, identity.User));
        using (var conflicting = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        { Assert.Throws<IOException>(() => files.AcquireServiceCustody(path)); }
        using (var released = files.AcquireServiceCustody(path)) { Assert.NotEqual(0, released.Length); }
        // Only this private test fixture is downgraded to the legacy inherited
        // shape. Production refuses it rather than changing an uncertain lock.
        var legacy = new FileInfo(path).GetAccessControl();
        legacy.SetAccessRuleProtection(false, true);
        new FileInfo(path).SetAccessControl(legacy);
        byte[] before = new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();
        Assert.Throws<UnauthorizedAccessException>(() => files.AcquireServiceCustody(path));
        Assert.Equal(before, new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm());
        Assert.Equal(neighborAcl, new FileInfo(neighbor).GetAccessControl().GetSecurityDescriptorBinaryForm());
        Assert.Equal(neighborBytes, File.ReadAllBytes(neighbor));
        using var publicEvidence = files.OpenFile(neighbor);
    }
}
