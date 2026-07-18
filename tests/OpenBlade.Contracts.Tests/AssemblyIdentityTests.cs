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
        Assert.Matches(
            "^0\\.1\\.0\\+[0-9a-f]{8}$",
            OpenBladeProductVersion.Read(assembly));
        Assert.Equal(new Version(0, 1, 0, 0), assembly.GetName().Version);
        Assert.Equal(
            "0.1.0.0",
            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
        Assert.Matches(
            "^0\\.1\\.0\\+[0-9a-f]{40}$",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }
}
