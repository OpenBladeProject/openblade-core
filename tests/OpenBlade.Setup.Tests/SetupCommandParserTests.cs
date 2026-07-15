using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupCommandParserTests
{
    [Theory]
    [InlineData(SetupCommand.Install, "install")]
    [InlineData(SetupCommand.Uninstall, "UNINSTALL")]
    [InlineData(SetupCommand.UninstallAndPurge, "uninstall", "--PURGE")]
    public void TryParse_RecognizesOnlySupportedCommands(SetupCommand expected, params string[] arguments)
    {
        bool parsed = SetupCommandParser.TryParse(arguments, out SetupCommand command);

        Assert.True(parsed);
        Assert.Equal(expected, command);
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
