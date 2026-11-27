using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Deployment.Tests;

// This exact-name class is an offline allowlist: algorithmic crypto, inert PE/CAB
// bytes, and private current-token filesystem only. No product/child/IPC/SCM calls.
[SupportedOSPlatform("windows")]
public sealed class WindowsNativeEpochTests
{
    private const string Features = "installed-service-candidate,native-agent-entry-compile-check,startup-custody-v1";

    private static string AgentCapabilities(bool battery, bool display, bool brightness, bool hdr, bool refresh = false, bool color = false, bool profile = false)
    {
        string[] markers = [.. new (bool Selected, string Marker)[]
        {
            (battery, "agent-battery-report-v1"),
            (display, "session-display-query-partial-v1"),
            (brightness, "session-display-brightness-write-partial-v1"),
            (hdr, "session-display-hdr-write-partial-v1"),
            (refresh, "session-display-refresh-write-partial-v1"),
            (color, "session-display-color-write-partial-v1"),
            (profile, "session-display-profile-apply-partial-v1"),
        }.Where(entry => entry.Selected).Select(entry => entry.Marker)];
        return markers.Length == 0 ? "none" : string.Join(',', markers);
    }

    internal static byte[] Image(string role, char nonce = 'b', byte marker = 1, int schema = 1, string? capabilities = null, bool mailbox = false, string? sourceRevision = null, bool mutation = false, bool fan = false, bool battery = false, bool temporary = false, bool agentBattery = false, bool agentDisplay = false, bool agentWrite = false, bool agentHdr = false, bool agentRefresh = false, bool agentColor = false, string[]? imports = null, bool agentProfile = false, bool agentGpu = false, string? features = null)
    {
        byte[] bytes = new byte[imports is null ? 0xA00 : 0xC00];
        void U16(int n, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(n), v);
        void U32(int n, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(n), v);
        "MZ"u8.CopyTo(bytes); U32(60, 0x80); "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        U16(0x84, 0x8664); U16(0x86, 2); U16(0x94, 240); U16(0x96, 2); U16(0x98, 0x20b);
        U32(0x98 + 32, 0x1000); U32(0x98 + 36, 0x200); U32(0x98 + 56, 0x3000); U32(0x98 + 60, 0x400); U32(0x98 + 108, 16);
        U32(0x98 + 120, 0x2000); U32(0x98 + 124, 40);
        string[] fields = [new string(nonce, 64) + "-" + role, role, "x86_64-pc-windows-msvc", sourceRevision ?? new string('a', 40),
            schema == 1 ? Features : battery ? "controller-admission-compile-check,firmware-performance-compile-check,installed-service-candidate,native-agent-entry-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-thermal-mutation-v1,startup-custody-v1" : fan ? "controller-admission-compile-check,firmware-performance-compile-check,installed-service-candidate,native-agent-entry-compile-check,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-thermal-mutation-v1,startup-custody-v1" : mutation ? "controller-admission-compile-check,firmware-performance-compile-check,installed-service-candidate,native-agent-entry-compile-check,service-hardware-compile-check,service-mailbox-observation-v1,service-thermal-mutation-v1,startup-custody-v1" : mailbox ? "firmware-performance-compile-check,installed-service-candidate,native-agent-entry-compile-check,service-hardware-compile-check,service-mailbox-observation-v1,startup-custody-v1"
                : "installed-service-candidate,native-agent-entry-compile-check,service-metadata-only-v1,startup-custody-v1",
            "rustc 1.99.1 (inert test data)", "debug"];
        if (temporary) { fields[4] = "controller-admission-compile-check,firmware-performance-compile-check,installed-service-candidate,native-agent-entry-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1"; }
        if (features is not null) { fields[4] = features; }
        if (agentGpu) { fields[4] = "agent-gpu-v1," + fields[4]; }
        if (agentRefresh) { fields[4] = "agent-display-refresh-write-v1," + fields[4]; }
        if (agentDisplay) { fields[4] = "agent-display-query-v1," + fields[4]; }
        if (agentProfile) { fields[4] = "agent-display-profile-apply-v1," + fields[4]; }
        if (agentHdr) { fields[4] = "agent-display-hdr-write-v1," + fields[4]; }
        if (agentColor) { fields[4] = "agent-display-color-write-v1," + fields[4]; }
        if (agentWrite) { fields[4] = "agent-display-brightness-write-v1," + fields[4]; }
        if (agentBattery) { fields[4] = "agent-battery-report-v1," + fields[4]; }
        if (role == "agent" && (agentDisplay || agentWrite || agentHdr || agentRefresh || agentColor || agentProfile) && capabilities is null)
        { capabilities = AgentCapabilities(agentBattery, display: true, agentWrite, agentHdr, agentRefresh, agentColor, agentProfile); }
        byte[] metadata = NativeCanonical.Lines($"OpenBlade.NativeArtifact.v{schema}", schema == 1 ? fields
            : [.. fields, capabilities ?? (role == "service" ? temporary ? "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1" : battery ? "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1" : fan ? "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2" : mutation ? "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1" : mailbox ? "private-service-custody-v1,service-mailbox-observation-v1" : "private-service-custody-v1,service-metadata-only-v1" : agentBattery ? "agent-battery-report-v1" : "none")]);
        int section = 0x98 + 240;
        ".obid"u8.CopyTo(bytes.AsSpan(section)); U32(section + 8, (uint)metadata.Length); U32(section + 12, 0x1000); U32(section + 16, 0x400); U32(section + 20, 0x400); U32(section + 36, 0x40000040);
        ".idata"u8.CopyTo(bytes.AsSpan(section + 40)); U32(section + 48, imports is null ? 0x200u : 0x400u); U32(section + 52, 0x2000); U32(section + 56, imports is null ? 0x200u : 0x400u); U32(section + 60, 0x800); U32(section + 76, 0x40000040);
        metadata.CopyTo(bytes, 0x400); bytes[^1] = marker;
        if (imports is null) { U32(0x800 + 12, 0x2040); "KERNEL32.dll\0"u8.CopyTo(bytes.AsSpan(0x840)); }
        else
        {
            // One descriptor per name, the terminator, then the names.
            int name = 0x800 + 20 * (imports.Length + 1);
            for (int i = 0; i < imports.Length; i++)
            {
                U32(0x800 + 20 * i + 12, (uint)(0x2000 + name - 0x800));
                name += Encoding.ASCII.GetBytes(imports[i] + "\0", bytes.AsSpan(name));
            }
            U32(0x98 + 124, (uint)(20 * (imports.Length + 1)));
        }
        return bytes;
    }

    internal static Task<WindowsNativePackage> Package(WindowsNativeEpochFiles files, char nonce = 'b', byte marker = 1, int schema = 1, bool mailbox = false, string? sourceRevision = null, bool mutation = false) =>
        Package(files, Image("service", nonce, marker, schema, mailbox: mailbox, sourceRevision: sourceRevision, mutation: mutation),
            Image("agent", nonce, 2, schema, mailbox: mailbox, sourceRevision: sourceRevision, mutation: mutation), sourceRevision);

    internal static async Task<WindowsNativePackage> Package(WindowsNativeEpochFiles files, byte[] service, byte[] agent, string? sourceRevision = null)
    {
        byte[] bytes = WindowsNativeInventoryBurnExtractorTests.NativeEpochBundle(service, agent, sourceRevision);
        using var signed = OpenBladeSignedReleaseVerifierTests.TestRelease.CreateWithInstallerBytes(bytes, sourceRevision ?? new string('a', 40));
        if (!File.Exists(Path.Combine(files.Root, "root.bin")))
        {
            files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
            Directory.CreateDirectory(Path.Combine(files.Root, "owner"));
            if (!File.Exists(Path.Combine(files.Root, "owner", "writer.lock"))) { File.WriteAllBytes(Path.Combine(files.Root, "owner", "writer.lock"), []); }
        }
        var source = WindowsNativeEpochFiles.CreatePrivateFixture();
        string path = Path.Combine(source.Root, "package.exe");
        File.WriteAllBytes(path, bytes);
        var held = source.OpenFile(path);
        var release = OpenBladeSignedReleaseVerifier.Verify(signed.ManifestBytes, signed.SignatureBytes, signed.Policy);
        return await WindowsNativePackage.OpenLocalDevAsync(held, source, signed.ManifestBytes,
            signed.SignatureBytes, NativeLocalDevEnrollment.ForAlgorithmTests(release.Manifest.InstallationId, signed.Policy), CancellationToken.None);
    }

