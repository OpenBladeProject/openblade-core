using System.Reflection;
using System.Reflection.Emit;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class OpenBladeProductVersionTests
{
    [Fact]
    public void Read_TruncatesLongHexMetadataAndNormalizesItsCase()
    {
        Assembly assembly = CreateAssembly(
            "1.4.2+ABCDEF1234567890ABCDEF1234567890ABCDEF12");

        Assert.Equal("1.4.2+abcdef12", OpenBladeProductVersion.Read(assembly));
    }

    [Theory]
    [InlineData(" 1.4.2-preview.3 ", "1.4.2-preview.3")]
    [InlineData("1.4.2+abcdef12", "1.4.2+abcdef12")]
    [InlineData("1.4.2+not-a-full-git-hash", "1.4.2+not-a-full-git-hash")]
    public void Read_PreservesInformationalVersionsThatAreNotFullGitMetadata(
        string informationalVersion,
        string expected)
    {
        Assembly assembly = CreateAssembly(informationalVersion);

        Assert.Equal(expected, OpenBladeProductVersion.Read(assembly));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_UsesThreePartAssemblyVersionWhenInformationalVersionIsMissing(
        string? informationalVersion)
    {
        Assembly assembly = CreateAssembly(
            informationalVersion,
            new Version(2, 3, 4, 5));

        Assert.Equal("2.3.4", OpenBladeProductVersion.Read(assembly));
    }

    [Fact]
    public void Read_RejectsMissingAssembly()
    {
        Assert.Throws<ArgumentNullException>(() => OpenBladeProductVersion.Read(null!));
    }

    private static Assembly CreateAssembly(
        string? informationalVersion,
        Version? version = null)
    {
        var assemblyName = new AssemblyName($"OpenBlade.ProductVersion.Tests.{Guid.NewGuid():N}")
        {
            Version = version,
        };
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Run);
        if (informationalVersion is not null)
        {
            ConstructorInfo? constructor = typeof(AssemblyInformationalVersionAttribute)
                .GetConstructor([typeof(string)]);
            Assert.NotNull(constructor);
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                constructor,
                [informationalVersion]));
        }

        return assembly;
    }
}
