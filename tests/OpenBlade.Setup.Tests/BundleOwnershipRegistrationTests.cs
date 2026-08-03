using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class BundleOwnershipRegistrationTests
{
    [Theory]
    [InlineData(null, "0.0.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3.0", "1.2.3")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    public void FormatInstalledVersion_ProducesBurnComparableVersion(
        string? input,
        string expected)
    {
        Version? version = input is null ? null : Version.Parse(input);

        Assert.Equal(
            expected,
            WindowsBundleOwnershipRegistration.FormatInstalledVersion(version));
    }

    [Theory]
    [InlineData("0.1.0+abcdef12", "0.1.0+abcdef12", true)]
    [InlineData(" 0.1.0+abcdef12 ", "0.1.0+abcdef12", true)]
    [InlineData("0.1.0+11111111", "0.1.0+22222222", false)]
    [InlineData(null, "0.1.0+abcdef12", false)]
    [InlineData("", "0.1.0+abcdef12", false)]
    public void IsOwnedByBuild_RequiresTheExactCommitAwareIdentity(
        string? installed,
        string current,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsBundleOwnershipRegistration.IsOwnedByBuild(installed, current));
    }
}
