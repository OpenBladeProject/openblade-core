using System.Runtime.CompilerServices;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class SourceIdentityContractTests
{
    [Fact]
    public void ProtocolAssemblyUsesOpenBladeIdentityAndFriendAssembly()
    {
        Assert.Equal("OpenBlade.Protocol", typeof(TargetBladeDevice).Assembly.GetName().Name);
        Assert.Equal("OpenBlade.Protocol.Tests", typeof(SourceIdentityContractTests).Assembly.GetName().Name);
        Assert.Contains(
            typeof(TargetBladeDevice).Assembly.GetCustomAttributes(typeof(InternalsVisibleToAttribute), false)
                .Cast<InternalsVisibleToAttribute>(),
            attribute => attribute.AssemblyName == "OpenBlade.Protocol.Tests");
    }
}
