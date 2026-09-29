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
    internal const string ManifestSha256 = "A18E7BA301C87A0BA41BA0F39FECDFC4FC3A43660AE5DE28077C2863E7165237";
    private const string ResourceName = "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_PadlessLifecyclePreparation.json";

    internal static HyperBoostWithoutCoolingPadEvidenceGates ReadReadiness()
    {
        using Stream stream = typeof(Rz090528PadlessLifecyclePreparation).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new UnvalidatedCommandException("The exact-model lifecycle preparation descriptor is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Validate(bytes.ToArray());
    }

    internal static readonly string[] GateNames =
    [
        "exactDeviceAndFirmwareReviewed", "immediateNoLoadApplyReadbackRestoreValidated",
        "sustainedNoLoadPlanReviewed", "sustainedNoLoadExecutionValidated", "postRestorationAbsenceValidated",
        "reviewedLoadPlanReviewed", "reviewedLoadValidated", "adapterTransitionValidated",
        "lowPowerLifecycleValidated", "crashRecoveryValidated",
    ];

    // Only reviewed 2025 capture annotations and 2025 plan fixtures may back a gate.
    internal static bool IsAdmittedArtifact(string? artifact) =>
        artifact is not null && !artifact.Contains("..", StringComparison.Ordinal) && !artifact.Contains('\\')
        && (artifact.StartsWith("captures/annotations/", StringComparison.Ordinal) && artifact.Contains("-rz09-0528-", StringComparison.Ordinal)
            || artifact.StartsWith("src/OpenBlade.Protocol/Fixtures/Rz09_0528_02c6_2_02_", StringComparison.Ordinal))
        && artifact.EndsWith(".json", StringComparison.Ordinal);

    internal static HyperBoostWithoutCoolingPadEvidenceGates Validate(ReadOnlyMemory<byte> bytes)
    {
        if (Convert.ToHexString(SHA256.HashData(bytes.Span)) != ManifestSha256)
        {
            throw new UnvalidatedCommandException("The lifecycle readiness descriptor hash is not trusted.");
        }
        return ValidateContent(bytes);
    }

    // Semantic rules, separated from the hash pin so they are testable on modified descriptors.
    internal static HyperBoostWithoutCoolingPadEvidenceGates ValidateContent(ReadOnlyMemory<byte> bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 2
            || root.GetProperty("scope").GetString() != "Developer lifecycle validation readiness only; not production admission"
            || root.GetProperty("model").GetString() != "RZ09-0528"
            || root.GetProperty("sku").GetString() != "RZ09-05289EN4"
            || root.GetProperty("biosVersion").GetString() != "2.02"
            || root.GetProperty("baselineMode").GetString() != "Performance"
            || root.GetProperty("fanMode").GetString() != "Automatic")
        {
            throw new UnvalidatedCommandException("The lifecycle readiness descriptor does not describe the exact 2025 transition.");
        }
        JsonElement gates = root.GetProperty("gates");
        if (gates.EnumerateObject().Count() != GateNames.Length)
        {
            throw new UnvalidatedCommandException("The lifecycle readiness descriptor must name exactly the reviewed gates.");
        }
        bool Gate(string name)
        {
            JsonElement gate = gates.GetProperty(name);
            bool validated = gate.GetProperty("validated").GetBoolean();
            if (!validated) { return false; }
            // Lifecycle gates are only ever satisfied by installed lifecycle runs, never by this descriptor.
            if (name is "lowPowerLifecycleValidated" or "crashRecoveryValidated")
            {
                throw new UnvalidatedCommandException("Lifecycle gates cannot be claimed by readiness evidence.");
            }
            foreach (JsonProperty property in gate.EnumerateObject())
            {
                if (property.Name is "validated") { continue; }
                if (property.Name is not ("evidence" or "supplementalEvidence")
                    || !IsAdmittedArtifact(property.Value.GetProperty("artifact").GetString())
                    || property.Value.GetProperty("sha256").GetString() is not { Length: 64 } sha
                    || !sha.All(Uri.IsHexDigit))
                {
                    throw new UnvalidatedCommandException("Every validated gate must cite an admitted, hash-pinned 2025 artifact.");
                }
            }
            return gate.TryGetProperty("evidence", out _);
        }
        return new(Gate(GateNames[0]), Gate(GateNames[1]), Gate(GateNames[2]), Gate(GateNames[3]), Gate(GateNames[4]),
            Gate(GateNames[5]), Gate(GateNames[6]), Gate(GateNames[7]), Gate(GateNames[8]), Gate(GateNames[9]));
    }
}
