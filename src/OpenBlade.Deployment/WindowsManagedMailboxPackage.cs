using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace OpenBlade.Deployment;

// The original signed managed upgrade, retained before broker staging cleanup.
// Hashes reported by a running Service are never their own trust anchor.
[SupportedOSPlatform("windows")]
internal sealed class WindowsManagedMailboxPackage : IDisposable
{
    private readonly FileStream package;
    private readonly IDisposable leases;
    private readonly NativeLocalDevEnrollment enrollment;
    private readonly byte[] manifest, signature;
    private readonly ImmutableArray<WindowsNativeInventoryBurnExtractor.ManagedDeclaration> declarations;
    private readonly List<IDisposable> installed = [];
    internal OpenBladeVerifiedRelease Release { get; }
    internal string PackageDigest => Release.Manifest.InstallerSha256;
    internal string Installation => enrollment.InstallationId.ToString("N");
    internal string EnrollmentDigest => NativeCanonical.Digest(enrollment.Projection);
    internal byte[] VerificationProjection => (byte[])enrollment.Projection.Clone();
    internal sealed record BuildIdentity(string Executable, string Closure, string Module);

    private WindowsManagedMailboxPackage(FileStream package, IDisposable leases, NativeLocalDevEnrollment enrollment,
        byte[] manifest, byte[] signature, OpenBladeVerifiedRelease release,
        ImmutableArray<WindowsNativeInventoryBurnExtractor.ManagedDeclaration> declarations)
    { this.package = package; this.leases = leases; this.enrollment = enrollment; this.manifest = manifest; this.signature = signature; Release = release; this.declarations = declarations; }

    internal static async Task<WindowsManagedMailboxPackage> OpenLocalDevAsync(FileStream package, IDisposable leases,
        byte[] manifest, byte[] signature, NativeLocalDevEnrollment enrollment, CancellationToken token)
    {
        try
        {
            var release = OpenBladeSignedReleaseVerifier.Verify(manifest, signature, enrollment.Policy);
            if (release.Manifest.InstallationId != enrollment.InstallationId
                || release.Manifest.SchemaVersion != OpenBladeReleaseManifestCodec.CurrentSchemaVersion)
            { throw new InvalidDataException("Managed upgrade differs from its enrolled standard release."); }
            package.Position = 0;
            await OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(package, release, token).ConfigureAwait(false);
            package.Position = 0;
            var declarations = WindowsNativeInventoryBurnExtractor.ReadManagedDeclarations(package, release.Manifest.Version, token);
            return new(package, leases, enrollment, (byte[])manifest.Clone(), (byte[])signature.Clone(), release, declarations);
        }
        catch { package.Dispose(); leases.Dispose(); enrollment.Dispose(); throw; }
    }

    internal BuildIdentity AuthenticateInstalled(string directory) =>
        AuthenticateCore(WindowsNativeEpochFiles.OpenProtected(directory)).Identity;

    // Also proves that the running Service loaded exactly that closure. It compares
    // each loaded assembly's file origin, which only the framework-dependent Service
    // image has; the annotation keeps the single-file Setup from reaching it.
    [RequiresAssemblyFiles("Compares the running Service's loaded assemblies with their installed files.")]
    internal BuildIdentity AuthenticateLoadedService(string directory)
    {
        var files = WindowsNativeEpochFiles.OpenProtected(directory);
        var (identity, closure, module) = AuthenticateCore(files);
        if (module != System.Reflection.Assembly.GetEntryAssembly()!.ManifestModule.ModuleVersionId)
        { throw new IOException("Loaded managed Service differs from the signed original assembly."); }
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
        {
            string name = loaded.GetName().Name + ".dll";
            var candidates = closure.Where(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0) { continue; }
            // An assembly loaded from memory or a bundle has no file origin to match.
            if (candidates.Length != 1 || loaded.Location.Length == 0 || !Path.GetFullPath(loaded.Location).Equals(
                Path.GetFullPath(Path.Combine(files.Root, candidates[0])), StringComparison.OrdinalIgnoreCase))
            { throw new IOException("A loaded managed dependency has a different resolved origin."); }
            using var input = files.OpenFile(Path.Combine(files.Root, candidates[0]));
            using var metadata = new PEReader(input);
            var definition = metadata.GetMetadataReader();
            if (definition.GetGuid(definition.GetModuleDefinition().Mvid) != loaded.ManifestModule.ModuleVersionId)
            { throw new IOException("A loaded managed dependency differs from its authenticated closure."); }
        }
        return identity;
    }

#if OPENBLADE_NATIVE_PACKAGE_TESTS
    internal BuildIdentity AuthenticateFixture(WindowsNativeEpochFiles files) => AuthenticateCore(files).Identity;
#endif

