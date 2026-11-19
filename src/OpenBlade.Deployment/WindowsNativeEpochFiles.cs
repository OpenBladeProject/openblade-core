
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Deployment;

internal enum ManagedBootstrapBoundary { IntentPublished, DirectoryCreated, ExclusionCreated, RootPublished, Completed }

internal enum NativeFileBoundary { Created, Written, Flushed, Verified, Renamed, OwnerDirectoryCreated }

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
    internal bool IsPrivateFixture => owner is not null;
#if OPENBLADE_NATIVE_PACKAGE_TESTS
    internal NativeFileBoundary? CrashBoundaryForTests { get; set; }
    internal ManagedBootstrapBoundary? BootstrapCrashForTests { get; set; }
    internal Action<string>? PublishedForTests { get; set; }
#endif
    private void AtBoundary(NativeFileBoundary boundary)
    {
#if OPENBLADE_NATIVE_PACKAGE_TESTS
        if (CrashBoundaryForTests == boundary) { throw new IOException("Injected private-filesystem publication failure."); }
#endif
    }

    private void AtBootstrapBoundary(ManagedBootstrapBoundary boundary)
    {
#if OPENBLADE_NATIVE_PACKAGE_TESTS
        if (BootstrapCrashForTests == boundary) { throw new IOException("Injected authenticated handoff bootstrap interruption."); }
#endif
    }

    // Product callers use only the fixed protected namespace. Its provisioning is
    // an installer operation; this reader never creates an unprotected product root.
    internal static string ProductRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBlade.NativeEpochs");
    internal static string LegacyProductRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBlade", "NativeEpochs");
    internal static WindowsNativeEpochFiles OpenProduct()
    {
        var files = OpenProtected(ProductRoot);
        try
        {
            using var lease = files.RetainDirectory(files.Root);
            var descriptor = new RawSecurityDescriptor(new HandleSecurity(lease.Handles[^1], true).GetSecurityDescriptorBinaryForm(), 0);
            if (!AcceptsPublicRoot(descriptor)) { throw new UnauthorizedAccessException("Native public root DACL differs."); }
            files.RepairInterruptedOwnerBootstrap();
            return files;
        }
        catch { files.Dispose(); throw; }
    }

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
#if OPENBLADE_NATIVE_PACKAGE_TESTS
        if (FixtureFactoryForTests.Value is { } create) { return create(); }
#endif
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

#if OPENBLADE_NATIVE_PACKAGE_TESTS
    // Explicit private own-SID fixture inputs. No token/default/product discovery.
    internal static AsyncLocal<Func<WindowsNativeEpochFiles>?> FixtureFactoryForTests { get; } = new();
    internal static WindowsNativeEpochFiles CreateSuppliedPrivateFixture(string root, string sid)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.GetFileName(root).StartsWith(RootPrefix, StringComparison.Ordinal)
            || !sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) { throw new InvalidOperationException("Explicit private fixture required."); }
        var user = new SecurityIdentifier(sid);
        var security = new DirectorySecurity(); security.SetOwner(user); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        RequireAbsent(root);
        new DirectoryInfo(root).Create(security);
        var value = new WindowsNativeEpochFiles(root, user);
        try { var lease = value.PinDirectory(root); value.retained.Add(lease); RequireNtfs(lease); return value; }
        catch { value.Dispose(); throw; }
    }
#endif
#if OPENBLADE_NATIVE_PACKAGE_TESTS
    internal WindowsNativeEpochFiles RetainFixtureView()
    {
        if (owner is null) { throw new InvalidOperationException("Only private fixtures admit injected views."); }
        var view = new WindowsNativeEpochFiles(Root, owner);
        view.retained.Add(view.PinDirectory(Root));
        return view;
    }
