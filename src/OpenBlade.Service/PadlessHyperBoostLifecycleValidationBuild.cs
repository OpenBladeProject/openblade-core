using System.Reflection;
using System.Security.Cryptography;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record PadlessHyperBoostLifecycleValidationBuildIdentity(
    bool Enabled,
    string? SourceRevision,
    string ServiceAssemblySha256,
    string AdmissionManifestSha256)
{
    internal static PadlessHyperBoostLifecycleValidationBuildIdentity ReadCurrent()
    {
        Assembly assembly = typeof(PadlessHyperBoostLifecycleValidationBuildIdentity)
            .Assembly;
        IReadOnlyDictionary<string, string> metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(value => value.Key is not null && value.Value is not null)
            .ToDictionary(
                value => value.Key!,
                value => value.Value!,
                StringComparer.Ordinal);
        bool enabled = metadata.TryGetValue(
                "OpenBladePadlessHyperBoostLifecycleValidation",
                out string? marker)
            && marker.Equals("true", StringComparison.OrdinalIgnoreCase);
        metadata.TryGetValue("OpenBladeValidationSourceRevision", out string? revision);
        string path = assembly.Location;
        string assemblySha256 = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return new(
            enabled,
            revision,
            assemblySha256,
            HyperBoostWithoutCoolingPadAdmissionSource.TrustedManifestSha256);
    }

    internal bool Matches(
        string expectedSourceRevision,
        string expectedServiceAssemblySha256,
        string expectedAdmissionManifestSha256) =>
        Enabled
        && IsHex(SourceRevision, 40)
        && string.Equals(
            SourceRevision,
            expectedSourceRevision,
            StringComparison.OrdinalIgnoreCase)
        && IsHex(ServiceAssemblySha256, 64)
        && ServiceAssemblySha256.Equals(
            expectedServiceAssemblySha256,
            StringComparison.OrdinalIgnoreCase)
        && AdmissionManifestSha256.Equals(
            expectedAdmissionManifestSha256,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(Uri.IsHexDigit);
}
