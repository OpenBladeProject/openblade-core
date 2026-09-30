using System.Security.AccessControl;
using System.Security.Principal;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadMutationEvidenceStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.PadEvidenceTests", Guid.NewGuid().ToString("N"));
    public CoolingPadMutationEvidenceStoreTests() => Directory.CreateDirectory(directory);
    private CoolingPadMutationEvidenceStore Store() => new(directory, (_, _) => { }, _ => { });

    [Fact]
    public async Task DurableArmSurvivesNewStoreAndOnlyMatchingResolutionClears()
    {
        var first = Store();
        var empty = await first.LoadAsync(default);
        Assert.False(empty.BlocksAdmission);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        var armed = await first.ArmAsync(empty, default);
        Assert.True(armed.BlocksAdmission);
        var restarted = Store();
        Assert.Equal(armed, await restarted.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.ResolveAsync(empty, default));
        var resolved = await restarted.ResolveAsync(armed, default);
        Assert.False(resolved.BlocksAdmission);
        Assert.Equal(armed.Evidence!.Epoch, resolved.Evidence!.Epoch);
        Assert.Equal(armed.Evidence.Generation + 1, resolved.Evidence.Generation);
        var next = await restarted.ArmAsync(resolved, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => first.ResolveAsync(armed, default));
        Assert.Equal(next, await restarted.LoadAsync(default));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("schema")]
    [InlineData("scope")]
    [InlineData("oversize")]
    [InlineData("unknownProperty")]
    [InlineData("duplicateProperty")]
    [InlineData("mixedCaseDuplicate")]
    [InlineData("casingOnlyProperty")]
    public async Task InvalidEvidenceCannotBecomeCleanOrBeReplaced(string change)
    {
        var store = Store();
        var clean = await store.LoadAsync(default);
        var armed = await store.ArmAsync(clean, default);
        string path = Path.Combine(directory, CoolingPadMutationEvidenceStore.FileName);
        string json = await File.ReadAllTextAsync(path);
        json = change switch
        {
            "corrupt" => "{",
            "schema" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":99"),
            "scope" => json.Replace("RZ09-05289EN4", "OTHER"),
            "oversize" => new string(' ', 4097),
            "duplicateProperty" => json.Insert(1, "\"schemaVersion\":1,"),
            "mixedCaseDuplicate" => json[..^1] + ",\"MutationMayHaveOccurred\":false}",
            "casingOnlyProperty" => json.Replace("\"mutationMayHaveOccurred\":true", "\"MutationMayHaveOccurred\":false"),
            _ => json.Insert(1, "\"unexpected\":true,")
        };
        await File.WriteAllTextAsync(path, json);
        Assert.True((await store.LoadAsync(default)).Invalid);
        await Assert.ThrowsAnyAsync<Exception>(() => store.ResolveAsync(armed, default));
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AclFailureAndMissingParentAreInvalidRatherThanMissingEvidence()
    {
        var denied = new CoolingPadMutationEvidenceStore(directory, (_, _) => throw new UnauthorizedAccessException(), _ => { });
        Assert.True((await denied.LoadAsync(default)).Invalid);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.ArmAsync(new(null, null), default));
        var missing = new CoolingPadMutationEvidenceStore(Path.Combine(directory, "missing"), (_, _) => { }, _ => { });
        Assert.True((await missing.LoadAsync(default)).Invalid);
    }

    [Fact]
    public async Task ActualUnprotectedDirectoryIsNotAdmittedByProductionStore()
    {
        Assert.True((await new CoolingPadMutationEvidenceStore(directory).LoadAsync(default)).Invalid);
    }

    [Fact]
    public async Task ExclusiveLockConflictDoesNotOverwriteEvidence()
    {
        var store = Store();
        var armed = await store.ArmAsync(await store.LoadAsync(default), default);
        using (var held = new FileStream(Path.Combine(directory, CoolingPadMutationEvidenceStore.FileName + ".lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(armed, await Store().LoadAsync(default));
            await Assert.ThrowsAsync<IOException>(() => Store().ResolveAsync(armed, default));
        }
        Assert.Equal(armed, await store.LoadAsync(default));
    }

    [Fact]
    public async Task FileAclIsRevalidatedOnReadAndResolution()
    {
        bool reject = false;
        var store = new CoolingPadMutationEvidenceStore(directory,
            (path, _) => { if (reject && path.EndsWith(".json")) { throw new UnauthorizedAccessException(); } }, _ => { });
        var armed = await store.ArmAsync(await store.LoadAsync(default), default);
        reject = true;
        Assert.True((await store.LoadAsync(default)).Invalid);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ResolveAsync(armed, default));
        reject = false;
        Assert.Equal(armed, await store.LoadAsync(default));
    }

    [ProtectedPadEvidenceFact]
    public async Task ElevatedWorkspaceProtectedDirectorySupportsProductionPersistence()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            "This opt-in test requires an already-elevated Windows test process; it never launches UAC.");
        DirectoryInfo? workspace = new(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, ".git"))
            && !Directory.Exists(Path.Combine(workspace.FullName, ".git"))) { workspace = workspace.Parent; }
        Assert.NotNull(workspace);
        string root = Path.GetFullPath(workspace.FullName);
        string parent = Path.Combine(root, "artifacts", "pad-protected-store-tests");
        string isolated = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Assert.StartsWith(root + Path.DirectorySeparatorChar, isolated, StringComparison.OrdinalIgnoreCase);
        foreach (string existing in new[] { root, Path.Combine(root, "artifacts"), parent })
        {
            if (Directory.Exists(existing))
            { Assert.Equal(0, (int)(File.GetAttributes(existing) & FileAttributes.ReparsePoint)); }
        }
        DirectoryInfo target = Directory.CreateDirectory(isolated);
        DirectorySecurity original = target.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        try
        {
            // Same protected owner and two propagating FullControl grants as
            // WindowsMachineDataDirectorySecurity.CreateProtectedSecurity.
            var security = new DirectorySecurity();
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            target.SetAccessControl(security);
            var store = new CoolingPadMutationEvidenceStore(isolated);
            var clean = await store.LoadAsync(default);
            Assert.False(clean.Invalid);
            Assert.Null(clean.Evidence);
            Assert.Empty(Directory.EnumerateFileSystemEntries(isolated));
            var armed = await store.ArmAsync(clean, default);
            Assert.True(armed.BlocksAdmission);
            var restarted = new CoolingPadMutationEvidenceStore(isolated);
            Assert.Equal(armed, await restarted.LoadAsync(default));
            var resolved = await restarted.ResolveAsync(armed, default);
            Assert.False(resolved.BlocksAdmission);
            Assert.Equal(resolved, await new CoolingPadMutationEvidenceStore(isolated).LoadAsync(default));
        }
        finally
        {
            Assert.Equal(0, (int)(File.GetAttributes(isolated) & FileAttributes.ReparsePoint));
            target.SetAccessControl(original);
            Assert.Empty(Directory.EnumerateDirectories(isolated));
            foreach (string file in Directory.EnumerateFiles(isolated))
            {
                Assert.Equal(0, (int)(File.GetAttributes(file) & FileAttributes.ReparsePoint));
                Assert.StartsWith(CoolingPadMutationEvidenceStore.FileName, Path.GetFileName(file), StringComparison.Ordinal);
                File.Delete(file);
            }
            Directory.Delete(isolated);
        }
    }

    public sealed class ProtectedPadEvidenceFactAttribute : FactAttribute
    {
        public ProtectedPadEvidenceFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("OPENBLADE_TEST_PROTECTED_PAD_STORE") != "1")
            { Skip = "Opt-in only: set OPENBLADE_TEST_PROTECTED_PAD_STORE=1 in an already-elevated test process."; }
        }
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
