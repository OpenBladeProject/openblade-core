#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Reflection;
using System.Security.Cryptography;

namespace OpenBlade.Setup.Tests;

// Private test-build provenance, never installed-product authority.
internal static class ScratchFixtureBuildPin
{
    internal static void Require(string imagePath)
    {
        string Pin(string name) => typeof(ScratchFixtureBuildPin).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == name).Value!;
        using var image = File.OpenRead(imagePath);
        using var provenance = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "scratch-receiver-build.json"));
        RequireHashes(image, provenance, Pin("OpenBladeScratchReceiverSha256"), Pin("OpenBladeScratchReceiverBuildSha256"));
    }

    internal static void RequireHashes(Stream image, Stream provenance, string expectedImage, string expectedProvenance)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(image)), expectedImage, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(provenance)), expectedProvenance, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The scratch receiver differs from this test assembly's exact source/configuration build.");
        }
    }
}
#endif
