using System.Security.Cryptography;

namespace OpenBlade.Contracts.Tests;

public sealed class Blade16SourceFixtureIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionIdentityIsTheHashOfTheEmbeddedSourceBytes(bool amd)
    {
        string type = amd ? nameof(Blade16AmdKeyboardLightingLayout) : nameof(Blade16KeyboardLightingLayout);
        using Stream stream = typeof(Blade16KeyboardLightingLayout).Assembly.GetManifestResourceStream(
            $"OpenBlade.Contracts.Fixtures.{type}.json")!;
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        Assert.Equal(actual, amd ? Blade16AmdKeyboardLightingLayout.SourceFixtureSha256
            : Blade16KeyboardLightingLayout.SourceFixtureSha256);
    }
}
