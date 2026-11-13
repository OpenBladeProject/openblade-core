using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace OpenBlade.Deployment;

// Package facts only. Neither the compiled marker nor this inventory proves loaded origin.
internal sealed record NativeArtifact(string Id, string Role, string Target, string Source,
    string Features, string Compiler, string Profile, long Size, string Sha256, string Imports,
    int Schema = 1, string Capabilities = "none")
{
    internal bool HasStartupCustody => Features.Split(',').Contains("startup-custody-v1", StringComparer.Ordinal);
    internal bool PrivateCustody => Schema == 2 && Capabilities.Split(',').Contains("private-service-custody-v1", StringComparer.Ordinal);
    internal bool MetadataOnly => Schema == 2 && Capabilities.Split(',').Contains("service-metadata-only-v1", StringComparer.Ordinal);
    internal bool ReadOnlyMailbox => !ThermalMutation && Schema == 2 && Capabilities.Split(',').Contains("service-mailbox-observation-v1", StringComparer.Ordinal);
    internal bool FanMutation => Schema == 2 && Capabilities.Split(',').Contains("service-fan-mutation-v2", StringComparer.Ordinal);
    internal bool ThermalMutation => Schema == 2 && Capabilities.Split(',').Contains("service-thermal-mutation-v1", StringComparer.Ordinal);
    internal bool RequiresMailboxHandoff => ReadOnlyMailbox || ThermalMutation;
    internal string StartupPolicy => FanMutation ? "service-fan-mutation-v2" : ThermalMutation ? "service-thermal-mutation-v1" : MetadataOnly ? "service-metadata-only-v1" : ReadOnlyMailbox ? "service-mailbox-observation-v1" : Schema == 1 ? "metadata-only-no-firmware" : "service-hardware-compile-check";

    internal byte[] Encode()
    {
        string[] fields = [Id, Role, Target, Source, Features, Compiler, Profile,
            Size.ToString(System.Globalization.CultureInfo.InvariantCulture), Sha256, Imports];
        return NativeCanonical.Lines($"OpenBlade.NativeArtifactRecord.v{Schema}", Schema == 1 ? fields : [.. fields, Capabilities]);
    }

    internal static string[] RecordFields(byte[] bytes)
    {
        bool v2 = bytes.AsSpan().StartsWith("OpenBlade.NativeArtifactRecord.v2\n"u8);
        return NativeCanonical.ParseLines(bytes, v2 ? "OpenBlade.NativeArtifactRecord.v2" : "OpenBlade.NativeArtifactRecord.v1", v2 ? 12 : 11);
    }

    // Capabilities describe role composition. Features retain the whole Cargo
    // invocation, including Service features shared by a combined Agent build.
    private static void RequireCapabilities(int schema, string role, string[] features, string capabilities)
    {
        bool metadata = features.Contains("service-metadata-only-v1", StringComparer.Ordinal);
        bool hardware = features.Contains("service-hardware-compile-check", StringComparer.Ordinal);
        bool firmware = features.Contains("firmware-performance-compile-check", StringComparer.Ordinal);
        bool observation = features.Contains("service-mailbox-observation-v1", StringComparer.Ordinal);
        bool mutation = features.Contains("service-thermal-mutation-v1", StringComparer.Ordinal);
        bool fan = features.Contains("service-fan-mutation-v2", StringComparer.Ordinal);
        if (fan && !mutation) { throw new InvalidDataException("Fan mutation requires the thermal custody contract."); }
        if (mutation && (!observation || !features.Contains("controller-admission-compile-check", StringComparer.Ordinal))) { throw new InvalidDataException("Thermal mutation requires mailbox custody and fresh controller admission."); }
        if (observation && (!hardware || metadata)) { throw new InvalidDataException("Mailbox observation requires its exclusive acquisition composition."); }
        if (schema == 1)
        {
            if (metadata || hardware) { throw new InvalidDataException("Legacy artifacts cannot assert v2 composition."); }
            return;
        }
        string expected = "none";
        if (metadata && (hardware || firmware || features.Contains("hid-identity-compile-check", StringComparer.Ordinal) || features.Any(f => f.Contains("fixture", StringComparison.Ordinal) || f.StartsWith("scratch-", StringComparison.Ordinal))))
        { throw new InvalidDataException("Metadata composition includes incompatible acquisition or fixture features."); }
        if (hardware != firmware || (metadata || hardware) && !features.Contains("installed-service-candidate", StringComparer.Ordinal))
        { throw new InvalidDataException("Native feature composition contradicts its role provenance."); }
        if (role == "service")
        {
            if (metadata == hardware || !features.Contains("startup-custody-v1", StringComparer.Ordinal) || hardware != firmware)
            { throw new InvalidDataException("Native Service requires an explicit coherent composition."); }
            expected = metadata ? "private-service-custody-v1,service-metadata-only-v1" : fan ? "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2" : mutation ? "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1" : observation ? "private-service-custody-v1,service-mailbox-observation-v1" : "private-service-custody-v1";
        }
        if (capabilities != expected) { throw new InvalidDataException("Native capabilities differ from the authenticated role composition."); }
    }

    internal static NativeArtifact Inspect(ImmutableArray<byte> image, string role, string source)
    {
        if (role is not ("service" or "agent")) { throw new InvalidDataException("Unknown native artifact role."); }
        using PEReader reader = new(image);
        var headers = reader.PEHeaders;
        if (image.Length is < 512 or > WindowsNativeRoleInventory.MaximumRoleImageBytes
            || headers.CoffHeader.Machine != Machine.Amd64
            || (headers.CoffHeader.Characteristics & (Characteristics.ExecutableImage | Characteristics.Dll)) != Characteristics.ExecutableImage
            || headers.PEHeader is not { Magic: PEMagic.PE32Plus } pe
            || pe.CorHeaderTableDirectory.Size != 0 || pe.CorHeaderTableDirectory.RelativeVirtualAddress != 0
            || pe.DelayImportTableDirectory.Size != 0 || pe.DelayImportTableDirectory.RelativeVirtualAddress != 0)
        {
            throw new InvalidDataException("Native artifact requires a bounded x64 executable with no CLR or delay-import closure.");
        }
        var sections = headers.SectionHeaders.Where(s => s.Name == ".obid").ToArray();
        if (sections.Length != 1 || sections[0].VirtualSize is < 1 or > 1024
            || sections[0].SizeOfRawData < sections[0].VirtualSize)
        {
            throw new InvalidDataException("Native artifact has no unique compiled build identity.");
        }
        var section = sections[0];
        byte[] metadata = image.AsSpan().Slice(section.PointerToRawData, section.VirtualSize).ToArray();
        int schema = metadata.AsSpan().StartsWith("OpenBlade.NativeArtifact.v2\n"u8) ? 2 : 1;
        string[] fields = NativeCanonical.ParseLines(metadata, $"OpenBlade.NativeArtifact.v{schema}", schema == 2 ? 9 : 8);
        if (fields[0].Length != 65 + role.Length || fields[1] != role || fields[2] != "x86_64-pc-windows-msvc" || fields[3] != source
            || fields[0] != fields[0].ToLowerInvariant() || !fields[0].EndsWith("-" + role, StringComparison.Ordinal)
            || !NativeCanonical.IsHex(fields[0][..^(role.Length + 1)], 64)
            || fields[0][..64].All(c => c == '0')
            || fields[5].Length is < 1 or > 160 || !fields[5].StartsWith("rustc ", StringComparison.Ordinal)
            || fields[6] is not ("debug" or "release"))
        {
            throw new InvalidDataException("Native artifact build identity is unbound or differs from its authenticated role/build.");
        }
        string[] features = fields[4].Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (string.Join(',', features.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal)) != fields[4]
            || features.Any(f => f.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            || !features.Contains(role == "service" ? "installed-service-candidate" : "native-agent-entry-compile-check", StringComparer.Ordinal))
        {
            throw new InvalidDataException("Native artifact features do not admit the selected role.");
        }
        string capabilities = schema == 2 ? fields[7] : "none";
        RequireCapabilities(schema, role, features, capabilities);
        // This bounded package recipe has exactly two app-local executables. Imports
        // must be Windows system or the existing shared VC runtime contracts; dynamic optional integrations require their
        // own later loader/origin policy. No Microsoft-only CIG policy is installed.
        var imports = new SortedSet<string>(StringComparer.Ordinal);
        var directory = pe.ImportTableDirectory;
        if (directory.Size is < 20 or > 65536) { throw new InvalidDataException("Native import table is missing or unbounded."); }
        var table = reader.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size).AsSpan();
        bool terminated = false;
        for (int offset = 0; offset + 20 <= table.Length; offset += 20)
        {
            var entry = table.Slice(offset, 20);
            if (entry.IndexOfAnyExcept((byte)0) < 0) { terminated = true; break; }
            int nameRva = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            var nameBytes = reader.GetSectionData(nameRva).GetContent().AsSpan();
            int end = nameBytes.IndexOf((byte)0);
            if (end is < 1 or > 120 || nameBytes[..end].ContainsAnyExceptInRange((byte)0x21, (byte)0x7e))
            { throw new InvalidDataException("Invalid native import name."); }
            string name = Encoding.ASCII.GetString(nameBytes[..end]).ToLowerInvariant();
            if (!IsSystemImport(name)) { throw new InvalidDataException("Unreviewed external import contract."); }
            imports.Add(name);
        }
        if (!terminated) { throw new InvalidDataException("Unterminated native import table."); }
        return new(fields[0], role, fields[2], fields[3], fields[4], fields[5], fields[6], image.Length,
            Convert.ToHexStringLower(SHA256.HashData(image.AsSpan())), string.Join(',', imports), schema, capabilities);
    }

    private static bool IsSystemImport(string name) => name is "kernel32.dll" or "ntdll.dll" or "advapi32.dll"
        or "bcrypt.dll" or "crypt32.dll" or "user32.dll" or "userenv.dll" or "shell32.dll" or "ole32.dll"
        or "oleaut32.dll" or "ws2_32.dll" or "iphlpapi.dll" or "setupapi.dll" or "cfgmgr32.dll"
        or "powrprof.dll" or "wtsapi32.dll" or "secur32.dll" or "version.dll" or "wintrust.dll"
        or "combase.dll" or "bcryptprimitives.dll"
        or "psapi.dll" or "gdi32.dll" or "dwmapi.dll" or "comctl32.dll" or "comdlg32.dll"
        or "shlwapi.dll" or "hid.dll" or "winmm.dll" or "dbghelp.dll" or "msvcrt.dll"
        || (name.StartsWith("api-ms-win-", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal)
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'));
}