    [Fact]
    public void CompiledIdentityBindsRoleTargetFeaturesAndFullDigest()
    {
        byte[] bytes = Image("service");
        var artifact = NativeArtifact.Inspect([.. bytes], "service", new string('a', 40));
        Assert.EndsWith("-service", artifact.Id);
        Assert.Equal("kernel32.dll", artifact.Imports);
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. bytes], "agent", new string('a', 40)));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. bytes], "service", new string('c', 40)));
        bytes[^1] ^= 1;
        Assert.NotEqual(artifact.Sha256, NativeArtifact.Inspect([.. bytes], "service", new string('a', 40)).Sha256);
    }

    // The import descriptors of a real bound Agent (static CRT, every Agent
    // feature), read from the image without executing it.
    // native_package/tests.rs pins the same set for the Rust inspector.
    internal static readonly string[] RealAgentImports = ["kernel32.dll", "wtsapi32.dll", "shell32.dll",
        "combase.dll", "webservices.dll", "bcrypt.dll", "advapi32.dll", "ntdll.dll", "ole32.dll",
        "api-ms-win-core-synch-l1-2-0.dll", "user32.dll", "oleaut32.dll", "mscms.dll", "bcryptprimitives.dll",
        "KERNEL32.dll"];

    [Fact]
    public void RealAgentImportSetIsAdmittedAndAnUnreviewedDllIsNot()
    {
        string source = new('a', 40);
        byte[] Agent(string[] imports) => Image("agent", schema: 2, agentBattery: true, agentDisplay: true,
            agentWrite: true, agentHdr: true, agentRefresh: true, agentColor: true, imports: imports);
        var artifact = NativeArtifact.Inspect([.. Agent(RealAgentImports)], "agent", source);
        Assert.Equal("advapi32.dll,api-ms-win-core-synch-l1-2-0.dll,bcrypt.dll,bcryptprimitives.dll,combase.dll,"
            + "kernel32.dll,mscms.dll,ntdll.dll,ole32.dll,oleaut32.dll,shell32.dll,user32.dll,webservices.dll,wtsapi32.dll",
            artifact.Imports);
        foreach (string unreviewed in new[] { "winhttp.dll", "wininet.dll", "mscms.dll.evil" })
        {
            var refused = Assert.Throws<InvalidDataException>(
                () => NativeArtifact.Inspect([.. Agent([.. RealAgentImports, unreviewed])], "agent", source));
            Assert.Equal("Unreviewed external import contract.", refused.Message);
        }
    }

    // native_package::tests checks the Rust allowlist against the same rows.
    public static TheoryData<string, bool> ApiSetImports()
    {
        using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-api-set-imports-v1.json")));
        var rows = new TheoryData<string, bool>();
        foreach (var row in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            rows.Add(row.GetProperty("name").GetString()!, row.GetProperty("admitted").GetBoolean());
        }
        return rows;
    }

    [Theory]
    [MemberData(nameof(ApiSetImports))]
    public void ApiSetImportsFollowTheSharedTable(string name, bool admitted)
    {
        byte[] image = Image("agent", imports: [name]);
        if (admitted)
        {
            Assert.Equal(name, NativeArtifact.Inspect([.. image], "agent", new string('a', 40)).Imports);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. image], "agent", new string('a', 40)));
        }
    }

    [Fact]
    public void BatteryReportAgentCapabilityRequiresExplicitSelectionAndExactRole()
    {
        string source = new('a', 40);
        var ordinary = NativeArtifact.Inspect([.. Image("agent", schema: 2)], "agent", source);
        Assert.Equal("none", ordinary.Capabilities);
        var selected = NativeArtifact.Inspect([.. Image("agent", schema: 2, agentBattery: true)], "agent", source);
        Assert.Equal("agent-battery-report-v1", selected.Capabilities);
        Assert.False(selected.PrivateCustody);
        Assert.False(selected.MetadataOnly);
        Assert.False(selected.RequiresMailboxHandoff);
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, capabilities: "agent-battery-report-v1")], "agent", source));
        foreach (string forged in new[] { "none", "agent-battery-report-v1,private-service-custody-v1", "private-service-custody-v1" })
        {
            Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, agentBattery: true, capabilities: forged)], "agent", source));
        }
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 1, agentBattery: true)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, agentBattery: true, capabilities: "agent-battery-report-v1")], "service", source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinedBatteryReportFeaturePreservesSeparateAgentAndServiceCapabilities(bool hardware)
    {
        string source = new('a', 40);
        var agent = NativeArtifact.Inspect([.. Image("agent", schema: 2, agentBattery: true, temporary: hardware)], "agent", source);
        var service = NativeArtifact.Inspect([.. Image("service", schema: 2, agentBattery: true, temporary: hardware)], "service", source);
        Assert.Equal(agent.Features, service.Features);
        Assert.Equal("agent-battery-report-v1", agent.Capabilities);
        Assert.False(agent.PrivateCustody);
        Assert.False(agent.RequiresMailboxHandoff);
        Assert.True(service.PrivateCustody);
        Assert.Equal(!hardware, service.MetadataOnly);
        Assert.Equal(hardware, service.TemporaryChargeMutation);
        Assert.DoesNotContain("agent-battery-report-v1", service.Capabilities, StringComparison.Ordinal);
        Assert.Equal(hardware, service.RequiresMailboxHandoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialDisplayQueryCapabilityIsExactAndComposesWithBattery(bool agentBattery)
    {
        string source = new('a', 40);
        string expected = agentBattery ? "agent-battery-report-v1,session-display-query-partial-v1" : "session-display-query-partial-v1";
        var agent = NativeArtifact.Inspect([.. Image("agent", schema: 2, agentBattery: agentBattery, agentDisplay: true)], "agent", source);
        var service = NativeArtifact.Inspect([.. Image("service", schema: 2, agentBattery: agentBattery, agentDisplay: true)], "service", source);
        Assert.Equal(expected, agent.Capabilities);
        Assert.False(agent.PrivateCustody);
        Assert.False(agent.RequiresMailboxHandoff);
        Assert.Equal("private-service-custody-v1,service-metadata-only-v1", service.Capabilities);
        foreach (string forged in new[] { "none", "agent-display-query-v1", "session-display-v1", expected + ",session-display-query-partial-v1", "session-display-query-partial-v1,agent-battery-report-v1" })
        {
            Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, capabilities: forged, agentBattery: agentBattery, agentDisplay: true)], "agent", source));
        }
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, capabilities: expected, agentBattery: agentBattery)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 1, agentBattery: agentBattery, agentDisplay: true)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: true)], "service", source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialDisplayBrightnessWriteCapabilityIsExactAndQueryOnlyBuildsCannotClaimIt(bool agentBattery)
    {
        string source = new('a', 40);
        const string query = "session-display-query-partial-v1";
        string expected = agentBattery ? "agent-battery-report-v1," + query + ",session-display-brightness-write-partial-v1" : query + ",session-display-brightness-write-partial-v1";
        var agent = NativeArtifact.Inspect([.. Image("agent", schema: 2, agentBattery: agentBattery, agentDisplay: true, agentWrite: true)], "agent", source);
        Assert.Equal(expected, agent.Capabilities);
        Assert.False(agent.PrivateCustody);
        Assert.False(agent.RequiresMailboxHandoff);
        string queryOnly = agentBattery ? "agent-battery-report-v1," + query : query;
        foreach (string forged in new[] { "none", queryOnly, "session-display-brightness-write-partial-v1", expected + ",session-display-brightness-write-partial-v1", "session-display-brightness-write-partial-v1," + query })
        {
            Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, capabilities: forged, agentBattery: agentBattery, agentDisplay: true, agentWrite: true)], "agent", source));
        }
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: true)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, agentBattery: agentBattery, agentWrite: true)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 1, agentBattery: agentBattery, agentDisplay: true, agentWrite: true)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: true, agentWrite: true)], "service", source));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PartialDisplayHdrWriteCapabilityIsExactAndOtherBuildsCannotClaimIt(bool agentBattery, bool agentWrite)
    {
        string source = new('a', 40);
        const string hdr = "session-display-hdr-write-partial-v1";
        string expected = AgentCapabilities(agentBattery, display: true, agentWrite, hdr: true);
        var agent = NativeArtifact.Inspect([.. Image("agent", schema: 2, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: true)], "agent", source);
        Assert.Equal(expected, agent.Capabilities);
        Assert.False(agent.PrivateCustody);
        Assert.False(agent.MetadataOnly);
        string withoutHdr = AgentCapabilities(agentBattery, display: true, agentWrite, hdr: false);
        foreach (string forged in new[] { "none", withoutHdr, hdr, expected + "," + hdr, hdr + "," + withoutHdr })
        {
            Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, capabilities: forged, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: true)], "agent", source));
        }
        // Query-only and brightness-only builds cannot claim the HDR marker.
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, agentBattery: agentBattery, agentWrite: agentWrite, agentHdr: true)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 1, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: true)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: true)], "service", source));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void PartialDisplayRefreshWriteCapabilityIsExactAndOtherBuildsCannotClaimIt(bool agentBattery, bool agentWrite, bool agentHdr)
    {
        string source = new('a', 40);
        const string refresh = "session-display-refresh-write-partial-v1";
        string expected = AgentCapabilities(agentBattery, display: true, agentWrite, agentHdr, refresh: true);
        var agent = NativeArtifact.Inspect([.. Image("agent", schema: 2, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: agentHdr, agentRefresh: true)], "agent", source);
        Assert.Equal(expected, agent.Capabilities);
        Assert.False(agent.PrivateCustody);
        Assert.False(agent.MetadataOnly);
        string withoutRefresh = AgentCapabilities(agentBattery, display: true, agentWrite, agentHdr);
        foreach (string forged in new[] { "none", withoutRefresh, refresh, expected + "," + refresh, refresh + "," + withoutRefresh })
        {
            Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, capabilities: forged, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: agentHdr, agentRefresh: true)], "agent", source));
        }
        // Builds without the refresh write cannot claim its marker.
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: agentHdr)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, agentBattery: agentBattery, agentWrite: agentWrite, agentHdr: agentHdr, agentRefresh: true)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 1, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: agentHdr, agentRefresh: true)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: agentHdr, agentRefresh: true)], "service", source));
        // The refresh write requires the display query: a build without it cannot
        // claim the refresh marker alone under either schema.
        string alone = agentBattery ? "agent-battery-report-v1," + refresh : refresh;
        foreach (int schema in new[] { 1, 2 })
        {
            Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: schema, capabilities: alone, agentBattery: agentBattery, agentRefresh: true)], "agent", source));
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, true, true)]
    public void PartialDisplayColorWriteCapabilityIsExactAndOtherBuildsCannotClaimIt(bool agentBattery, bool agentWrite, bool agentHdr, bool agentRefresh)
    {
        string source = new('a', 40);
        const string color = "session-display-color-write-partial-v1";
        string expected = AgentCapabilities(agentBattery, display: true, agentWrite, agentHdr, agentRefresh, color: true);
        var agent = NativeArtifact.Inspect([.. Image("agent", schema: 2, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: agentHdr, agentRefresh: agentRefresh, agentColor: true)], "agent", source);
        Assert.Equal(expected, agent.Capabilities);
        Assert.False(agent.PrivateCustody);
        Assert.False(agent.MetadataOnly);
        string withoutColor = AgentCapabilities(agentBattery, display: true, agentWrite, agentHdr, agentRefresh);
        foreach (string forged in new[] { "none", withoutColor, color, expected + "," + color, color + "," + withoutColor })
        {
            Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, capabilities: forged, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: agentHdr, agentRefresh: agentRefresh, agentColor: true)], "agent", source));
        }
        // Builds without the color write cannot claim its marker.
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: agentHdr, agentRefresh: agentRefresh)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 1, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: agentHdr, agentRefresh: agentRefresh, agentColor: true)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: true, agentWrite: agentWrite, agentHdr: agentHdr, agentRefresh: agentRefresh, agentColor: true)], "service", source));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void ColorWriteRequiresTheDisplayQuery(bool agentBattery, int schema)
    {
        // A build selecting the color write without the display query is refused
        // whatever it claims: its own marker alone, or with the query marker it lacks.
        string source = new('a', 40);
        const string color = "session-display-color-write-partial-v1";
        string battery = agentBattery ? "agent-battery-report-v1," : string.Empty;
        foreach (string claimed in new[] { battery + color, battery + "session-display-query-partial-v1," + color })
        {
            var error = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: schema, capabilities: claimed, agentBattery: agentBattery, agentColor: true)], "agent", source));
            Assert.Equal("Color-profile write requires the display query contract.", error.Message);
        }
        // The same composition with the query is admitted (schema 2 only), so
        // the refusal above is the missing query, not the color write itself.
        if (schema == 2)
        {
            var admitted = NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, agentBattery: agentBattery, agentDisplay: true, agentColor: true)], "agent", source);
            Assert.Equal(battery + "session-display-query-partial-v1," + color, admitted.Capabilities);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialDisplayProfileCapabilityIsExactAndOtherBuildsCannotClaimIt(bool agentBattery)
    {
        string source = new('a', 40);
        const string profile = "session-display-profile-apply-partial-v1";
        string expected = AgentCapabilities(agentBattery, display: true, brightness: true, hdr: true, refresh: true, color: true, profile: true);
        byte[] Agent(int schema = 2, string? capabilities = null, bool agentProfile = true) => Image("agent", schema: schema, capabilities: capabilities,
            agentBattery: agentBattery, agentDisplay: true, agentWrite: true, agentHdr: true, agentRefresh: true, agentColor: true, agentProfile: agentProfile);
        var agent = NativeArtifact.Inspect([.. Agent()], "agent", source);
        Assert.Equal(expected, agent.Capabilities);
        Assert.Contains("agent-display-profile-apply-v1", agent.Features.Split(','));
        Assert.False(agent.PrivateCustody);
        Assert.False(agent.MetadataOnly);
        string withoutProfile = AgentCapabilities(agentBattery, display: true, brightness: true, hdr: true, refresh: true, color: true);
        foreach (string forged in new[] { "none", withoutProfile, profile, expected + "," + profile, profile + "," + withoutProfile })
        {
            Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. Agent(capabilities: forged)], "agent", source));
        }
        // A build with every write but without the profile cannot claim its marker.
        Assert.Equal(withoutProfile, NativeArtifact.Inspect([.. Agent(agentProfile: false)], "agent", source).Capabilities);
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. Agent(capabilities: expected, agentProfile: false)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. Agent(schema: 1)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: true, agentWrite: true, agentHdr: true, agentRefresh: true, agentColor: true, agentProfile: true)], "service", source));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    public void ProfileApplyRequiresEveryDisplayWriteItComposes(int missing, int schema)
    {
        // A build selecting the profile without one of its four writes is
        // refused whatever it claims, even the markers it would have.
        string source = new('a', 40);
        bool[] writes = [missing != 0, missing != 1, missing != 2, missing != 3];
        string claimed = AgentCapabilities(battery: false, display: true, writes[0], writes[1], writes[2], writes[3], profile: true);
        var error = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: schema, capabilities: claimed, agentDisplay: true, agentWrite: writes[0], agentHdr: writes[1], agentRefresh: writes[2], agentColor: writes[3], agentProfile: true)], "agent", source));
        Assert.Equal("Display profile apply requires every display write it composes.", error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SessionGpuCapabilityIsExactAndOtherBuildsCannotClaimIt(bool agentBattery, bool agentDisplay)
    {
        string source = new('a', 40);
        const string gpu = "session-gpu-apps-v1";
        string without = AgentCapabilities(agentBattery, agentDisplay, brightness: false, hdr: false);
        string expected = without == "none" ? gpu : without + "," + gpu;
        byte[] Agent(int schema = 2, string? capabilities = null, bool agentGpu = true) => Image("agent", schema: schema,
            capabilities: capabilities ?? expected, agentBattery: agentBattery, agentDisplay: agentDisplay, agentGpu: agentGpu);
        var agent = NativeArtifact.Inspect([.. Agent()], "agent", source);
        Assert.Equal(expected, agent.Capabilities);
        Assert.Contains("agent-gpu-v1", agent.Features.Split(','));
        Assert.False(agent.PrivateCustody);
        foreach (string forged in new[] { "none", without, gpu + "," + gpu, gpu + "," + without, expected + "," + gpu })
        {
            if (forged != expected)
            {
                Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. Agent(capabilities: forged)], "agent", source));
            }
        }
        // A build without the GPU feature cannot claim its marker.
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. Agent(agentGpu: false)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. Agent(schema: 1)], "agent", source));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, capabilities: expected, agentBattery: agentBattery, agentDisplay: agentDisplay, agentGpu: true)], "service", source));
    }

    [Fact]
    public void ProfileServingServiceIsNotAdmittedAsAnyComposition()
    {
        // The features and the capability build.rs gives a build with
        // service-profiles-v1. Packaging has not admitted that capability, so
        // the image is refused rather than taken for the temporary-charge
        // composition it extends.
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-profiles-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string temporary = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        const string profiles = temporary + ",service-profiles-v1";
        var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: profiles)], "service", new string('a', 40)));
        Assert.Equal("Profile serving is not admitted for packaging.", refused.Message);
        // Refused by name: an image that keeps the feature but claims only the
        // temporary-charge capabilities, in either role, is no better.
        refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: temporary)], "service", new string('a', 40)));
        Assert.Equal("Profile serving is not admitted for packaging.", refused.Message);
        // The Agent of the same build carries the Service features beside its own entry.
        string paired = features.Replace("lifecycle-evidence-compile-check,", "lifecycle-evidence-compile-check,native-agent-entry-compile-check,", StringComparison.Ordinal);
        refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, features: paired, capabilities: "none")], "agent", new string('a', 40)));
        Assert.Equal("Profile serving is not admitted for packaging.", refused.Message);
        // The refusal is the profile capability: without it this is the admitted
        // temporary-charge composition, which cannot claim it either.
        string extended = features.Replace("service-profiles-v1,", "", StringComparison.Ordinal);
        var admitted = NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: extended, capabilities: temporary)], "service", new string('a', 40));
        Assert.Equal("service-temporary-charge-v1", admitted.StartupPolicy);
        refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: extended, capabilities: profiles)], "service", new string('a', 40)));
        Assert.Equal("Profile serving is not admitted for packaging.", refused.Message);
    }

    [Fact]
    public void KeyboardDriverModeReadIsNotAdmittedByFeatureOrMarker()
    {
        // The features and the marker build.rs gives each admitted Service composition
        // built with service-keyboard-driver-mode-read-v1. Packaging has not admitted the
        // 00/84 read, so the image is refused by name rather than taken for the
        // composition it extends.
        const string marker = "service-keyboard-driver-mode-read-v1";
        const string refusal = "The keyboard Driver-mode read is not admitted for packaging.";
        (string Features, string Admitted, string Policy)[] compositions =
        [
            ("firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-hardware-compile-check,service-mailbox-observation-v1,startup-custody-v1",
                "private-service-custody-v1,service-mailbox-observation-v1", "service-mailbox-observation-v1"),
            ("controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-hardware-compile-check,service-mailbox-observation-v1,service-thermal-mutation-v1,startup-custody-v1",
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1", "service-thermal-mutation-v1"),
            ("controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1",
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1", "service-temporary-charge-v1"),
        ];
        foreach ((string features, string admitted, string policy) in compositions)
        {
            // Without the feature this is an admitted composition.
            Assert.Equal(policy, NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: features, capabilities: admitted)], "service", new string('a', 40)).StartupPolicy);
            string built = features.Replace("service-mailbox-observation-v1,", marker + ",service-mailbox-observation-v1,", StringComparison.Ordinal);
            // The build's own record, and one that keeps the feature but claims only the
            // composition it extends.
            foreach (string claimed in new[] { admitted + "," + marker, admitted })
            {
                var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                    [.. Image("service", schema: 2, features: built, capabilities: claimed)], "service", new string('a', 40)));
                Assert.Equal(refusal, refused.Message);
            }

            // The Agent of the same build carries the Service features beside its own entry.
            string paired = built.Replace("lifecycle-evidence-compile-check,", "lifecycle-evidence-compile-check,native-agent-entry-compile-check,", StringComparison.Ordinal);
            var agent = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, features: paired, capabilities: "none")], "agent", new string('a', 40)));
            Assert.Equal(refusal, agent.Message);
            // A forged marker without the feature is no better.
            var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: features, capabilities: admitted + "," + marker)], "service", new string('a', 40)));
            Assert.Equal(refusal, forged.Message);
        }
    }

    [Fact]
    public void KeyboardDriverModeOwnerIsNotAdmittedByFeatureOrMarker()
    {
        // Driver-mode ownership writes 00/04. The image is refused by its own name, before the
        // read feature it implies, even in a record that drops it and claims only
        // the admitted composition it would extend.
        string source = new('a', 40);
        const string owner = "service-keyboard-driver-mode-v1";
        const string refusal = "Keyboard Driver-mode ownership is not admitted for packaging.";
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        Assert.Equal("service-temporary-charge-v1", NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: admitted)], "service", source).StartupPolicy);
        string forged = features.Replace("service-mailbox-observation-v1,", owner + ",service-mailbox-observation-v1,", StringComparison.Ordinal);
        string built = features.Replace("service-mailbox-observation-v1,", "service-keyboard-driver-mode-read-v1," + owner + ",service-mailbox-observation-v1,", StringComparison.Ordinal);
        string marked = admitted + ",service-keyboard-driver-mode-read-v1," + owner;
        foreach (string withFeature in new[] { forged, built })
        {
            foreach (string claimed in new[] { marked, admitted })
            {
                var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                    [.. Image("service", schema: 2, features: withFeature, capabilities: claimed)], "service", source));
                Assert.Equal(refusal, refused.Message);
            }
        }
        // A forged marker without the feature is no better.
        var marker = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: admitted + "," + owner)], "service", source));
        Assert.Equal(refusal, marker.Message);
    }

    [Fact]
    public void ProfileTriggerServiceIsNotAdmittedByFeatureOrMarker()
    {
        string source = new('a', 40);
        const string message = "Automatic profile reconciliation is not admitted for packaging.";
        // The features and the capability build.rs gives a build with service-profile-triggers-v1.
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-profile-triggers-v1,service-profiles-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string temporary = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        const string marked = temporary + ",service-profiles-v1,service-profile-triggers-v1";
        // Refused by name, whatever the record claims.
        foreach (string capabilities in new[] { marked, temporary, "none" })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: features, capabilities: capabilities)], "service", source));
            Assert.Equal(message, refused.Message);
        }
        // Neither the feature nor the marker alone is admitted beside the admitted
        // temporary-charge composition.
        string extended = features.Replace("service-profile-triggers-v1,service-profiles-v1,", "", StringComparison.Ordinal);
        Assert.Equal("service-temporary-charge-v1", NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: extended, capabilities: temporary)], "service", source).StartupPolicy);
        string featureOnly = extended.Replace("service-temporary-charge-v1,", "service-profile-triggers-v1,service-temporary-charge-v1,", StringComparison.Ordinal);
        var feature = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: featureOnly, capabilities: temporary)], "service", source));
        Assert.Equal(message, feature.Message);
        var marker = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: extended, capabilities: temporary + ",service-profile-triggers-v1")], "service", source));
        Assert.Equal(message, marker.Message);
    }

    [Fact]
    public void ThermalConvergenceServiceIsNotAdmittedByFeatureOrMarker()
    {
        string source = new('a', 40);
        const string message = "Thermal convergence is not admitted for packaging.";
        // The features and the capability build.rs gives a build with service-thermal-convergence-v1.
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-profile-triggers-v1,service-profiles-v1,service-temporary-charge-v1,service-thermal-convergence-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string temporary = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        const string marked = temporary + ",service-profiles-v1,service-profile-triggers-v1,service-thermal-convergence-v1";
        // Refused by name before the triggers it extends, whatever the record claims.
        foreach (string capabilities in new[] { marked, temporary, "none" })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: features, capabilities: capabilities)], "service", source));
            Assert.Equal(message, refused.Message);
        }
        // Neither the feature nor the marker alone is admitted beside the admitted
        // temporary-charge composition.
        string extended = features.Replace("service-profile-triggers-v1,service-profiles-v1,", "", StringComparison.Ordinal)
            .Replace("service-thermal-convergence-v1,", "", StringComparison.Ordinal);
        Assert.Equal("service-temporary-charge-v1", NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: extended, capabilities: temporary)], "service", source).StartupPolicy);
        string featureOnly = extended.Replace("service-thermal-mutation-v1,", "service-thermal-convergence-v1,service-thermal-mutation-v1,", StringComparison.Ordinal);
        var feature = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: featureOnly, capabilities: temporary)], "service", source));
        Assert.Equal(message, feature.Message);
        var marker = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: extended, capabilities: temporary + ",service-thermal-convergence-v1")], "service", source));
        Assert.Equal(message, marker.Message);
    }

    [Fact]
    public void FanStopServiceIsNotAdmittedByFeatureOrMarker()
    {
        string source = new('a', 40);
        const string message = "Fan Stop is not admitted for packaging.";
        // The features and the capability build.rs gives a build with service-fan-stop-v1.
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-fan-stop-v1,service-hardware-compile-check,service-mailbox-observation-v1,service-profiles-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string temporary = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        const string marked = temporary + ",service-profiles-v1,service-fan-stop-v1";
        // Refused by name before the profiles it extends, whatever the record claims.
        foreach (string capabilities in new[] { marked, temporary, "none" })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: features, capabilities: capabilities)], "service", source));
            Assert.Equal(message, refused.Message);
        }
        // Neither the feature nor the marker alone is admitted beside the admitted
        // temporary-charge composition.
        string extended = features.Replace("service-profiles-v1,", "", StringComparison.Ordinal)
            .Replace("service-fan-stop-v1,", "", StringComparison.Ordinal);
        Assert.Equal("service-temporary-charge-v1", NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: extended, capabilities: temporary)], "service", source).StartupPolicy);
        string featureOnly = extended.Replace("service-fan-mutation-v2,", "service-fan-mutation-v2,service-fan-stop-v1,", StringComparison.Ordinal);
        var feature = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: featureOnly, capabilities: temporary)], "service", source));
        Assert.Equal(message, feature.Message);
        var marker = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: extended, capabilities: temporary + ",service-fan-stop-v1")], "service", source));
        Assert.Equal(message, marker.Message);
    }

    [Fact]
    public void GpuClockTuningServiceIsNotAdmittedByFeatureOrMarker()
    {
        string source = new('a', 40);
        const string message = "GPU clock tuning is not admitted for packaging.";
        // The features and the capability build.rs gives a build with service-gpu-clock-tuning-v1,
        // beside the triggers it implies.
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-gpu-clock-tuning-v1,service-hardware-compile-check,service-mailbox-observation-v1,service-profile-triggers-v1,service-profiles-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string temporary = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        const string marked = temporary + ",service-profiles-v1,service-profile-triggers-v1,service-gpu-clock-tuning-v1";
        // Refused by name before the triggers it extends, whatever the record claims.
        foreach (string capabilities in new[] { marked, temporary, "none" })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: features, capabilities: capabilities)], "service", source));
            Assert.Equal(message, refused.Message);
        }
        // Neither the feature nor the marker alone is admitted beside the admitted
        // temporary-charge composition.
        string extended = features.Replace("service-profile-triggers-v1,service-profiles-v1,", "", StringComparison.Ordinal)
            .Replace("service-gpu-clock-tuning-v1,", "", StringComparison.Ordinal);
        Assert.Equal("service-temporary-charge-v1", NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: extended, capabilities: temporary)], "service", source).StartupPolicy);
        string featureOnly = extended.Replace("service-fan-mutation-v2,", "service-fan-mutation-v2,service-gpu-clock-tuning-v1,", StringComparison.Ordinal);
        var feature = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: featureOnly, capabilities: temporary)], "service", source));
        Assert.Equal(message, feature.Message);
        var marker = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: extended, capabilities: temporary + ",service-gpu-clock-tuning-v1")], "service", source));
        Assert.Equal(message, marker.Message);
    }

    [Fact]
    public void LiveReadingReadIsNotAdmittedByFeatureOrMarker()
    {
        // The features and the marker build.rs gives each admitted Service composition
        // built with service-live-readings-v1. Packaging has not admitted the 0D/88 read,
        // so the image is refused by name rather than taken for the composition it extends.
        const string marker = "service-live-readings-v1";
        const string refusal = "The live-reading read is not admitted for packaging.";
        (string Features, string Admitted, string Policy)[] compositions =
        [
            ("firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-hardware-compile-check,service-mailbox-observation-v1,startup-custody-v1",
                "private-service-custody-v1,service-mailbox-observation-v1", "service-mailbox-observation-v1"),
            ("controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1",
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1", "service-temporary-charge-v1"),
        ];
        foreach ((string features, string admitted, string policy) in compositions)
        {
            Assert.Equal(policy, NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: features, capabilities: admitted)], "service", new string('a', 40)).StartupPolicy);
            string built = features.Replace("service-mailbox-observation-v1,", marker + ",service-mailbox-observation-v1,", StringComparison.Ordinal);
            foreach (string claimed in new[] { admitted + "," + marker, admitted })
            {
                var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                    [.. Image("service", schema: 2, features: built, capabilities: claimed)], "service", new string('a', 40)));
                Assert.Equal(refusal, refused.Message);
            }

            var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: features, capabilities: admitted + "," + marker)], "service", new string('a', 40)));
            Assert.Equal(refusal, forged.Message);
        }
    }

    [Fact]
    public void DiagnosticExportIsNotAdmittedByFeatureOrMarker()
    {
        // The features and the marker build.rs gives each admitted Service composition
        // built with service-diagnostics-export-v1. Packaging has not admitted exportDiagnostics,
        // so the image is refused by name rather than taken for the composition it extends.
        const string marker = "service-diagnostics-export-v1";
        const string refusal = "The diagnostic export is not admitted for packaging.";
        (string Features, string Admitted, string Policy)[] compositions =
        [
            ("firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-hardware-compile-check,service-mailbox-observation-v1,startup-custody-v1",
                "private-service-custody-v1,service-mailbox-observation-v1", "service-mailbox-observation-v1"),
            ("controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1",
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1", "service-temporary-charge-v1"),
        ];
        foreach ((string features, string admitted, string policy) in compositions)
        {
            Assert.Equal(policy, NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: features, capabilities: admitted)], "service", new string('a', 40)).StartupPolicy);
            string built = string.Join(',', features.Split(',').Append(marker).Order(StringComparer.Ordinal));
            foreach (string claimed in new[] { admitted + "," + marker, admitted })
            {
                var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                    [.. Image("service", schema: 2, features: built, capabilities: claimed)], "service", new string('a', 40)));
                Assert.Equal(refusal, refused.Message);
            }

            var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: features, capabilities: admitted + "," + marker)], "service", new string('a', 40)));
            Assert.Equal(refusal, forged.Message);
        }
    }

    [Fact]
    public void PerformanceShortcutIsNotAdmittedByFeatureOrMarker()
    {
        // The features and the marker build.rs gives the admitted temporary-charge composition
        // built with service-performance-shortcut-v1. Packaging has not admitted the shortcut or its
        // preview, so the image is refused by name rather than taken for the composition it extends.
        const string marker = "service-performance-shortcut-v1";
        const string refusal = "The performance shortcut is not admitted for packaging.";
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        Assert.Equal("service-temporary-charge-v1", NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: admitted)], "service", new string('a', 40)).StartupPolicy);
        string built = string.Join(',', features.Split(',').Append(marker).Order(StringComparer.Ordinal));
        foreach (string claimed in new[] { admitted + "," + marker, admitted })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: built, capabilities: claimed)], "service", new string('a', 40)));
            Assert.Equal(refusal, refused.Message);
        }

        var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: admitted + "," + marker)], "service", new string('a', 40)));
        Assert.Equal(refusal, forged.Message);
    }

    [Fact]
    public void HyperBoostExitIsNotAdmittedByFeatureOrMarker()
    {
        // The features and the marker build.rs gives the admitted temporary-charge composition
        // built with service-thermal-hyperboost-exit-v1. Packaging has not admitted the restore pair in
        // HyperBoost, so the image is refused by name rather than taken for the composition it extends.
        const string marker = "service-thermal-hyperboost-exit-v1";
        const string refusal = "The HyperBoost exit is not admitted for packaging.";
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        Assert.Equal("service-temporary-charge-v1", NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: admitted)], "service", new string('a', 40)).StartupPolicy);
        string built = string.Join(',', features.Split(',').Append(marker).Order(StringComparer.Ordinal));
        foreach (string claimed in new[] { admitted + "," + marker, admitted })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: built, capabilities: claimed)], "service", new string('a', 40)));
            Assert.Equal(refusal, refused.Message);
        }

        var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: admitted + "," + marker)], "service", new string('a', 40)));
        Assert.Equal(refusal, forged.Message);
    }

    [Fact]
    public void OwnedHyperBoostIsNotAdmittedByFeatureOrMarker()
    {
        // The features and markers build.rs gives the admitted temporary-charge composition built with
        // service-hyperboost-v1, which implies the exit. Packaging has not admitted the enable pair, so the
        // image is refused, and the owned feature or marker is refused by its own name without the exit.
        const string exit = "service-thermal-hyperboost-exit-v1";
        const string marker = "service-hyperboost-v1";
        const string refusal = "Owned HyperBoost is not admitted for packaging.";
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        string built = string.Join(',', features.Split(',').Append(exit).Append(marker).Order(StringComparer.Ordinal));
        foreach (string claimed in new[] { admitted + "," + exit + "," + marker, admitted + "," + marker, admitted })
        {
            Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: built, capabilities: claimed)], "service", new string('a', 40)));
        }
        string owned = string.Join(',', features.Split(',').Append(marker).Order(StringComparer.Ordinal));
        foreach (string claimed in new[] { admitted + "," + marker, admitted })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: owned, capabilities: claimed)], "service", new string('a', 40)));
            Assert.Equal(refusal, refused.Message);
        }

        var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: admitted + "," + marker)], "service", new string('a', 40)));
        Assert.Equal(refusal, forged.Message);
    }

    [Fact]
    public void KeyboardLightingIsNotAdmittedByFeatureOrMarker()
    {
        // The features and marker build.rs gives the admitted temporary-charge composition built with
        // service-lighting-v1. Packaging has not admitted the lighting setters, so the image is refused by
        // name, and the feature or a forged marker alone is refused too.
        const string marker = "service-lighting-v1";
        const string refusal = "Keyboard lighting is not admitted for packaging.";
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        string built = string.Join(',', features.Split(',').Append(marker).Order(StringComparer.Ordinal));
        foreach (string claimed in new[] { admitted + "," + marker, admitted })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: built, capabilities: claimed)], "service", new string('a', 40)));
            Assert.Equal(refusal, refused.Message);
        }

        var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: admitted + "," + marker)], "service", new string('a', 40)));
        Assert.Equal(refusal, forged.Message);
    }

    [Fact]
    public void DisplayBrightnessInputIsNotAdmittedByFeatureOrMarker()
    {
        // The features and marker build.rs gives the admitted temporary-charge composition built with
        // service-brightness-hid-usage-v1. Its IOCTLs have no readback and no capture evidence, so the
        // image is refused by name, and the marker alone is refused too.
        const string marker = "service-brightness-hid-usage-v1";
        const string refusal = "Native display-brightness input is not admitted for packaging.";
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        string built = string.Join(',', features.Split(',').Append("driver-api-trust").Append(marker).Order(StringComparer.Ordinal));
        foreach (string claimed in new[] { admitted + "," + marker, admitted })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: built, capabilities: claimed)], "service", new string('a', 40)));
            Assert.Equal(refusal, refused.Message);
        }

        var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: admitted + "," + marker)], "service", new string('a', 40)));
        Assert.Equal(refusal, forged.Message);
    }

    [Theory]
    [InlineData("service-lifecycle-shutdown-v1", "The native shutdown operation is not admitted for packaging.")]
    [InlineData("service-session-power-relay-v1", "The native session power relay is not admitted for packaging.")]
    public void LifecycleOperationsAreNotAdmittedByFeatureOrMarker(string marker, string refusal)
    {
        // The features and marker build.rs gives the admitted temporary-charge composition built with each
        // lifecycle operation. Neither has an attended validation, so the image is refused by name, and the
        // marker alone is refused too.
        const string features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
        const string admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
        string built = string.Join(',', features.Split(',').Append(marker).Order(StringComparer.Ordinal));
        foreach (string claimed in new[] { admitted + "," + marker, admitted })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("service", schema: 2, features: built, capabilities: claimed)], "service", new string('a', 40)));
            Assert.Equal(refusal, refused.Message);
        }

        var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, features: features, capabilities: admitted + "," + marker)], "service", new string('a', 40)));
        Assert.Equal(refusal, forged.Message);
    }

    [Fact]
    public void ApplicationReporterAgentIsNotAdmittedUntilPackagingAdmitsIt()
    {
        string source = new('a', 40);
        const string message = "The application reporter is not admitted for packaging.";
        // The feature and the marker build.rs gives a build with agent-application-reporter-v1.
        const string features = "agent-application-reporter-v1,native-agent-entry-compile-check";
        // Refused by name, whatever the record claims: the marker, the feature name,
        // neither, or the marker beside an admitted contract.
        foreach (string capabilities in new[]
        {
            "session-application-reporter-v1",
            "agent-application-reporter-v1",
            "none",
            "agent-battery-report-v1,session-application-reporter-v1",
        })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, features: features, capabilities: capabilities)], "agent", source));
            Assert.Equal(message, refused.Message);
            refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, features: "agent-application-reporter-v1,agent-battery-report-v1,native-agent-entry-compile-check", capabilities: capabilities)], "agent", source));
            Assert.Equal(message, refused.Message);
        }
        // A forged marker without the feature is no better.
        var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, features: "native-agent-entry-compile-check", capabilities: "session-application-reporter-v1")], "agent", source));
        Assert.Equal(message, forged.Message);
        // The same build without the reporter is still the admitted battery composition.
        var admitted = NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, features: "native-agent-entry-compile-check", agentBattery: true)], "agent", source);
        Assert.NotNull(admitted);
    }

    [Fact]
    public void FunctionRowAgentIsNotAdmittedUntilPackagingAdmitsIt()
    {
        string source = new('a', 40);
        const string message = "The function-row hook is not admitted for packaging.";
        // The feature and the marker build.rs gives a build with agent-function-row-v1.
        const string features = "agent-function-row-v1,native-agent-entry-compile-check";
        // Refused by name, whatever the record claims: the marker, the feature name,
        // neither, or the marker beside an admitted contract.
        foreach (string capabilities in new[]
        {
            "session-function-row-v1",
            "agent-function-row-v1",
            "none",
            "agent-battery-report-v1,session-function-row-v1",
        })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, features: features, capabilities: capabilities)], "agent", source));
            Assert.Equal(message, refused.Message);
            refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, features: "agent-battery-report-v1,agent-function-row-v1,native-agent-entry-compile-check", capabilities: capabilities)], "agent", source));
            Assert.Equal(message, refused.Message);
        }
        // A forged marker without the feature is no better.
        var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, features: "native-agent-entry-compile-check", capabilities: "session-function-row-v1")], "agent", source));
        Assert.Equal(message, forged.Message);
        // The same build without the hook is still the admitted battery composition.
        var admitted = NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, features: "native-agent-entry-compile-check", agentBattery: true)], "agent", source);
        Assert.NotNull(admitted);
    }

    [Fact]
    public void DisplayAutomationAgentIsNotAdmittedUntilPackagingAdmitsIt()
    {
        string source = new('a', 40);
        const string message = "Display automation is not admitted for packaging.";
        // The feature and the marker build.rs gives a build with agent-display-automation-v1.
        const string features = "agent-display-automation-v1,native-agent-entry-compile-check";
        // Refused by name, whatever the record claims: the marker, the feature name,
        // neither, or the marker beside an admitted contract.
        foreach (string capabilities in new[]
        {
            "session-display-automation-v1",
            "agent-display-automation-v1",
            "none",
            "agent-battery-report-v1,session-display-automation-v1",
        })
        {
            var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, features: features, capabilities: capabilities)], "agent", source));
            Assert.Equal(message, refused.Message);
            refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
                [.. Image("agent", schema: 2, features: "agent-battery-report-v1,agent-display-automation-v1,native-agent-entry-compile-check", capabilities: capabilities)], "agent", source));
            Assert.Equal(message, refused.Message);
        }
        // A forged marker without the feature is no better.
        var forged = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("agent", schema: 2, features: "native-agent-entry-compile-check", capabilities: "session-display-automation-v1")], "agent", source));
        Assert.Equal(message, forged.Message);
    }

    [Fact]
    public void ExplicitThermalMutationRoleRequiresSignedCapabilityAndMailboxCustody()
    {
        var artifact = NativeArtifact.Inspect([.. Image("service", schema: 2, mutation: true)], "service", new string('a', 40));
        Assert.True(artifact.ThermalMutation);
        Assert.True(artifact.RequiresMailboxHandoff);
        Assert.False(artifact.ReadOnlyMailbox);
        Assert.Equal("service-thermal-mutation-v1", artifact.StartupPolicy);
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, mutation: true, capabilities: "private-service-custody-v1,service-mailbox-observation-v1")], "service", new string('a', 40)));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect(
            [.. Image("service", schema: 2, mailbox: true, capabilities: "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1")], "service", new string('a', 40)));
        var readOnly = NativeArtifact.Inspect([.. Image("service", schema: 2, mailbox: true)], "service", new string('a', 40));
        Assert.True(readOnly.ReadOnlyMailbox);
        Assert.False(readOnly.ThermalMutation);
    }

    [Fact]
    public void SameSourceBuildsHaveDistinctArtifactIdentities()
    {
        var a = NativeArtifact.Inspect([.. Image("service", 'b')], "service", new string('a', 40));
        var b = NativeArtifact.Inspect([.. Image("service", 'c')], "service", new string('a', 40));
        Assert.NotEqual(a.Id, b.Id); Assert.NotEqual(a.Sha256, b.Sha256);
    }

    [Fact]
    public void UnboundIdentityAndAdditionalLoaderContractsDeny()
    {
        byte[] bytes = Image("service");
        bytes[0x400] ^= 1;
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. bytes], "service", new string('a', 40)));
        bytes = Image("service");
        "foreignx.dll\0"u8.CopyTo(bytes.AsSpan(0x840));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. bytes], "service", new string('a', 40)));
        bytes = Image("service"); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + 216), 1);
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. bytes], "service", new string('a', 40)));
    }

    [Fact]
    public async Task RealAuthenticatedPackageYieldsArtifactAndClosureCommitment()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        var f = NativeCanonical.ParseLines(package.Record, "OpenBlade.NativePackage.v2", 13);
        Assert.Equal("LocalDev", f[0]); Assert.Equal(package.Release.Manifest.InstallerSha256, f[2]);
        Assert.Equal(package.Service.Encode(), Convert.FromBase64String(f[8]));
        Assert.Equal(package.Agent.Encode(), Convert.FromBase64String(f[9]));
        Assert.Equal("two-executables-static-crt-system-imports-v2", f[10]);
    }

    // native-identity-bound-v1.json: identities padded to 1025 bytes, the
    // shared bound, and one byte over it. native_package::tests checks the Rust
    // readers against the same images and records.
    private static JsonElement[] IdentityBoundCases(out int bound)
    {
        using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-identity-bound-v1.json")));
        bound = data.RootElement.GetProperty("bound").GetInt32();
        return [.. data.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone())];
    }

    private static byte[] Base64(JsonElement item, string name) => Convert.FromBase64String(item.GetProperty(name).GetString()!);

    [Fact]
    public void CompiledIdentityIsAdmittedUpToTheSharedBoundAndNoFurther()
    {
        var cases = IdentityBoundCases(out int bound);
        Assert.Equal(NativeArtifact.MaximumIdentityBytes, bound);
        Assert.Equal(new[] { 1025, bound, bound, bound + 1 }, cases.Select(c => c.GetProperty("identityBytes").GetInt32()));
        foreach (var item in cases)
        {
            byte[] image = Base64(item, "image");
            string role = item.GetProperty("role").GetString()!;
            int section = 0x98 + 240;
            Assert.Equal(item.GetProperty("identityBytes").GetInt32(), BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(section + 8)));
            if (!item.GetProperty("accepted").GetBoolean())
            {
                var refused = Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. image], role, new string('a', 40)));
                Assert.Equal("Native artifact has no unique compiled build identity.", refused.Message);
                continue;
            }
            var artifact = NativeArtifact.Inspect([.. image], role, new string('a', 40));
            Assert.Equal(Base64(item, "record"), artifact.Encode());
            Assert.Contains("agent-display-color-write-v1", artifact.Features.Split(','));
        }
    }

    [Fact]
    public async Task PackageCarriesBothRolesAtTheIdentityBound()
    {
        var cases = IdentityBoundCases(out _);
        byte[] Role(string name) => Base64(cases.Single(c => c.GetProperty("name").GetString() == name), "image");
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files, Role("service-at-bound"), Role("agent-at-bound"));
        byte[] record = package.Record;
        Assert.InRange(record.Length, 1, 16384);
        var f = NativeCanonical.ParseLines(record, "OpenBlade.NativePackage.v2", 13);
        Assert.Equal(package.Service.Encode(), Convert.FromBase64String(f[8]));
        Assert.Equal(package.Agent.Encode(), Convert.FromBase64String(f[9]));
        // Each base64 record exceeds the general 2048-character field bound.
        Assert.All(f[8..10], field => Assert.InRange(field.Length, 2049, NativeArtifact.MaximumRecordBase64));
    }

    // Every field of the artifact and package records, with the limit it must
    // have: the widened fields named here, 2048 for all others, and no
    // widening of v1 artifact records.
    public static TheoryData<string, int, int, int> RecordFieldLimits()
    {
        var rows = new TheoryData<string, int, int, int>();
        foreach (var (header, count, widened) in new (string, int, Dictionary<int, int>)[]
        {
            ("OpenBlade.NativeArtifactRecord.v2", 11, new() { [4] = NativeArtifact.MaximumIdentityBytes, [10] = NativeArtifact.MaximumIdentityBytes }),
            ("OpenBlade.NativeArtifactRecord.v1", 10, new()),
            ("OpenBlade.NativePackage.v2", 12, new() { [8] = NativeArtifact.MaximumRecordBase64, [9] = NativeArtifact.MaximumRecordBase64 }),
        })
        {
            for (int index = 0; index < count; index++) { rows.Add(header, count, index, widened.GetValueOrDefault(index, 2048)); }
        }
        return rows;
    }

    [Theory]
    [MemberData(nameof(RecordFieldLimits))]
    public void RecordFieldsAdmitTheirLimitAndNoMore(string header, int count, int index, int limit)
    {
        string[] Fields(int length)
        {
            string[] fields = [.. Enumerable.Repeat("x", count)];
            fields[index] = new string('x', length);
            return fields;
        }
        Assert.Equal(header.Length + 1 + 2 * (count - 1) + limit + 1, NativeCanonical.Lines(header, Fields(limit)).Length);
        var refused = Assert.Throws<InvalidDataException>(() => NativeCanonical.Lines(header, Fields(limit + 1)));
        Assert.Equal("Canonical native records require bounded ASCII fields.", refused.Message);
    }

    // Encode never produces a record whose base64 the package field must refuse.
    [Fact]
    public void EncoderRefusesARecordThePackageCannotCarry()
    {
        var artifact = NativeArtifact.Inspect([.. Image("agent", schema: 2, agentBattery: true)], "agent", new string('a', 40)) with
        {
            Features = new string('f', NativeArtifact.MaximumIdentityBytes),
            Imports = new string('i', 2048),
            Capabilities = "",
        };
        var atBound = artifact with { Capabilities = new string('c', NativeArtifact.MaximumRecordBytes - artifact.Encode().Length) };
        byte[] record = atBound.Encode();
        Assert.Equal(NativeArtifact.MaximumRecordBytes, record.Length);
        Assert.Equal(NativeArtifact.MaximumRecordBase64, Convert.ToBase64String(record).Length);
        // The limit is the longest record whose base64 the package field admits:
        // 8191 to 8193 bytes all encode to the same 10924 characters.
        Assert.Equal(NativeArtifact.MaximumRecordBase64, Convert.ToBase64String(new byte[NativeArtifact.MaximumRecordBytes]).Length);
        Assert.True(Convert.ToBase64String(new byte[NativeArtifact.MaximumRecordBytes + 1]).Length > NativeArtifact.MaximumRecordBase64);
        var refused = Assert.Throws<InvalidDataException>(() => (atBound with { Capabilities = atBound.Capabilities + "c" }).Encode());
        Assert.Equal("Native artifact record exceeds the package bound.", refused.Message);
    }

    [Fact]
    public async Task ProvisionalOwnerLossPersistsDeniedPhaseAndRequiresExplicitRecovery()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        string epoch;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid())) { epoch = tx.Record.Epoch; }
        var record = NativeEpochRecord.Parse(files.ReadCurrent()!);
        Assert.Equal(epoch, record.Epoch); Assert.Equal(NativeEpochPhase.Provisional, record.Phase);
        Assert.Throws<InvalidDataException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, NativeCanonical.Digest(files.ReadCurrent()!));
        Assert.Equal(NativeEpochPhase.Unresolved, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
    }

    [Fact]
    public async Task CommitRetireAndIdenticalByteRollbackUseFreshEpochAndPredecessor()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        string prior; byte[] committed;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            prior = tx.Record.Epoch;
            Assert.Throws<InvalidDataException>(() => tx.CommitAfterBoundReadiness(new string('0', 64)));
            tx.CommitAfterBoundReadiness(new string('e', 64)); committed = files.ReadCurrent()!;
            Assert.Equal(NativeEpochPhase.Committed, NativeEpochRecord.Parse(committed).Phase);
            tx.RetirePackageSelection();
        }
        byte[] retired = files.ReadCurrent()!;
        using var rollback = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.NotEqual(prior, rollback.Record.Epoch); Assert.Equal(2, rollback.Record.Ordinal);
        Assert.Equal(NativeCanonical.Digest(retired), rollback.Record.Predecessor);
        Assert.Equal(NativeEpochRecord.Parse(committed).Package, rollback.Record.Package);
    }

    [Fact]
    public async Task ConflictingArtifactMappingDeniesEvenAcrossAbandonedPreparations()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var first = await Package(files);
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, first, Guid.NewGuid(), testCrashBoundary:
            reached => { if (reached == NativeEpochBoundary.PackageWritten) { throw new IOException("abandoned preparation"); } }));
        using var conflicting = await Package(files, marker: 9);
        Assert.Throws<NativeRecoveryException>(() => WindowsNativeEpochTransaction.Begin(files, conflicting, Guid.NewGuid()));
        Assert.Single(Directory.EnumerateFiles(files.Root, "package.bin", SearchOption.AllDirectories));
        Assert.Equal(NativeRecoveryReason.UnknownBootstrapHistory,
            Assert.Throws<NativeRecoveryException>(() => WindowsNativeEpochTransaction.Begin(files, first, Guid.NewGuid())).Reason);
    }

    [Theory]
    [InlineData((int)NativeEpochBoundary.Reserved)]
    [InlineData((int)NativeEpochBoundary.PackageWritten)]
    [InlineData((int)NativeEpochBoundary.OriginWritten)]
    [InlineData((int)NativeEpochBoundary.PhaseWritten)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    public async Task EveryPreparationCrashBoundaryPreservesEvidenceWithoutReplay(int injectedBoundary)
    {
        NativeEpochBoundary boundary = (NativeEpochBoundary)injectedBoundary;
        await CheckBoundary(boundary);
    }

    private static async Task CheckBoundary(NativeEpochBoundary boundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid(), testCrashBoundary:
            reached => { if (reached == boundary) { throw new IOException("injected crash"); } }));
        var reservations = Directory.EnumerateFiles(files.Root, "*.reserved").ToArray(); Assert.Single(reservations);
        if (boundary == NativeEpochBoundary.SelectionPublished)
        {
            Assert.Equal(NativeEpochPhase.Provisional, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
            Assert.Throws<InvalidDataException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        }
        else
        {
            Assert.Null(files.ReadCurrent());
            if (boundary == NativeEpochBoundary.FrontierWritten)
            { Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid())); }
        }
    }

    [Fact]
    public async Task ReplayedSelectionAndInterruptedFrontierDenyBeforeNewPublication()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        byte[] provisional;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        { provisional = files.ReadCurrent()!; tx.CommitAfterBoundReadiness(new string('e', 64)); }
        files.PublishCurrent(provisional);
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        string latest = Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last();
        string frontier = NativeCanonical.Digest(File.ReadAllBytes(latest));
        WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, frontier);
        byte[] unresolved = files.ReadCurrent()!;
        using var recovery = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid(), NativeCanonical.Digest(unresolved));
        Assert.Equal(2, recovery.Record.Ordinal);
    }

    [Fact]
    public async Task ImmutableFilesRemainWriteAndDeleteExcludedThroughTransaction()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.Throws<IOException>(() => File.Open(tx.ServicePath, FileMode.Open, FileAccess.Write, FileShare.Read));
        Assert.Throws<IOException>(() => File.Delete(tx.ServicePath));
        Assert.Throws<IOException>(() => Directory.Move(tx.GenerationDirectory, tx.GenerationDirectory + "-moved"));
    }

    [Fact]
    public void CrossLanguageCanonicalEpochVectorIsExactAndRejectsTrailingOrPhaseMutation()
    {
        using var vector = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-package-v1.json")));
        byte[] bytes = Convert.FromBase64String(vector.RootElement.GetProperty("epoch").GetString()!);
        var record = NativeEpochRecord.Parse(bytes);
        Assert.Equal(bytes, record.Encode()); Assert.Equal(NativeEpochPhase.Committed, record.Phase);
        Assert.Throws<InvalidDataException>(() => NativeEpochRecord.Parse([.. bytes, 10]));
        Assert.Throws<InvalidDataException>(() => NativeEpochRecord.Parse(Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(bytes).Replace("Committed", "Provisional", StringComparison.Ordinal))));
    }

    [Fact]
    public void CrossLanguageSignatureAndArtifactVectorUseActualCSharpContracts()
    {
        using var vector = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-package-v1.json")));
        byte[] Bytes(string name) => Convert.FromBase64String(vector.RootElement.GetProperty(name).GetString()!);
        byte[] spki = Bytes("spki");
        var policy = new OpenBladeReleaseTrustPolicy(true, [new(NativeCanonical.Digest(spki), Convert.ToBase64String(spki))]);
        var release = OpenBladeSignedReleaseVerifier.Verify(Bytes("manifest"), Bytes("signature"), policy);
        Assert.Equal("LocalDev", release.Manifest.Channel);
        Assert.Throws<OpenBladeReleaseVerificationException>(() => OpenBladeSignedReleaseVerifier.Verify(Bytes("manifest"), Bytes("wrongDomain"), policy));
        Assert.Equal(Bytes("serviceRecord"), NativeArtifact.Inspect([.. Bytes("serviceImage")], "service", release.Manifest.SourceRevision).Encode());
        Assert.Equal(Bytes("agentRecord"), NativeArtifact.Inspect([.. Bytes("agentImage")], "agent", release.Manifest.SourceRevision).Encode());
    }

    [Theory]
    [InlineData((int)NativeEpochBoundary.PhaseWritten)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    public async Task EveryCommitCrashBoundaryHasIndependentRecoveryMeaning(int injectedBoundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.Throws<IOException>(() => tx.CommitAfterBoundReadiness(new string('e', 64), reached =>
        { if ((int)reached == injectedBoundary) { throw new IOException("injected commit crash"); } }));
        var current = NativeEpochRecord.Parse(files.ReadCurrent()!);
        Assert.Equal(injectedBoundary == (int)NativeEpochBoundary.SelectionPublished ? NativeEpochPhase.Committed : NativeEpochPhase.Provisional, current.Phase);
        Assert.Throws<IOException>(() => tx.RequireProvisional());
    }

    [Fact]
    public void CompiledNativeBinariesExposeMetadataWhenReadOnlyDataInputsAreSupplied()
    {
        string? service = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_SERVICE_DATA");
        string? agent = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_AGENT_DATA");
        if (service is null || agent is null) { return; }
        string source = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_SOURCE") ?? throw new InvalidDataException();
        var a = NativeArtifact.Inspect([.. File.ReadAllBytes(service)], "service", source);
        var b = NativeArtifact.Inspect([.. File.ReadAllBytes(agent)], "agent", source);
        Assert.NotEqual(a.Id, b.Id); Assert.NotEqual(a.Sha256, b.Sha256);
        Assert.Equal(2, a.Schema); Assert.Equal(2, b.Schema);
        Assert.True(a.PrivateCustody);
        Assert.Equal(Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_COMPOSITION") != "hardware", a.MetadataOnly);
        bool agentBattery = b.Features.Split(',').Contains("agent-battery-report-v1", StringComparer.Ordinal);
        bool agentDisplay = b.Features.Split(',').Contains("agent-display-query-v1", StringComparer.Ordinal);
        bool agentWrite = b.Features.Split(',').Contains("agent-display-brightness-write-v1", StringComparer.Ordinal);
        bool agentHdr = b.Features.Split(',').Contains("agent-display-hdr-write-v1", StringComparer.Ordinal);
        bool agentRefresh = b.Features.Split(',').Contains("agent-display-refresh-write-v1", StringComparer.Ordinal);
        bool agentColor = b.Features.Split(',').Contains("agent-display-color-write-v1", StringComparer.Ordinal);
        bool agentProfile = b.Features.Split(',').Contains("agent-display-profile-apply-v1", StringComparer.Ordinal);
        string expectedAgentCapabilities = AgentCapabilities(agentBattery, agentDisplay, agentWrite, agentHdr, agentRefresh, agentColor, agentProfile);
        Assert.Equal(expectedAgentCapabilities, b.Capabilities); Assert.False(b.PrivateCustody); Assert.False(b.MetadataOnly);
        if (Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_COMPOSITION") == "combined")
        { Assert.Equal(a.Features, b.Features); Assert.Contains("service-metadata-only-v1", b.Features); }
    }

    [Theory]
    [InlineData((int)NativeEpochBoundary.PhaseWritten)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    public async Task ExplicitRecoveryReconcilesEachCrashBoundaryWithoutReplayingNativeWork(int injectedBoundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid())) { }
        string Frontier() => NativeCanonical.Digest(File.ReadAllBytes(Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last()));
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier(), reached =>
        { if ((int)reached == injectedBoundary) { throw new IOException("injected recovery crash"); } }));
        WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier());
        Assert.Equal(NativeEpochPhase.Unresolved, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
        using var fresh = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid(), NativeCanonical.Digest(files.ReadCurrent()!));
        Assert.Equal(2, fresh.Record.Ordinal);
    }

    [Theory]
    [InlineData((int)NativeFileBoundary.Created)]
    [InlineData((int)NativeFileBoundary.Written)]
    [InlineData((int)NativeFileBoundary.Flushed)]
    [InlineData((int)NativeFileBoundary.Verified)]
    [InlineData((int)NativeFileBoundary.Renamed)]
    public async Task AtomicPackageRecordCannotPoisonFutureEpochsAtAnyFilePublicationBoundary(int injectedBoundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        files.CrashBoundaryForTests = (NativeFileBoundary)injectedBoundary;
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        foreach (string record in Directory.EnumerateFiles(files.Root, "package.bin", SearchOption.AllDirectories))
        { Assert.Equal(package.Record, File.ReadAllBytes(record)); }
        files.CrashBoundaryForTests = null;
        // A real crash may leave a partial file/reservation. Keep every byte;
        // null selection cannot reset that history into a clean ordinal 1.
        string abandoned = Directory.EnumerateDirectories(files.Root).Single(p => Path.GetFileName(p) != "owner");
        File.WriteAllBytes(Path.Combine(abandoned, "receipt-abandoned.tmp"), [1, 2]);
        Assert.Throws<NativeRecoveryException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        Assert.Null(files.ReadCurrent());
        Assert.True(Directory.Exists(abandoned));
    }

    [Theory]
    [InlineData((int)NativeEpochBoundary.PhaseWritten)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    public async Task InterruptedRetirementRequiresFreshAuthenticatedCustodyAndIsIdempotent(int retirementBoundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        string epoch;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            epoch = tx.Record.Epoch; tx.MarkStartupSubmitted();
            NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, true);
            tx.CommitAfterBoundReadiness(new string('e', 64));
            Assert.Throws<IOException>(() => tx.RetirePackageSelection(reached =>
            { if ((int)reached == retirementBoundary) { throw new IOException("retirement interrupted"); } }));
            Assert.Throws<IOException>(() => tx.RequireProvisional());
        }
        string Frontier() => NativeCanonical.Digest(File.ReadAllBytes(Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last()));
        byte[] before = files.ReadCurrent()!;
        Assert.Equal(NativeRecoveryReason.RetirementPublicationFailed,
            Assert.Throws<NativeRecoveryException>(() => WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier())).Reason);
        Assert.Equal(before, files.ReadCurrent());
        using (var recovery = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        { recovery!.RetireRecoverableSelection(); }
        byte[] retired = files.ReadCurrent()!;
        using (var recovery = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        { recovery!.RetireRecoverableSelection(); }
        Assert.Equal(retired, files.ReadCurrent());
        Assert.Equal(NativeEpochPhase.Retired, NativeEpochRecord.Parse(retired).Phase);
        using var fresh = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.NotEqual(epoch, fresh.Record.Epoch);
        Assert.Equal(2, fresh.Record.Ordinal);
        Assert.Equal(NativeCanonical.Digest(retired), fresh.Record.Predecessor);
    }

    [Fact]
    public async Task HeldDeleteSharingCurrentKeepsOldBytesButFreshSelectionAndFrontierRejectOldGeneration()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        tx.MarkStartupSubmitted(); NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, true);
        tx.CommitAfterBoundReadiness(new string('e', 64));
        byte[] committed = files.ReadCurrent()!;
        // Same NT access/share/options as native_package::open_file(current,true).
        using var old = OpenNativeCurrent(Path.Combine(files.Root, "current.bin"));
        tx.RetirePackageSelection();
        byte[] retired = files.ReadCurrent()!;
        Assert.Equal(committed, WindowsNativeEpochFiles.ReadBounded(old, 16384));
        Assert.NotEqual(committed, retired);
        string frontier = Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last();
        Assert.Equal(retired, File.ReadAllBytes(frontier));
        // Actual transaction admission reopens the current path and frontier;
        // retaining/replaying the old committed object cannot select it again.
        tx.Dispose();
        files.PublishCurrent(committed);
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        using var recovery = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        Assert.Equal(retired, files.ReadCurrent());
    }

    [Theory]
    [InlineData((int)NativeFileBoundary.Created)]
    [InlineData((int)NativeFileBoundary.Written)]
    [InlineData((int)NativeFileBoundary.Flushed)]
    [InlineData((int)NativeFileBoundary.Verified)]
    [InlineData((int)NativeFileBoundary.Renamed)]
    public async Task RetirementFileFailureRecoversOnlyDurableIntent(int boundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            tx.MarkStartupSubmitted(); NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, true);
            tx.CommitAfterBoundReadiness(new string('e', 64));
            files.CrashBoundaryForTests = (NativeFileBoundary)boundary;
            Assert.Throws<IOException>(() => tx.RetirePackageSelection());
            Assert.Throws<IOException>(() => tx.RequireProvisional());
        }
        files.CrashBoundaryForTests = null;
        string Frontier() => NativeCanonical.Digest(File.ReadAllBytes(Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last()));
        if (boundary == (int)NativeFileBoundary.Renamed)
        {
            using var recovery = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
            recovery!.RetireRecoverableSelection();
        }
        else { WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier()); }
        var phase = NativeEpochRecord.Parse(files.ReadCurrent()!).Phase;
        Assert.Equal(boundary == (int)NativeFileBoundary.Renamed ? NativeEpochPhase.Retired : NativeEpochPhase.Unresolved, phase);
        byte[] recovered = files.ReadCurrent()!;
        using (var recovery = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        { Assert.Equal(phase, recovery!.Record.Phase); }
        Assert.Equal(recovered, files.ReadCurrent());
    }

    [Fact]
    public async Task RejectedConflictCannotPoisonCommittedOriginalAndItsFreshEpoch()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var first = await Package(files);
        using (var tx = WindowsNativeEpochTransaction.Begin(files, first, Guid.NewGuid()))
        { tx.CommitAfterBoundReadiness(new string('e', 64)); }
        using var conflict = await Package(files, marker: 9);
        byte[] committed = files.ReadCurrent()!;
        Assert.Throws<InvalidDataException>(() => WindowsNativeEpochTransaction.Begin(files, conflict, Guid.NewGuid()));
        Assert.Equal(committed, files.ReadCurrent());
        Assert.Single(Directory.EnumerateFiles(files.Root, "package.bin", SearchOption.AllDirectories));
        using var retried = WindowsNativeEpochTransaction.Begin(files, first, Guid.NewGuid());
        Assert.NotEqual(NativeEpochRecord.Parse(committed).Epoch, retried.Record.Epoch);
        Assert.Equal(2, retried.Record.Ordinal);
    }

    [Fact]
    public void PublicProjectionComesFromSameHeldEnrollmentAndMatchesRustCanonicalVector()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var v = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-package-v1.json")));
        byte[] enrollment = Convert.FromBase64String(v.RootElement.GetProperty("enrollment").GetString()!);
        string path = Path.Combine(files.Root, "enrollment.json");
        File.WriteAllBytes(path, enrollment);
        using var held = files.OpenFile(path);
        using var source = NativeLocalDevEnrollment.FromHeld(held);
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-verification-v1.json")));
        Assert.Equal(Convert.FromBase64String(expected.RootElement.GetProperty("projection").GetString()!), source.Projection);
        Assert.Equal(expected.RootElement.GetProperty("sha256").GetString(), NativeCanonical.Digest(source.Projection));
        string text = Encoding.ASCII.GetString(source.Projection);
        Assert.DoesNotContain("enrolledAccountSid", text);
        Assert.DoesNotContain("cpuVoltage", text);
        using var reconstructed = NativeLocalDevEnrollment.FromProtectedProjection(source.Projection);
        Assert.Equal(source.InstallationId, reconstructed.InstallationId);
        Assert.Equal(source.Policy.TrustedKeys, reconstructed.Policy.TrustedKeys);
        Assert.Throws<InvalidDataException>(() => NativeLocalDevEnrollment.FromProtectedProjection(Encoding.ASCII.GetBytes(text.Replace("LocalDev", "Stable"))));
    }

    [Theory]
    [InlineData("0x1200a9", true)]
    [InlineData("FA", false)]
    [InlineData("WD", false)]
    [InlineData("WO", false)]
    [InlineData("DC", false)]
    [InlineData("SD", false)]
    public void PublicRootRequiresExactProtectedUsersReadExecuteWithoutMutation(string rights, bool accepted)
    {
        var descriptor = new System.Security.AccessControl.RawSecurityDescriptor($"O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;{rights};;;BU)");
        Assert.Equal(accepted, WindowsNativeEpochFiles.AcceptsPublicRoot(descriptor));
        Assert.False(WindowsNativeEpochFiles.AcceptsPublicRoot(new System.Security.AccessControl.RawSecurityDescriptor("O:BAG:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)")));
    }

    [Fact]
    public async Task PublicProjectionIsCommittedBeforeSelectionAndRetainedWithImmutableGeneration()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid(), testCrashBoundary: reached =>
        {
            if (reached == NativeEpochBoundary.SelectionPublished)
            {
                var record = NativeEpochRecord.Parse(files.ReadCurrent()!);
                string root = Path.Combine(files.Root, record.Epoch);
                Assert.Equal(package.VerificationProjection, File.ReadAllBytes(Path.Combine(root, "verification.bin")));
                Assert.Equal(record.Owner, NativeCanonical.Digest(File.ReadAllBytes(Path.Combine(root, "startup-owner.bin"))));
            }
        });
        Assert.Throws<IOException>(() => File.Open(Path.Combine(tx.GenerationDirectory, "verification.bin"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        tx.CommitAfterBoundReadiness(new string('e', 64));
    }

    [Fact]
    public async Task ActualSignedCleanupNeedsOriginalStatusDrainAndCannotClearFirmwareOwnerHistory()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        string epoch;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        { epoch = tx.Record.Epoch; tx.CommitAfterBoundReadiness(new string('e', 64)); }
        using var cleanup = await WindowsNativeInstalledSelection.OpenAsync(files, CancellationToken.None);
        Assert.Equal(Path.Combine(files.Root, epoch, "app", "OpenBlade.Service.exe"), cleanup.ServicePath);
        Assert.Throws<NativeRecoveryException>(() => cleanup.RetireAfterStatusDrain(17, 1));
        files.Publish(Path.Combine(files.Root, epoch, "status-drain-17-1.bin"), NativeCanonical.Lines("OpenBlade.NativeStatusDrain.v1", epoch, "17", "1"));
        string owner = Path.Combine(files.Root, "owner");
        files.Publish(Path.Combine(owner, "owner-00000000000000000001.bin"), NativeCanonical.Lines("OpenBlade.NativeOwner.v1", "Active", "1", new string('0', 64), cleanup.Record.Installation, epoch,
            package.Service.Id, "17:1", new string('f', 64)));
        Assert.Throws<NativeRecoveryException>(() => cleanup.RetireAfterStatusDrain(17, 1));
        Assert.Equal(NativeEpochPhase.Committed, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
    }

    [Fact]
    public void FixedRootPresenceRequiresExplicitAbsenceAndPreservesUnknownHistory()
    {
        Assert.True(WindowsNativeEpochFiles.ObserveDirectoryPresence(() => FileAttributes.Directory));
        Assert.False(WindowsNativeEpochFiles.ObserveDirectoryPresence(() => throw new FileNotFoundException()));
        Assert.False(WindowsNativeEpochFiles.ObserveDirectoryPresence(() => throw new DirectoryNotFoundException()));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsNativeEpochFiles.ObserveDirectoryPresence(() => throw new UnauthorizedAccessException("denied")));
        Assert.Throws<IOException>(() => WindowsNativeEpochFiles.ObserveDirectoryPresence(() => throw new IOException("unknown history")));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsNativeEpochFiles.ObserveDirectoryPresence(() => FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsNativeEpochFiles.ObserveDirectoryPresence(() => FileAttributes.Normal));
    }

    [Fact]
    public void UnknownOwnerEntryCannotBecomeAnEmptyHistoryRetirementCertificate()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        string owner = Path.Combine(files.Root, "owner");
        files.CreateDirectoryOnce(owner);
        File.WriteAllBytes(Path.Combine(owner, "writer.lock"), []);
        WindowsNativeTrustLifecycle.RequireOwnerRetired(files);
        files.Publish(Path.Combine(owner, "unrecognized.bin"), NativeCanonical.Lines("unknown-owner-state", "retain"));
        Assert.Throws<NativeRecoveryException>(() => WindowsNativeTrustLifecycle.RequireOwnerRetired(files));
    }

    private static FileStream OpenNativeCurrent(string path)
    {
        var handle = CreateFileW(path, 0x00120089, 5, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid) { throw new IOException("Exact native reader open failed.", new System.ComponentModel.Win32Exception()); }
        return new FileStream(handle, FileAccess.Read);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share,
        nint security, uint creation, uint flags, nint template);

    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
}
