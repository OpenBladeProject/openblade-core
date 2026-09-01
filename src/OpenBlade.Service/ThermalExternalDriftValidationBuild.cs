using System.Reflection;
using System.Security.Cryptography;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record ThermalExternalDriftValidationBuildIdentity(
    bool Enabled,
    string? SourceRevision,
    string ServiceAssemblySha256,
    string ProtocolAssemblySha256,
    string ProductionFixtureSha256)
{
    internal static ThermalExternalDriftValidationBuildIdentity ReadCurrent()
    {
        Assembly serviceAssembly = typeof(ThermalExternalDriftValidationBuildIdentity)
            .Assembly;
        IReadOnlyDictionary<string, string> metadata = serviceAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(value => value.Key is not null && value.Value is not null)
            .ToDictionary(
                value => value.Key!,
                value => value.Value!,
                StringComparer.Ordinal);
        bool enabled = metadata.TryGetValue(
                "OpenBladeThermalExternalDriftValidation",
                out string? marker)
            && marker.Equals("true", StringComparison.OrdinalIgnoreCase);
        metadata.TryGetValue("OpenBladeValidationSourceRevision", out string? revision);

        Assembly protocolAssembly = typeof(ProductionBatteryFactory).Assembly;
        ReadOnlyMemory<byte>? fixture = ProductionFixtureSource.Read();
        return new(
            enabled,
            revision,
            HashAssembly(serviceAssembly),
            HashAssembly(protocolAssembly),
            fixture is null
                ? string.Empty
                : Convert.ToHexString(SHA256.HashData(fixture.Value.Span)));
    }

    internal bool Matches(
        string expectedSourceRevision,
        string expectedServiceAssemblySha256,
        string expectedProtocolAssemblySha256,
        string expectedProductionFixtureSha256) =>
        Enabled
        && IsHex(SourceRevision, 40)
        && string.Equals(
            SourceRevision,
            expectedSourceRevision,
            StringComparison.OrdinalIgnoreCase)
        && IsMatchingHash(ServiceAssemblySha256, expectedServiceAssemblySha256)
        && IsMatchingHash(ProtocolAssemblySha256, expectedProtocolAssemblySha256)
        && IsMatchingHash(
            ProductionFixtureSha256,
            expectedProductionFixtureSha256)
        && TrustedFixtureHashRegistry.Production.Contains(
            Convert.FromHexString(ProductionFixtureSha256));

    private static string HashAssembly(Assembly assembly)
    {
        string path = assembly.Location;
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static bool IsMatchingHash(string actual, string expected) =>
        IsHex(actual, 64)
        && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(Uri.IsHexDigit);
}
