using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record PadlessHyperBoostLifecycleTarget(string Model, PerformancePreset BaseMode,
    string ManifestSha256, HyperBoostWithoutCoolingPadEvidenceGates Readiness)
{
    internal static PadlessHyperBoostLifecycleTarget? ForExactIdentity(HostIdentityReadResult identity)
    {
        if (identity.Failure is not null || identity.FirmwareCompatibility is not
            (HostFirmwareCompatibility.Tested or HostFirmwareCompatibility.ModelScoped or HostFirmwareCompatibility.UntestedAllowed))
        {
            return null;
        }
        return identity.Evidence switch
        {
            { Manufacturer: "Razer", ProductName: "Blade 16 - RZ09-0528", Sku: "RZ09-05289EN4", BiosVersion: "2.02" } =>
                new("RZ09-0528", PerformancePreset.Performance, Rz090528PadlessLifecyclePreparation.ManifestSha256,
                    Rz090528PadlessLifecyclePreparation.ReadReadiness()),
            { Manufacturer: "Razer", ProductName: "Blade 16 - RZ09-0581", Sku: "RZ09-05819EN4" } =>
                new("RZ09-0581", PerformancePreset.Balanced, HyperBoostWithoutCoolingPadAdmissionSource.TrustedManifestSha256,
                    HyperBoostWithoutCoolingPadEvidenceGates.Current),
            _ => null,
        };
    }

    internal static PadlessHyperBoostLifecycleTarget? ForCurrentWindowsHost()
    {
        if (!OperatingSystem.IsWindows()) { return null; }
        var registry = new WindowsRegistryValueSource();
        var platform = new RuntimeWindowsPlatformEvidenceSource(registry);
        HostIdentityReadResult rz090528 = new WindowsHostIdentitySource(registry, platform,
            WindowsHostIdentitySource.Rz090528Identity with { FirmwareAdmission = FirmwareAdmissionScope.ExactVersions })
            .ReadExactIdentity();
        return ForExactIdentity(rz090528)
            ?? ForExactIdentity(new WindowsHostIdentitySource(registry, platform).ReadExactIdentity());
    }
}

internal static class Rz090528PadlessLifecyclePreparation
{
    internal const string ManifestSha256 = "1BB16AC542BBB410E0C2DC012D9BE973234B97E8D5FDB9C563714B83C21A0E3A";
    private const string ResourceName = "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_PadlessLifecyclePreparation.json";

    internal static HyperBoostWithoutCoolingPadEvidenceGates ReadReadiness()
    {
        using Stream stream = typeof(Rz090528PadlessLifecyclePreparation).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new UnvalidatedCommandException("The exact-model lifecycle preparation descriptor is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Validate(bytes.ToArray());
    }

    internal static HyperBoostWithoutCoolingPadEvidenceGates Validate(ReadOnlyMemory<byte> bytes)
    {
        if (Convert.ToHexString(SHA256.HashData(bytes.Span)) != ManifestSha256)
        {
            throw new UnvalidatedCommandException("The lifecycle preparation descriptor hash is not trusted.");
        }
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.GetProperty("scope").GetString() != "Developer lifecycle validation preparation only; not admission evidence"
            || root.GetProperty("model").GetString() != "RZ09-0528"
            || root.GetProperty("sku").GetString() != "RZ09-05289EN4"
            || root.GetProperty("biosVersion").GetString() != "2.02"
            || root.GetProperty("baselineMode").GetString() != "Performance"
            || root.GetProperty("gates").EnumerateObject().Count() != 10
            || root.GetProperty("gates").EnumerateObject().Any(gate => gate.Value.ValueKind != JsonValueKind.False))
        {
            throw new UnvalidatedCommandException("Lifecycle preparation must not claim reviewed admission gates.");
        }
        return new(false, false, false, false, false, false, false, false, false, false);
    }
}
