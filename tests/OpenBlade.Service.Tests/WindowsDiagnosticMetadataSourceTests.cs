using System.Security;
using System.Security.Principal;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class WindowsDiagnosticMetadataSourceTests
{
    [Fact]
    public void AuthorizedUserTokenSource_ResolvesSidAccountUsernameAndProfilePath()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var source = new WindowsAuthorizedUserTokenSource(
            () => sid,
            _ => @"BLADE16\jxu",
            _ => @"C:\Users\jxu");

        IReadOnlyCollection<string> tokens = source.ReadTokens();

        Assert.Contains(sid, tokens);
        Assert.Contains(@"BLADE16\jxu", tokens);
        Assert.Contains("jxu", tokens);
        Assert.Contains(@"C:\Users\jxu", tokens);
    }

    [Fact]
    public void AuthorizedUserTokenSource_ResolutionFailuresOmitDerivedValuesWithoutFailing()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var source = new WindowsAuthorizedUserTokenSource(
            () => sid,
            _ => throw new IdentityNotMappedException("not mapped"),
            _ => throw new SecurityException("registry denied"));

        IReadOnlyCollection<string> tokens = source.ReadTokens();

        Assert.Equal([sid], tokens);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("xy")]
    public void AuthorizedUserTokenSource_PreservesShortUsernames(string username)
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var source = new WindowsAuthorizedUserTokenSource(
            () => sid,
            _ => $@"M\{username}",
            _ => $@"C:\Users\{username}");

        Assert.Contains(username, source.ReadTokens());
    }

    [Fact]
    public void MetadataSource_AutomaticallyIncludesAuthorizedUserTokens()
    {
        var source = new WindowsDiagnosticMetadataSource(
            new FixedAuthorizedUserTokenSource("installer-private-token"));

        DiagnosticHostMetadata metadata = source.Capture();

        Assert.Contains("installer-private-token", metadata.SensitiveTokens);
        Assert.Matches(
            "^0\\.1\\.0\\+[0-9a-f]{8}$",
            metadata.Versions.OpenBladeVersion);
        Assert.False(string.IsNullOrWhiteSpace(metadata.Versions.WindowsVersion));
        Assert.False(string.IsNullOrWhiteSpace(metadata.Versions.BiosVersion));
    }

    [Fact]
    public void MetadataSource_IncludesAllCapturedFirmwareVersions()
    {
        var firmware = new FirmwareVersions("3.01", "1.04", "1.5.0.0", "98.3.5c.40.3d");
        var source = new WindowsDiagnosticMetadataSource(
            new FixedAuthorizedUserTokenSource(),
            new FixedFirmwareVersionSource(firmware));

        DiagnosticVersions versions = source.Capture().Versions;

        Assert.Equal(firmware.BiosVersion, versions.BiosVersion);
        Assert.Equal(firmware.EcVersion, versions.EcVersion);
        Assert.Equal(firmware.McuVersion, versions.McuVersion);
        Assert.Equal(firmware.VbiosVersion, versions.VbiosVersion);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("xy")]
    public void MetadataSource_PreservesShortSensitiveTokens(string token)
    {
        var source = new WindowsDiagnosticMetadataSource(
            new FixedAuthorizedUserTokenSource(token));

        Assert.Contains(token, source.Capture().SensitiveTokens);
    }

    [Fact]
    public void MetadataSource_ExcludesEmptyAndWhitespaceSensitiveTokens()
    {
        var source = new WindowsDiagnosticMetadataSource(
            new FixedAuthorizedUserTokenSource(string.Empty, " ", "x"));

        IReadOnlyCollection<string> tokens = source.Capture().SensitiveTokens;

        Assert.DoesNotContain(string.Empty, tokens);
        Assert.DoesNotContain(" ", tokens);
        Assert.Contains("x", tokens);
    }

    private sealed class FixedAuthorizedUserTokenSource(params string[] tokens)
        : IAuthorizedUserTokenSource
    {
        public IReadOnlyCollection<string> ReadTokens() => tokens;
    }

    private sealed class FixedFirmwareVersionSource(FirmwareVersions versions)
        : IFirmwareVersionSource
    {
        public FirmwareVersions Capture() => versions;
    }
}
