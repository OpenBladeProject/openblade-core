using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace OpenBlade.Deployment.Tests;

public sealed class DeploymentVariantIsolationTests
{
    [MixedVariantInputs]
    public void ActualMixedBuildProductAndAlgorithmOutputsHaveDistinctMetadata()
    {
        string product = Environment.GetEnvironmentVariable("OPENBLADE_PRODUCT_DEPLOYMENT_DATA")!;
        string algorithm = Environment.GetEnvironmentVariable("OPENBLADE_TEST_DEPLOYMENT_DATA")!;
        Assert.NotEqual(Path.GetFullPath(product), Path.GetFullPath(algorithm));
        var productMethods = ReadMethods(product);
        var algorithmMethods = ReadMethods(algorithm);
        foreach (string seam in new[] { "NativeLocalDevEnrollment.ForAlgorithmTests", "WindowsNativeEpochFiles.get_CrashBoundaryForTests", "WindowsNativeEpochFiles.get_PublishedForTests" })
        {
            Assert.DoesNotContain(seam, productMethods);
            Assert.Contains(seam, algorithmMethods);
        }
    }

    private static HashSet<string> ReadMethods(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        HashSet<string> methods = [];
        foreach (var definition in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(definition);
            foreach (var method in type.GetMethods())
            { methods.Add(metadata.GetString(type.Name) + "." + metadata.GetString(metadata.GetMethodDefinition(method).Name)); }
        }
        return methods;
    }

    private sealed class MixedVariantInputsAttribute : FactAttribute
    {
        public MixedVariantInputsAttribute()
        {
            if (Environment.GetEnvironmentVariable("OPENBLADE_PRODUCT_DEPLOYMENT_DATA") is null
                || Environment.GetEnvironmentVariable("OPENBLADE_TEST_DEPLOYMENT_DATA") is null)
            { Skip = "Requires the independently built mixed-reference product and test outputs as data."; }
        }
    }
}