    private (BuildIdentity Identity, IReadOnlyCollection<string> Closure, Guid Module) AuthenticateCore(WindowsNativeEpochFiles files)
    {
        string directory = files.Root;
        installed.Add(files);
        var expected = declarations.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        System.Runtime.Loader.AssemblyDependencyResolver? resolver = null;
        var pending = new Stack<string>();
        foreach (string name in new[] { "OpenBlade.Service.exe", "OpenBlade.Service.dll", "OpenBlade.Service.deps.json",
            "OpenBlade.Service.runtimeconfig.json", "openblade_ffi.dll" })
        { if (expected.ContainsKey(name)) { pending.Push(name); } else { throw new IOException("Signed Service input is missing."); } }
        while (pending.Count != 0)
        {
            string relative = pending.Pop();
            if (actual.ContainsKey(relative)) { continue; }
            if (actual.Count >= 2048 || !expected.TryGetValue(relative, out var declared))
            { throw new IOException("Service load input is outside the signed upgrade."); }
            string path = Path.Combine(directory, relative);
            installed.Add(files.RetainDirectory(Path.GetDirectoryName(path)!));
            var held = files.RetainFile(path);
            if (held.Length != declared.Length || Convert.ToHexStringLower(SHA512.HashData(held)) != declared.Sha512)
            { throw new IOException("Installed managed code differs from the original signed upgrade."); }
            held.Position = 0;
            actual.Add(relative, Convert.ToHexStringLower(SHA256.HashData(held)));
            if (!relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { continue; }
            held.Position = 0;
            using var dependency = new PEReader(held, PEStreamOptions.LeaveOpen);
            if (!dependency.HasMetadata) { continue; }
            var metadata = dependency.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                string name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                // Use the host's deps/RID resolution, including runtime-specific
                // implementations. Equal basenames in other RIDs are not loaded.
                string? resolved;
                try
                {
                    resolver ??= new(Path.Combine(directory, "OpenBlade.Service.dll"));
                    resolved = resolver.ResolveAssemblyToPath(new System.Reflection.AssemblyName(name));
                }
                catch (InvalidOperationException error)
                { throw new InvalidDataException("Signed Service dependency resolution is unavailable.", error); }
                if (resolved is not null)
                {
                    string selected = Path.GetRelativePath(directory, resolved);
                    if (Path.IsPathRooted(selected) || selected.StartsWith("..", StringComparison.Ordinal)
                        || !expected.ContainsKey(selected))
                    { throw new IOException("Resolved Service dependency is outside the signed upgrade."); }
                    pending.Push(selected);
                }
                else
                {
                    try { _ = File.GetAttributes(Path.Combine(directory, name + ".dll")); }
                    catch (FileNotFoundException) { continue; } // Shared framework input, outside product custody.
                    throw new IOException("Unsigned Service dependency shadows its shared framework input.");
                }
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("OpenBlade.ManagedMailboxBuild.v1\n"u8);
        foreach (var (name, digest) in actual) { hash.AppendData(Encoding.ASCII.GetBytes(name + "\n" + digest + "\n")); }
        using var assembly = files.OpenFile(Path.Combine(directory, "OpenBlade.Service.dll"));
        using var pe = new PEReader(assembly, PEStreamOptions.LeaveOpen);
        var reader = pe.GetMetadataReader();
        Guid module = reader.GetGuid(reader.GetModuleDefinition().Mvid);
        return (new(actual["OpenBlade.Service.exe"], Convert.ToHexStringLower(hash.GetHashAndReset()), module.ToString("N")),
            actual.Keys, module);
    }

    internal void ReleaseInstalledPins()
    {
        for (int i = installed.Count - 1; i >= 0; i--) { installed[i].Dispose(); }
        installed.Clear();
    }

    internal void Preserve(WindowsNativeEpochFiles files)
    {
        string directory = Path.Combine(files.Root, "build-" + PackageDigest);
        if (!WindowsNativeEpochFiles.IsPresentDirectory(directory))
        {
            files.CreateDirectoryOnce(directory);
            using var pinned = files.RetainDirectory(directory);
            package.Position = 0;
            files.WriteEvidence(Path.Combine(directory, "package.exe"), package);
            files.Publish(Path.Combine(directory, "release.json"), manifest);
            using var bytes = new MemoryStream(signature, false);
            files.WriteEvidence(Path.Combine(directory, "release.sig"), bytes);
            files.Publish(Path.Combine(directory, "verified.bin"), CacheBinding());
        }
        using var marker = files.OpenFile(Path.Combine(directory, "verified.bin"));
        WindowsNativeEpochFiles.RequireExact(marker, CacheBinding());
        files.Publish(Path.Combine(files.Root, "build-current.bin"), NativeCanonical.Lines("OpenBlade.ManagedMailboxBuildSelection.v1", PackageDigest), replace: true);
    }
    private byte[] CacheBinding() => NativeCanonical.Lines("OpenBlade.ManagedMailboxPackage.v1", Installation, PackageDigest,
        NativeCanonical.Digest(manifest), NativeCanonical.Digest(signature), EnrollmentDigest);

    internal static WindowsManagedMailboxPackage OpenCachedHistorical(WindowsNativeEpochFiles files, string digest, byte[] projection) =>
        OpenCached(files, digest, () => NativeLocalDevEnrollment.FromProtectedProjection(projection));

    internal static WindowsManagedMailboxPackage OpenCached(WindowsNativeEpochFiles files, string digest, Func<NativeLocalDevEnrollment>? acquire = null)
    {
        if (!NativeCanonical.IsHex(digest, 64)) { throw new InvalidDataException("Managed cached package identity differs."); }
        var enrollment = acquire is null ? AcquireDefaultEnrollment(files) : acquire();
        var leases = new HeldFiles();
        FileStream? package = null;
        try
        {
            string directory = Path.Combine(files.Root, "build-" + digest);
            leases.Add(files.RetainDirectory(directory));
            var manifest = files.OpenFile(Path.Combine(directory, "release.json")); leases.Add(manifest);
            var signature = files.OpenFile(Path.Combine(directory, "release.sig")); leases.Add(signature);
            package = files.OpenFile(Path.Combine(directory, "package.exe"));
            var value = OpenLocalDevAsync(package, leases, WindowsNativeEpochFiles.ReadBounded(manifest),
                WindowsNativeEpochFiles.ReadBounded(signature), enrollment, CancellationToken.None).GetAwaiter().GetResult();
            if (value.PackageDigest != digest) { value.Dispose(); throw new IOException("Managed cache package was substituted."); }
            using var marker = files.OpenFile(Path.Combine(directory, "verified.bin"));
            WindowsNativeEpochFiles.RequireExact(marker, value.CacheBinding());
            return value;
        }
        catch { package?.Dispose(); leases.Dispose(); enrollment.Dispose(); throw; }
    }
    // A retained private owner positively identifies fixtures. Check it before
    // selecting the product resolver; explicit private and historical readers remain valid.
    internal static NativeLocalDevEnrollment AcquireDefaultEnrollment(WindowsNativeEpochFiles files,
        Func<NativeLocalDevEnrollment>? productResolver = null)
    {
        if (files.IsPrivateFixture) { throw new InvalidOperationException("Private mailbox fixtures require explicit enrollment."); }
        return (productResolver ?? NativeLocalDevEnrollment.Acquire)();
    }

    private sealed class HeldFiles : IDisposable
    {
        private readonly List<IDisposable> held = [];
        internal void Add(IDisposable value) => held.Add(value);
        public void Dispose() { for (int i = held.Count - 1; i >= 0; i--) { held[i].Dispose(); } held.Clear(); }
    }
    public void Dispose()
    {
        ReleaseInstalledPins(); package.Dispose(); leases.Dispose(); enrollment.Dispose();
    }
}