internal static class NativeCanonical
{
    internal static bool IsHex(string value, int length) => value.Length == length
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static byte[] Lines(string header, params string[] fields)
    {
        int maximum = header is "OpenBlade.NativeStartupOwner.v2" or "OpenBlade.NativeServiceLaunch.v1" or "OpenBlade.NativeReadiness.v2" or "OpenBlade.ManagedNativeResolution.v2" ? 10924 : 2048;
        if (fields.Any(f => f.Length > maximum || f.Any(c => c < 0x20 || c > 0x7e))
            || header.Length + fields.Sum(f => f.Length + 1) + 1 > 16384)
        { throw new InvalidDataException("Canonical native records require bounded ASCII fields."); }
        return Encoding.ASCII.GetBytes(header + "\n" + string.Join('\n', fields) + "\n");
    }

    internal static string[] ParseLines(byte[] bytes, string header, int lines)
    {
        if (bytes.Length is < 1 or > 16384 || bytes.Any(c => c != 10 && (c < 0x20 || c > 0x7e)))
        { throw new InvalidDataException("Invalid canonical native record."); }
        string[] values = Encoding.ASCII.GetString(bytes).Split('\n');
        if (values.Length != lines + 1 || values[0] != header || values[^1] != "")
        { throw new InvalidDataException("Native record schema or field count differs."); }
        return values[1..^1];
    }