#endif

    internal static bool IsPresentDirectory(string path) => ObserveDirectoryPresence(() => File.GetAttributes(path));

    // Directory.Exists suppresses access/I/O failures. Only explicit not-found
    // evidence permits absence; unreadable historical state must not be reset.
    internal static bool ObserveDirectoryPresence(Func<FileAttributes> read)
    {
        FileAttributes attributes;
        try { attributes = read(); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
        { throw new UnauthorizedAccessException("Native root is not a plain directory."); }
        return true;
    }

    internal static void RequireLegacyStoreAbsent() => RequireLegacyStoreAbsent(
        () => IsPresentDirectory(Path.GetDirectoryName(LegacyProductRoot)!),
        () => OpenProtected(Path.GetDirectoryName(LegacyProductRoot)!),
        () => IsPresentDirectory(LegacyProductRoot));

    internal static void RequireLegacyStoreAbsent(Func<bool> parentPresent, Func<IDisposable> pinParent, Func<bool> legacyPresent)
    {
        using var parent = parentPresent() ? pinParent() : null;
        if (legacyPresent())
        { throw new IOException("Historical native epoch storage requires explicit transition; history was preserved."); }
    }

    internal static WindowsNativeEpochFiles CreateOrOpenProductForInstall()
    {
        string parent = Path.GetDirectoryName(ProductRoot)!;
        // ProgramData is a shared creation ancestor, not a private product root.
        using var ancestors = new DirectoryLease();
        PinAncestors(parent, ancestors, null);
        for (int i = 0; i < ancestors.Handles.Count; i++)
        { VerifySharedAncestor(ancestors.Handles[i], i + 1 == ancestors.Handles.Count); }
        // Pin a present legacy private parent before deciding the old child is
        // absent. A redirected/unreadable old ancestor is not transition evidence.
        RequireLegacyStoreAbsent();
        if (!IsPresentDirectory(ProductRoot))
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
        var files = OpenProduct();
        try
        {
            using (var rootLease = files.RetainDirectory(files.Root))
            {
                var descriptor = new RawSecurityDescriptor(new HandleSecurity(rootLease.Handles[^1], true).GetSecurityDescriptorBinaryForm(), 0);
                if (!AcceptsPublicRoot(descriptor)) { throw new UnauthorizedAccessException("Native public root DACL differs from its exact installer recipe."); }
            }
            string transition = Path.Combine(files.Root, "root.bin");
            byte[] expected = NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent");
            if (!File.Exists(transition)) { files.Publish(transition, expected); }
            using var record = files.OpenFile(transition);
            RequireExact(record, expected);
            files.EnsureOwnerStore();
            return files;
        }
        catch { files.Dispose(); throw; }
    }

    // The installer-protected machine-data namespace is the creation anchor.
    // Never create a migration leaf directly under writable ProgramData.
    internal static string ManagedHandoffAnchor => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBlade");
    internal static string ManagedHandoffRoot => Path.Combine(ManagedHandoffAnchor, "ManagedHandoff");
    internal static string LegacyManagedHandoffRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBlade.ManagedHandoff");
    internal static WindowsNativeEpochFiles CreateOrOpenManagedHandoff()
    {
        using var anchor = OpenProtected(ManagedHandoffAnchor);
        return anchor.CreateOrOpenManagedHandoffChild();
    }
    internal WindowsNativeEpochFiles CreateOrOpenManagedHandoffChild()
    {
        using var anchor = RetainDirectory(Root);
        using var writer = AcquireWriter();
        string child = Path.Combine(Root, "ManagedHandoff");
        string intent = Path.Combine(Root, "managed-handoff-bootstrap-v2.bin");
        string completed = Path.Combine(Root, "managed-handoff-complete-v2.bin");
        byte[] creation = NativeCanonical.Lines("OpenBlade.ManagedMailboxBootstrap.v2", "protected-installer-anchor", "ManagedHandoff");
        byte[] complete = NativeCanonical.Lines("OpenBlade.ManagedMailboxBootstrapComplete.v2", NativeCanonical.Digest(creation));
        if (!Present(intent))
        {
            // A pre-existing leaf without this positive creation intent is never
            // adopted, even if its owner could subsequently be made privileged.
            RequireAbsent(child);
            RequireAbsent(completed);
            PublishCustody(intent, creation);
            AtBootstrapBoundary(ManagedBootstrapBoundary.IntentPublished);
        }
        using (var held = OpenFile(intent)) { RequireExact(held, creation); }
        if (Present(completed))
        {
            using var held = OpenFile(completed); RequireExact(held, complete);
            if (!IsPresentDirectory(child)) { throw new IOException("Completed handoff namespace disappeared; no bootstrap reset."); }
        }
        if (!IsPresentDirectory(child))
        {
            RequireAbsent(child);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var security = new DirectorySecurity();
            security.SetOwner(owner ?? administrators);
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in owner is null ? new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) } : new[] { owner })
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            new DirectoryInfo(child).Create(security);
            AtBootstrapBoundary(ManagedBootstrapBoundary.DirectoryCreated);
        }
        var files = new WindowsNativeEpochFiles(child, owner);
        try
        {
            files.retained.Add(files.PinDirectory(child));
            using var childWriter = files.AcquireWriter();
            string marker = Path.Combine(child, "handoff-root.bin");
            byte[] markerBytes = NativeCanonical.Lines("OpenBlade.ManagedMailboxRoot.v2", NativeCanonical.Digest(creation));
            if (!Present(completed))
            {
                // Only this creation prefix can precede completion. Interrupted
                // publication bytes remain unchanged; they grant no run authority.
                foreach (string entry in Directory.EnumerateFileSystemEntries(child).Take(33))
                {
                    string name = Path.GetFileName(entry);
                    if (name is "writer.lock" or "exclusion.lock" or "handoff-root.bin") { continue; }
                    if (!name.StartsWith("receipt-", StringComparison.Ordinal) || !name.EndsWith(".tmp", StringComparison.Ordinal))
                    { throw new IOException("Unknown interrupted handoff bootstrap was preserved."); }
                    using var residue = files.OpenFile(entry);
                    byte[] bytes = residue.Length == 0 ? [] : ReadBounded(residue);
                    if (bytes.Length > markerBytes.Length || !markerBytes.AsSpan(0, bytes.Length).SequenceEqual(bytes))
                    { throw new IOException("Unmatched bootstrap residue was preserved."); }
                }
                if (Directory.EnumerateFileSystemEntries(child).Take(33).Count() > 32) { throw new IOException("Bootstrap residue exceeds its bound."); }
                string exclusion = Path.Combine(child, "exclusion.lock");
                if (!Present(exclusion))
                {
                    // A zero-byte private mutex has no partial payload boundary.
                    // Its existence is exclusion only, never a drain certificate.
                    using var created = files.CreatePublicationFile(exclusion, privateCustody: true);
                    created.Flush(true);
                    AtBootstrapBoundary(ManagedBootstrapBoundary.ExclusionCreated);
                }
                using (var custody = files.AcquireServiceCustody(exclusion))
                { if (custody.Length != 0) { throw new IOException("Bootstrap exclusion differs."); } }
                if (!Present(marker)) { files.PublishCustody(marker, markerBytes); AtBootstrapBoundary(ManagedBootstrapBoundary.RootPublished); }
                using (var held = files.OpenFile(marker)) { RequireExact(held, markerBytes); }
                PublishCustody(completed, complete);
                AtBootstrapBoundary(ManagedBootstrapBoundary.Completed);
            }
            using (var held = files.OpenFile(marker)) { RequireExact(held, markerBytes); }
            return files;
        }
        catch { files.Dispose(); throw; }
    }

    internal WindowsNativeEpochFiles RetainView()
    {
        var view = new WindowsNativeEpochFiles(Root, owner);
        view.retained.Add(view.PinDirectory(Root));
        return view;
    }

    // Opens a later view of this same root in this same trust mode, after this
    // instance may be disposed. It repeats the original open's exact-path,
    // non-reparse and NTFS checks, so tests and the product run one path.
    internal Func<WindowsNativeEpochFiles> ReopenLater()
    {
        string root = Root;
        SecurityIdentifier? trust = owner;
        return () =>
        {
            var view = new WindowsNativeEpochFiles(root, trust);
            try
            {
                var lease = view.PinDirectory(root);
                view.retained.Add(lease);
                RequireNtfs(lease);
                return view;
            }
            catch { view.Dispose(); throw; }
        };
    }

    internal static bool AcceptsPublicRoot(RawSecurityDescriptor descriptor)
    {
        if (descriptor.Owner?.Value != "S-1-5-32-544" || !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            || descriptor.DiscretionaryAcl is not { Count: 3 } acl) { return false; }
        var seen = new HashSet<string>();
        foreach (GenericAce entry in acl)
        {
            if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed
                || ace.AceFlags != (AceFlags.ContainerInherit | AceFlags.ObjectInherit) || !seen.Add(ace.SecurityIdentifier.Value)) { return false; }
            if (ace.SecurityIdentifier.Value is "S-1-5-18" or "S-1-5-32-544")
            { if (ace.AccessMask != (int)FileSystemRights.FullControl) { return false; } }
            else if (ace.SecurityIdentifier.Value != "S-1-5-32-545" || ace.AccessMask != (int)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) { return false; }
        }
        return true;
    }
    private static void VerifySharedAncestor(SafeFileHandle handle, bool programData)
    {
        var descriptor = new RawSecurityDescriptor(new HandleSecurity(handle, true).GetSecurityDescriptorBinaryForm(), 0);
        RequireSharedAncestor(descriptor, programData);
    }
    internal static void RequireSharedAncestor(RawSecurityDescriptor descriptor, bool programData)
    {
        static bool Trusted(SecurityIdentifier? sid) => sid?.Value is "S-1-5-18" or "S-1-5-32-544"
            or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        if (!Trusted(descriptor.Owner) || descriptor.DiscretionaryAcl is null) { throw new UnauthorizedAccessException("Untrusted native root ancestry."); }
        // Only the fixed ProgramData creation ancestor admits ordinary WEA/WA
        // metadata grants. Private/product/token policies remain strict.
        int forbidden = (0x40000000 | 0x10000000 | 0x000D0156) & ~(programData ? 0x116 : 4);
        foreach (GenericAce entry in descriptor.DiscretionaryAcl)
        {
            if (entry is not CommonAce ace || ace.IsCallback) { throw new UnauthorizedAccessException("Unsupported ancestor ACE."); }
            if (ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AceFlags & AceFlags.InheritOnly) == 0
                && !Trusted(ace.SecurityIdentifier) && (ace.AccessMask & forbidden) != 0) { throw new UnauthorizedAccessException("Native root ancestor permits unprivileged replacement."); }
        }
    }

    internal void EnsureOwnerStore()
    {
        string path = Path.Combine(Root, "owner");
        if (!Directory.Exists(path))
        {
            using var writer = AcquireWriter();
            using (var marker = OpenFile(Path.Combine(Root, "root.bin")))
            { RequireExact(marker, NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent")); }
            if (!Directory.EnumerateFileSystemEntries(Root).Select(Path.GetFileName).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "root.bin", "writer.lock" }))
            { throw new IOException("Owner initialization requires a never-selected native bootstrap."); }
            RequireAbsent(path);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var security = new DirectorySecurity();
            security.SetOwner(owner ?? administrators);
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in owner is null ? new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) } : new[] { owner })
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            new DirectoryInfo(path).Create(security);
            AtBoundary(NativeFileBoundary.OwnerDirectoryCreated);
        }
        using var directory = RetainDirectory(path);
        string lockPath = Path.Combine(path, "writer.lock");
        if (!Present(lockPath)) { RepairInterruptedOwnerBootstrap(); }
        // Absence of a native history is NOT a managed-drain certificate.
    }

    // Only an authenticated never-selected bootstrap may acquire its missing lock.
    // Epoch namespace exclusion precedes creation; historical ownership is never reset.
    internal void RepairInterruptedOwnerBootstrap()
    {
        string ownerPath = Path.Combine(Root, "owner");
        if (!Present(ownerPath) || Present(Path.Combine(ownerPath, "writer.lock"))) { return; }
        using var writer = AcquireWriter();
        using var directory = RetainDirectory(ownerPath);
        using (var marker = OpenFile(Path.Combine(Root, "root.bin")))
        { RequireExact(marker, NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent")); }
        if (!Directory.EnumerateFileSystemEntries(Root).Select(Path.GetFileName).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "owner", "root.bin", "writer.lock" })
            || Directory.EnumerateFileSystemEntries(ownerPath).Any())
        { throw new IOException("Interrupted native bootstrap has unknown history; no owner lock was created."); }
        string path = Path.Combine(ownerPath, "writer.lock");
        using (var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { created.Flush(true); }
        // Validate the original lock and complete bootstrap under both boundaries.
        // AcquireRetiredOwner accepts known-empty history without fabricating a record.
        using var original = WindowsNativeTrustLifecycle.AcquireRetiredOwner(this);
        using var check = OpenFile(Path.Combine(Root, "root.bin"));
        RequireExact(check, NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        if (!Directory.EnumerateFileSystemEntries(Root).Select(Path.GetFileName).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "owner", "root.bin", "writer.lock" })
            || Directory.EnumerateFileSystemEntries(ownerPath).Any(p => Path.GetFileName(p) != "writer.lock"))
        { throw new IOException("Native bootstrap changed during initialization; history was preserved."); }
    }

    private static bool Present(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
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

    internal FileStream AcquireServiceCustody(string path, bool authenticatedLegacyCleanup = false) =>
        new(OpenChecked(path, false, this, exclusive: true, privateCustody: true, legacyCustody: authenticatedLegacyCleanup), FileAccess.Read);

    // SAME lock as native durable Store::open. Recovery never creates it.
    internal FileStream AcquireOwnerWriter() =>
        new(OpenChecked(Path.Combine(Root, "owner", "writer.lock"), false, this, exclusive: true), FileAccess.Read);

    internal byte[]? ReadCurrent()
    {
        string path = Path.Combine(Root, "current.bin");
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
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

    internal FileStream RetainFile(string path)
    {
        var file = OpenFile(path);
        retained.Add(file);
        return file;
    }

    internal void PublishCustody(string path, byte[] bytes) => Publish(path, bytes, privateCustody: true);

    internal void Publish(string path, byte[] bytes, bool replace = false, bool privateCustody = false)
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
            using (var stream = CreatePublicationFile(temporary, privateCustody))
            {
                created = true;
                AtBoundary(NativeFileBoundary.Created);
                stream.Write(bytes);
                AtBoundary(NativeFileBoundary.Written);
                stream.Flush(flushToDisk: true);
                AtBoundary(NativeFileBoundary.Flushed);
            }
            using var retained = new FileStream(OpenChecked(temporary, directory: false, this, delete: true, privateCustody: privateCustody),
                FileAccess.Read);
            RequireExact(retained, bytes);
            AtBoundary(NativeFileBoundary.Verified);
            // Publish the verified object itself while its handle and the
            // containing directory are retained. Only a caller that passes
            // replace (a current or selection pointer, or a handback
            // resolution) may replace an existing name.
            PublishByHandle(retained.SafeFileHandle, path, replace);
            created = false;
            AtBoundary(NativeFileBoundary.Renamed);
#if OPENBLADE_NATIVE_PACKAGE_TESTS
            PublishedForTests?.Invoke(path);
#endif
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
        // SDK FILE_INFO_BY_HANDLE_CLASS.FileRenameInfoEx, FILE_RENAME_INFO.Flags.
        // POSIX replacement permits the old delete-sharing reader to keep its
        // object while fresh opens select the new generation. No retry/fallback.
        const int fileRenameInfoEx = 22;
        const int replaceIfExistsAndPosixSemantics = 0x1 | 0x2;
        int nameOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName));
        int lengthOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileNameLength));
        byte[] name = Encoding.Unicode.GetBytes(destination + '\0');
        int size = nameOffset + name.Length;
        nint information = Marshal.AllocHGlobal(size);
        try
        {
            // Initialize Flags and zero RootDirectory. The exact absolute
            // destination is below the directory lease held by Publish.
            Marshal.Copy(new byte[nameOffset], 0, information, nameOffset);
            Marshal.WriteInt32(information, replace ? replaceIfExistsAndPosixSemantics : 0);
            Marshal.WriteInt32(information, lengthOffset, name.Length - sizeof(char));
            Marshal.Copy(name, 0, information + nameOffset, name.Length);
            if (!SetFileInformationByHandle(handle, fileRenameInfoEx, information, (uint)size))
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
        bool delete = false, bool mutable = false, bool exclusive = false, bool privateCustody = false, bool legacyCustody = false)
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
            scope?.VerifyAccess(handle, directory, path == scope.Root, path, privateCustody || Path.GetFileName(path) == "custody.lock", legacyCustody);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private FileStream CreatePublicationFile(string path, bool privateCustody)
    {
        if (!privateCustody) { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); }
        var security = new FileSecurity();
        security.SetOwner(owner ?? new SecurityIdentifier("S-1-5-32-544"));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var principal in owner is null
            ? new[] { new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") } : new[] { owner })
        { security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl, AccessControlType.Allow)); }
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, security);
    }

    // Runtime and v2 cleanup require this exact private promise. Authenticated v1
    // cleanup separately retains the older no-unprivileged-mutation contract.
    internal static bool AcceptsPrivateCustody(RawSecurityDescriptor descriptor, SecurityIdentifier? fixtureOwner = null)
    {
        if (!descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            || descriptor.DiscretionaryAcl is not { } acl) { return false; }
        string[] principals = fixtureOwner is null ? ["S-1-5-18", "S-1-5-32-544"] : [fixtureOwner.Value];
        if (!(fixtureOwner is null ? descriptor.Owner?.Value is "S-1-5-18" or "S-1-5-32-544" : descriptor.Owner == fixtureOwner)
            || acl.Count != principals.Length) { return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (GenericAce entry in acl)
        {
            if (entry is not CommonAce ace || ace.IsCallback || ace.AceFlags != AceFlags.None
                || ace.AceQualifier != AceQualifier.AccessAllowed || ace.AccessMask != (int)FileSystemRights.FullControl
                || !principals.Contains(ace.SecurityIdentifier.Value, StringComparer.Ordinal) || !seen.Add(ace.SecurityIdentifier.Value)) { return false; }
        }
        return seen.Count == principals.Length;
    }

    // V1 authenticates no-unprivileged-write, not an original private-only promise.
    // Cleanup retains the exact original descriptor and exclusive lock.
    internal static bool AcceptsLegacyCustody(RawSecurityDescriptor descriptor, SecurityIdentifier? fixtureOwner = null)
    {
        bool Trusted(SecurityIdentifier? sid) => fixtureOwner is null
            ? sid?.Value is "S-1-5-18" or "S-1-5-32-544" : sid == fixtureOwner;
        if (!Trusted(descriptor.Owner) || descriptor.DiscretionaryAcl is not { Count: >= 1 and <= 64 } acl) { return false; }
        const int readOnly = unchecked((int)0xA01200A9); // generic/file read+execute, READ_CONTROL, SYNCHRONIZE
        foreach (GenericAce entry in acl)
        {
            if (entry is not CommonAce ace || ace.IsCallback
                || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied)) { return false; }
            if (ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AceFlags & AceFlags.InheritOnly) == 0
                && !Trusted(ace.SecurityIdentifier) && (ace.AccessMask & ~readOnly) != 0) { return false; }
        }
        return true;
    }

    // Product trust mode for an ordinary entry: a trusted owner and no write
    // access for any untrusted principal. Private-custody entries pass too.
    internal static void RequireProtectedEntry(RawSecurityDescriptor descriptor)
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
    }

    private void VerifyAccess(SafeFileHandle handle, bool directory, bool isRoot, string path, bool privateCustody, bool legacyCustody = false)
    {
        var descriptor = new RawSecurityDescriptor(
            new HandleSecurity(handle, directory).GetSecurityDescriptorBinaryForm(), 0);

        if (legacyCustody)
        {
            if (!AcceptsPrivateCustody(descriptor, owner) && !AcceptsLegacyCustody(descriptor, owner))
            { throw new UnauthorizedAccessException("Authenticated legacy cleanup custody permits untrusted mutation."); }
            return;
        }
        if (privateCustody)
        {
            if (!AcceptsPrivateCustody(descriptor, owner)) { throw new UnauthorizedAccessException("Native custody requires a protected private lock; legacy public locks are not adopted."); }
            return;
        }
        if (owner is null)
        {
            RequireProtectedEntry(descriptor);
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
        internal uint Flags;
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
