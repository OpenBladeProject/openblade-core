using System.Reflection;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class AssemblyIdentityTests
{
    [Fact]
    public void ProductionAndTestAssembliesUseOpenBladeNames()
    {
        Assert.Equal("OpenBlade.Contracts", typeof(ServiceRequest).Assembly.GetName().Name);
        Assert.Equal("OpenBlade.Contracts.Tests", typeof(AssemblyIdentityTests).Assembly.GetName().Name);
    }

    [Fact]
    public void ProductVersionIsExplicitAndReadableFromBuiltAssemblies()
    {
        Assembly assembly = typeof(ServiceRequest).Assembly;
        Version version = Assert.IsType<Version>(assembly.GetName().Version);
        Assert.Equal(0, version.Revision);
        string baseVersion = version.ToString(3);

        string productVersion = OpenBladeProductVersion.Read(assembly);
        Assert.StartsWith($"{baseVersion}+", productVersion, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{8}$", productVersion[(baseVersion.Length + 1)..]);
        Assert.Equal(
            $"{baseVersion}.0",
            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);

        string informationalVersion = Assert.IsType<string>(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        Assert.StartsWith($"{baseVersion}+", informationalVersion, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{40}$", informationalVersion[(baseVersion.Length + 1)..]);
    }
}
