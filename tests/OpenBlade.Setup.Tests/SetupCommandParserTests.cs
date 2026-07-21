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
    [InlineData("uninstall", "--purge", "extra")]
    [InlineData("--purge", "uninstall")]
    [InlineData("remove")]
    public void TryParse_RejectsUnknownFlagsExtraArgumentsAndWrongOrdering(params string[] arguments)
    {
        Assert.False(SetupCommandParser.TryParse(arguments, out _));
    }
}
