namespace OpenBlade.Platform.Tests;

public sealed class PawnIoRuntimeAdmissionTests
{
    [Theory]
    [InlineData(@"\SystemRoot\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\PawnIO.sys")]
    [InlineData(@"\??\C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\PawnIO.sys")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\PawnIO.sys")]
    public void OnlyCanonicalExpectedDriverStorePackagePathIsAdmitted(string path) =>
        Assert.Equal(@"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\PawnIO.sys",
            PawnIoRuntimeAdmission.NormalizeDriverPath(path, @"C:\Windows"));

    [Theory]
    [InlineData(@"C:\Users\User\PawnIO.sys")]
    [InlineData(@"C:\Windows\System32\drivers\PawnIO.sys")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\other.inf_amd64_123\PawnIO.sys")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\Other.sys")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\nested\PawnIO.sys")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\..\PawnIO.sys")]
    public void ArbitraryOrAmbiguousBindingPathsAreRejected(string path) =>
        Assert.Throws<InvalidDataException>(() => PawnIoRuntimeAdmission.NormalizeDriverPath(path, @"C:\Windows"));
}
