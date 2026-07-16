using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProtectionOffValidationPortTests
{
    [Fact]
    public void Port_ExposesOnlyFixedTypedSequenceOperations()
    {
        string[] methods = typeof(IProtectionOffValidationPort).GetMethods()
            .Select(method => method.Name).Order().ToArray();

        Assert.Equal(
            ["ApplyProtectionOffAsync", "QueryProtectionAsync", "QueryTemporaryFullChargeAsync", "RestoreLimit80Async"],
            methods);
        Assert.Contains(typeof(IAsyncDisposable), typeof(IProtectionOffValidationPort).GetInterfaces());
        Assert.All(typeof(IProtectionOffValidationPort).GetMethods(), method =>
        {
            Assert.DoesNotContain(method.GetParameters(), parameter =>
                parameter.ParameterType == typeof(BatteryValidationSetting)
                || parameter.ParameterType == typeof(byte)
                || parameter.ParameterType == typeof(byte[])
                || parameter.ParameterType == typeof(ReadOnlyMemory<byte>));
        });
        Assert.DoesNotContain(typeof(IProtectionOffValidationPort).GetMembers(), member =>
            member.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Catalog", StringComparison.OrdinalIgnoreCase));
    }
}
