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

    [Fact]
    public void FormatOwnedBuildIdentity_DistinguishesBundleRegistrationsOfTheSameBuild()
    {
        const string firstOwnerId = "01234567-89ab-cdef-0123-456789abcdef";
        const string secondOwnerId = "fedcba98-7654-3210-fedc-ba9876543210";
        string first = WindowsBundleOwnershipRegistration.FormatOwnedBuildIdentity(
            "0.8.5",
            firstOwnerId);
        string second = WindowsBundleOwnershipRegistration.FormatOwnedBuildIdentity(
            "0.8.5",
            secondOwnerId);

        Assert.Equal("0.8.5|bundle:01234567-89ab-cdef-0123-456789abcdef", first);
        Assert.True(WindowsBundleOwnershipRegistration.IsOwnedByBuild(first, first));
        Assert.False(WindowsBundleOwnershipRegistration.IsOwnedByBuild(first, second));
        Assert.False(WindowsBundleOwnershipRegistration.IsOwnedByBuild("0.8.5", first));
        Assert.False(WindowsBundleOwnershipRegistration.IsOwnedByBuild(first, "0.8.5"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}")]
    public void FormatOwnedBuildIdentity_RejectsMalformedBundleOwnerId(string ownerId)
    {
        Assert.Throws<ArgumentException>(
            () => WindowsBundleOwnershipRegistration.FormatOwnedBuildIdentity("0.8.5", ownerId));
    }
}
