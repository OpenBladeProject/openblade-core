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
}
