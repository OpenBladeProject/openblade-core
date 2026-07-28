using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupCommandParserTests
{
    [Theory]
    [InlineData(SetupCommand.Install, "install")]
    [InlineData(SetupCommand.Uninstall, "UNINSTALL")]
    [InlineData(SetupCommand.Uninstall, "uninstall", "--INTERACTIVE")]
    [InlineData(SetupCommand.UninstallAndPurge, "uninstall", "--PURGE")]
    public void TryParse_RecognizesOnlySupportedCommands(SetupCommand expected, params string[] arguments)
    {
        bool parsed = SetupCommandParser.TryParse(arguments, out SetupCommand command);

        Assert.True(parsed);
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData(SetupCommand.Install, "install")]
    [InlineData(SetupCommand.Uninstall, "uninstall")]
    [InlineData(SetupCommand.UninstallAndPurge, "uninstall", "--purge")]
    public void TryParseInvocation_DefaultsPublicCommandsToSelfManaged(
        SetupCommand expected,
        params string[] arguments)
    {
        bool parsed = SetupCommandParser.TryParseInvocation(arguments, out SetupInvocation invocation);

        Assert.True(parsed);
        Assert.Equal(expected, invocation.Command);
        Assert.False(invocation.BundleManaged);
    }

    [Theory]
    [InlineData(SetupCommand.Install, "INSTALL", "--BUNDLE-MANAGED")]
    [InlineData(SetupCommand.Uninstall, "UNINSTALL", "--BUNDLE-MANAGED")]
    public void TryParseInvocation_RecognizesExactBundleManagedCommands(
        SetupCommand expected,
        params string[] arguments)
    {
        bool parsed = SetupCommandParser.TryParseInvocation(arguments, out SetupInvocation invocation);

        Assert.True(parsed);
        Assert.Equal(expected, invocation.Command);
        Assert.True(invocation.BundleManaged);
    }

    [Fact]
    public void InteractiveMarker_IsLimitedToExactUserFacingUninstallInvocation()
    {
        Assert.True(SetupCommandParser.IsInteractiveUninstall(["uninstall", "--interactive"]));
        Assert.False(SetupCommandParser.IsInteractiveUninstall(["uninstall"]));
        Assert.False(SetupCommandParser.IsInteractiveUninstall(["uninstall", "--purge"]));
    }

    [Theory]
    [InlineData()]
    [InlineData("install", "--purge")]
    [InlineData("install", "--bundle-managed", "extra")]
    [InlineData("uninstall", "--bundle-managed", "--purge")]
    [InlineData("uninstall", "--purge", "extra")]
    [InlineData("--purge", "uninstall")]
    [InlineData("remove")]
    public void TryParse_RejectsUnknownFlagsExtraArgumentsAndWrongOrdering(params string[] arguments)
    {
        Assert.False(SetupCommandParser.TryParse(arguments, out _));
    }
}
