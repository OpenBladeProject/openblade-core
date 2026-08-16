using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupCommandParserTests
{
    [Theory]
    [InlineData(SetupCommand.Install, "install")]
    [InlineData(SetupCommand.Uninstall, "UNINSTALL")]
    [InlineData(SetupCommand.Uninstall, "uninstall", "--INTERACTIVE")]
    [InlineData(SetupCommand.Uninstall, "uninstall", "--RECOVER-ORPHANED-BUNDLE")]
    [InlineData(SetupCommand.UninstallAndPurge, "uninstall", "--PURGE")]
    public void TryParseInvocation_RecognizesOnlySupportedCommands(
        SetupCommand expected,
        params string[] arguments)
    {
        bool parsed = SetupCommandParser.TryParseInvocation(
            arguments,
            out SetupInvocation invocation);

        Assert.True(parsed);
        Assert.Equal(expected, invocation.Command);
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
        Assert.False(invocation.RecoverOrphanedBundle);
    }

    [Theory]
    [InlineData(SetupCommand.Install, "INSTALL", "--BUNDLE-MANAGED", "--BUNDLE-OWNER-ID", "01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData(SetupCommand.Uninstall, "UNINSTALL", "--BUNDLE-MANAGED", "--BUNDLE-OWNER-ID", "01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData(
        SetupCommand.UninstallAndPurge,
        "UNINSTALL",
        "--BUNDLE-MANAGED",
        "--BUNDLE-OWNER-ID",
        "01234567-89ab-cdef-0123-456789abcdef",
        "--PURGE")]
    public void TryParseInvocation_RecognizesExactBundleManagedCommands(
        SetupCommand expected,
        params string[] arguments)
    {
        bool parsed = SetupCommandParser.TryParseInvocation(arguments, out SetupInvocation invocation);

        Assert.True(parsed);
        Assert.Equal(expected, invocation.Command);
        Assert.True(invocation.BundleManaged);
        Assert.False(invocation.RecoverOrphanedBundle);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", invocation.BundleOwnerId);
    }

    [Fact]
    public void TryParseInvocation_RecognizesExactOrphanedBundleRecoveryCommand()
    {
        bool parsed = SetupCommandParser.TryParseInvocation(
            ["UNINSTALL", "--RECOVER-ORPHANED-BUNDLE"],
            out SetupInvocation invocation);

        Assert.True(parsed);
        Assert.Equal(SetupCommand.Uninstall, invocation.Command);
        Assert.False(invocation.BundleManaged);
        Assert.True(invocation.RecoverOrphanedBundle);
    }

    [Theory]
    [InlineData(SetupCommand.Install, "install", "--bundle-managed", "--bundle-owner-id", "01234567-89ab-cdef-0123-456789abcdef", "--progress-pipe", "OpenBlade.Setup.Progress.0123456789abcdef0123456789abcdef")]
    [InlineData(SetupCommand.Uninstall, "uninstall", "--bundle-managed", "--progress-pipe", "OpenBlade.Setup.Progress.0123456789abcdef0123456789abcdef", "--bundle-owner-id", "01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData(SetupCommand.UninstallAndPurge, "uninstall", "--bundle-managed", "--purge", "--bundle-owner-id", "01234567-89ab-cdef-0123-456789abcdef", "--progress-pipe", "OpenBlade.Setup.Progress.0123456789abcdef0123456789abcdef")]
    [InlineData(SetupCommand.UninstallAndPurge, "uninstall", "--bundle-managed", "--progress-pipe", "OpenBlade.Setup.Progress.0123456789abcdef0123456789abcdef", "--bundle-owner-id", "01234567-89ab-cdef-0123-456789abcdef", "--purge")]
    public void TryParseInvocation_AllowsProgressPipeOnlyOnExactBundleManagedCommands(
        SetupCommand expected,
        params string[] arguments)
    {
        Assert.True(SetupCommandParser.TryParseInvocation(arguments, out SetupInvocation invocation));
        Assert.Equal(expected, invocation.Command);
        Assert.True(invocation.BundleManaged);
        Assert.Equal(
            "OpenBlade.Setup.Progress.0123456789abcdef0123456789abcdef",
            invocation.ProgressPipeName);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", invocation.BundleOwnerId);
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
    [InlineData("install", "--bundle-managed")]
    [InlineData("install", "--bundle-managed", "--bundle-owner-id")]
    [InlineData("install", "--bundle-managed", "--bundle-owner-id", "")]
    [InlineData("install", "--bundle-managed", "--bundle-owner-id", "not-a-guid")]
    [InlineData("install", "--bundle-managed", "--bundle-owner-id", "01234567-89ab-cdef-0123-456789abcdef", "--bundle-owner-id", "fedcba98-7654-3210-fedc-ba9876543210")]
    [InlineData("uninstall", "--purge", "--bundle-managed")]
    [InlineData("uninstall", "--bundle-managed", "--interactive")]
    [InlineData("uninstall", "--recover-orphaned-bundle", "extra")]
    [InlineData("uninstall", "--purge", "extra")]
    [InlineData("--purge", "uninstall")]
    [InlineData("install", "--progress-pipe", "OpenBlade.Setup.Progress.0123456789abcdef0123456789abcdef")]
    [InlineData("install", "--bundle-managed", "--progress-pipe")]
    [InlineData("install", "--bundle-managed", "--progress-pipe", "")]
    [InlineData("uninstall", "--bundle-managed", "--progress-pipe", "--purge")]
    [InlineData("uninstall", "--bundle-managed", "--purge", "--purge")]
    [InlineData("uninstall", "--bundle-managed", "--progress-pipe", "pipe", "--progress-pipe", "other")]
    [InlineData("remove")]
    public void TryParseInvocation_RejectsUnknownFlagsExtraArgumentsAndDuplicateOptions(
        params string[] arguments)
    {
        Assert.False(SetupCommandParser.TryParseInvocation(arguments, out _));
    }
}