    internal static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

// Only the protected enrollment adapter may choose a LocalDev key. This type is
// internal; no public API accepts a caller public key as installed trust.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class NativeLocalDevEnrollment : IDisposable
{
    private readonly WindowsNativeEpochFiles? storage;
    private readonly FileStream? held;
    internal Guid InstallationId { get; }
    internal OpenBladeReleaseTrustPolicy Policy { get; }
    internal byte[] Projection { get; private init; } = [];
    private NativeLocalDevEnrollment(Guid installation, OpenBladeReleaseTrustPolicy policy,
        WindowsNativeEpochFiles? storage, FileStream? held)
    { InstallationId = installation; Policy = policy; this.storage = storage; this.held = held; }

    internal static NativeLocalDevEnrollment Acquire()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade", "LocalDevDeploymentBroker");
        var storage = WindowsNativeEpochFiles.OpenProtected(root);
        FileStream? held = null;
        try
        {
            held = storage.OpenFile(Path.Combine(root, "enrollment.json"));
            return FromCanonical(WindowsNativeEpochFiles.ReadBounded(held, 2048), storage, held);
        }
        catch { held?.Dispose(); storage.Dispose(); throw; }
    }

    internal static NativeLocalDevEnrollment FromProtectedProjection(byte[] bytes)
    {
        var projection = ParseProjection(bytes);
        return new(projection.Installation, new(true, [new(projection.Key, projection.Spki)]), null, null) { Projection = (byte[])bytes.Clone() };
    }

