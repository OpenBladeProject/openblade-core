using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class BundleOwnershipRegistrationTests
{
    [Theory]
    [InlineData(null, "0.0.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3.4", "1.2.3")]
    public void FormatInstalledVersion_ProducesBurnComparableThreePartVersion(
        string? input,
        string expected)
    {
        Version? version = input is null ? null : Version.Parse(input);

        Assert.Equal(
            expected,
            WindowsBundleOwnershipRegistration.FormatInstalledVersion(version));
    }
}