    // Pure retained-envelope parsing; no current enrollment or key trust is acquired.
    internal static (Guid Installation, string Key, string Spki) ParseProjection(byte[] bytes)
    {
        string[] f = NativeCanonical.ParseLines(bytes, "OpenBlade.NativeVerification.v1", 7);
        if (f[0] != "LocalDev" || !Guid.TryParseExact(f[1], "D", out var installation) || installation == Guid.Empty
            || f[2].Length == 0 || !NativeCanonical.IsHex(f[3], 64) || !NativeCanonical.IsHex(f[5], 64)
            || NativeCanonical.Digest(Convert.FromBase64String(f[4])) != f[3])
        { throw new InvalidDataException("Native public verification projection differs."); }
        return (installation, f[3], f[4]);
    }

    internal static NativeLocalDevEnrollment FromHeld(FileStream original)
    {
        original.Position = 0;
        return FromCanonical(WindowsNativeEpochFiles.ReadBounded(original, 2048), null, null);
    }
    private static NativeLocalDevEnrollment FromCanonical(byte[] bytes, WindowsNativeEpochFiles? storage, FileStream? held)
    {

        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        string[] required = ["schemaVersion", "installationId", "enrolledAccountSid", "deploymentPolicyId", "brokerVersion",
                "developmentSigningKeyId", "developmentSigningSubjectPublicKeyInfoBase64", "enrolledAt"];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name) || !required.Contains(property.Name, StringComparer.Ordinal)
                && property.Name != "cpuVoltageValidationEnabled")
            { throw new InvalidDataException("Native trust enrollment schema differs."); }
        }
        var enrollment = document.RootElement;
        if (required.Any(name => !names.Contains(name)) || enrollment.GetProperty("schemaVersion").GetInt32() != 1
            || !Guid.TryParseExact(enrollment.GetProperty("installationId").GetString(), "D", out var installation)
            || installation == Guid.Empty)
        { throw new InvalidDataException("Native LocalDev enrollment is unavailable."); }
        string key = enrollment.GetProperty("developmentSigningKeyId").GetString() ?? throw new InvalidDataException();
        string spki = enrollment.GetProperty("developmentSigningSubjectPublicKeyInfoBase64").GetString() ?? throw new InvalidDataException();
        return new(installation, new(true, [new(key, spki)]), storage, held)
        {
            Projection = NativeCanonical.Lines("OpenBlade.NativeVerification.v1", "LocalDev", installation.ToString("D"),
                enrollment.GetProperty("deploymentPolicyId").GetString() ?? throw new InvalidDataException(), key, spki, NativeCanonical.Digest(bytes))
        };
    }

#if OPENBLADE_NATIVE_PACKAGE_TESTS
    // Algorithm-only/private-filesystem test seam. It does not exist in product builds.
    internal static NativeLocalDevEnrollment ForAlgorithmTests(Guid installation, OpenBladeReleaseTrustPolicy policy)
        => new(installation, policy, null, null)
        {
            Projection = NativeCanonical.Lines("OpenBlade.NativeVerification.v1", "LocalDev", installation.ToString("D"),
                "offline-policy", policy.TrustedKeys.Single().KeyId, policy.TrustedKeys.Single().SubjectPublicKeyInfoBase64, new string('a', 64))
        };
#endif
    public void Dispose() { held?.Dispose(); storage?.Dispose(); }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsNativePackage : IDisposable
{
    private readonly FileStream snapshot;
    private readonly IDisposable enrollmentLease;
    private readonly NativeLocalDevEnrollment enrollment;
    private readonly byte[] manifest;
    internal byte[] Manifest => (byte[])manifest.Clone();
    private readonly byte[] signature;
    internal byte[] Signature => (byte[])signature.Clone();
    private readonly byte[] record;
    internal byte[] Record => (byte[])record.Clone();
    private readonly byte[] inventory;
    internal byte[] Inventory => (byte[])inventory.Clone();
    internal byte[] VerificationProjection => (byte[])enrollment.Projection.Clone();
    internal OpenBladeVerifiedRelease Release { get; }
    internal ImmutableArray<byte> ServiceImage { get; }
    internal ImmutableArray<byte> AgentImage { get; }
    internal NativeArtifact Service { get; }
    internal NativeArtifact Agent { get; }

    private WindowsNativePackage(FileStream snapshot, IDisposable enrollmentLease, byte[] manifest, byte[] signature,
        OpenBladeVerifiedRelease release, WindowsNativeInventoryBurnExtractor.RoleExtraction extracted, NativeLocalDevEnrollment enrollment)
    {
        this.snapshot = snapshot;
        this.enrollmentLease = enrollmentLease;
        this.enrollment = enrollment;
        this.manifest = (byte[])manifest.Clone();
        this.signature = (byte[])signature.Clone();
        Release = release;
        ServiceImage = extracted.Service.Image;
        AgentImage = extracted.SessionAgent.Image;
        var build = new WindowsNativeBuildIdentity(release.Manifest.SourceRevision, Version.Parse(release.Manifest.Version));
        var roleInventory = WindowsNativeRoleInventory.DecodeForBuild(build, extracted.Resource.AsSpan());
        Service = NativeArtifact.Inspect(ServiceImage, "service", build.SourceRevision);
        Agent = NativeArtifact.Inspect(AgentImage, "agent", build.SourceRevision);
        if (Service.Id == Agent.Id || Service.Sha256 != roleInventory.GetExpectedSha256(WindowsNativeRole.Service)
            || Agent.Sha256 != roleInventory.GetExpectedSha256(WindowsNativeRole.SessionAgent))
        { throw new InvalidDataException("Native role/identity inventory substitution."); }
        inventory = extracted.Resource.ToArray();
        record = NativeCanonical.Lines("OpenBlade.NativePackage.v2", "LocalDev", release.Manifest.InstallationId.ToString("N"),
            release.Manifest.InstallerSha256, release.Manifest.InstallerSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            NativeCanonical.Digest(manifest), NativeCanonical.Digest(signature), release.SigningKeyId,
            NativeCanonical.Digest(extracted.Resource.AsSpan()), Convert.ToBase64String(Service.Encode()), Convert.ToBase64String(Agent.Encode()),
            "two-executables-static-crt-system-imports-v2", NativeCanonical.Digest(enrollment.Projection));
    }

    internal static async Task<WindowsNativePackage> OpenLocalDevAsync(FileStream heldPackage, IDisposable enrollmentLease,
        byte[] manifest, byte[] signature, NativeLocalDevEnrollment enrollment, CancellationToken cancellationToken)
    {
        try
        {
            var release = OpenBladeSignedReleaseVerifier.Verify(manifest, signature, enrollment.Policy);
            if (release.Manifest.InstallationId != enrollment.InstallationId
                || release.Manifest.SchemaVersion != OpenBladeReleaseManifestCodec.CurrentSchemaVersion)
            { throw new InvalidDataException("Native package is not bound to the enrolled standard LocalDev installation."); }
            heldPackage.Position = 0;
            await OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(heldPackage, release, cancellationToken).ConfigureAwait(false);
            heldPackage.Position = 0;
            var extracted = WindowsNativeInventoryBurnExtractor.ExtractRoles(heldPackage,
                WindowsNativeBurnProfile.AuthoredOpenBlade, release.Manifest.Version, cancellationToken);
            return new(heldPackage, enrollmentLease, (byte[])manifest.Clone(), (byte[])signature.Clone(), release, extracted, enrollment);
        }
        catch { heldPackage.Dispose(); enrollmentLease.Dispose(); enrollment.Dispose(); throw; }
    }

    internal void CopyPackageTo(Stream destination)
    {
        snapshot.Position = 0;
        snapshot.CopyTo(destination);
    }

    public void Dispose() { snapshot.Dispose(); enrollmentLease.Dispose(); enrollment.Dispose(); }
}
