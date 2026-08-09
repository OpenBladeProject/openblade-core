using System.ComponentModel;
using System.Diagnostics;

namespace OpenBlade.Tray.Tests;

public sealed class ElevatedServiceControlLauncherTests
{
    [Theory]
    [InlineData("--start-service", true, (int)ElevatedServiceControlAction.Start)]
    [InlineData("--START-SERVICE", true, (int)ElevatedServiceControlAction.Start)]
    [InlineData("--shutdown-service", true, (int)ElevatedServiceControlAction.Shutdown)]
    [InlineData("--SHUTDOWN-SERVICE", true, (int)ElevatedServiceControlAction.Shutdown)]
    [InlineData("--start-service=1", false, 0)]
    public void HelperLaunchRequiresOneExactPrivateArgument(
        string argument,
        bool expected,
        int expectedActionValue)
    {
        ElevatedServiceHelperParseResult result = ElevatedServiceControlLauncher.ParseHelperAction(
            ["OpenBlade.Tray.exe", argument],
            out ElevatedServiceControlAction action);

        Assert.Equal(
            expected
                ? ElevatedServiceHelperParseResult.Valid
                : ElevatedServiceHelperParseResult.Invalid,
            result);
        if (expected)
        {
            Assert.Equal((ElevatedServiceControlAction)expectedActionValue, action);
        }
    }

    [Fact]
    public void HelperLaunchRejectsExtraDuplicateOrConflictingArguments()
    {
        Assert.Equal(ElevatedServiceHelperParseResult.Invalid, ElevatedServiceControlLauncher.ParseHelperAction(
            ["OpenBlade.Tray.exe", "--start-service", "--startup"],
            out _));
        Assert.Equal(ElevatedServiceHelperParseResult.Invalid, ElevatedServiceControlLauncher.ParseHelperAction(
            ["OpenBlade.Tray.exe", "--start-service", "--start-service"],
            out _));
        Assert.Equal(ElevatedServiceHelperParseResult.Invalid, ElevatedServiceControlLauncher.ParseHelperAction(
            ["OpenBlade.Tray.exe", "--start-service", "--shutdown-service"],
            out _));
    }

    [Fact]
    public void OrdinaryArgumentsAreNotHelperLaunches()
    {
        foreach (string argument in new[] { "--startup", "--shutdown" })
        {
            Assert.Equal(ElevatedServiceHelperParseResult.None, ElevatedServiceControlLauncher.ParseHelperAction(
                ["OpenBlade.Tray.exe", argument],
                out _));
        }
    }

    [Theory]
    [InlineData((int)ElevatedServiceControlAction.Start, "--start-service")]
    [InlineData((int)ElevatedServiceControlAction.Shutdown, "--shutdown-service")]
    public void CreateStartInfoElevatesOnlyTheExactTrayExecutable(
        int actionValue,
        string expectedArgument)
    {
        const string executable = @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe";

        ProcessStartInfo startInfo = ElevatedServiceControlLauncher.CreateStartInfo(
            executable,
            (ElevatedServiceControlAction)actionValue);

        Assert.Equal(executable, startInfo.FileName);
        Assert.Equal(expectedArgument, startInfo.Arguments);
        Assert.Equal(@"C:\Program Files\OpenBlade", startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
    }

    [Theory]
    [InlineData((int)ElevatedServiceControlAction.Start)]
    [InlineData((int)ElevatedServiceControlAction.Shutdown)]
    public async Task RequestAsyncSuccessfulHelperReturnsCompletedOutcome(int actionValue)
    {
        ProcessStartInfo? observed = null;
        var launcher = new ElevatedServiceControlLauncher((startInfo, token) =>
        {
            token.ThrowIfCancellationRequested();
            observed = startInfo;
            return Task.FromResult(0);
        });

        ElevatedServiceControlOutcome outcome = await launcher.RequestAsync(
            @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe",
            (ElevatedServiceControlAction)actionValue,
            CancellationToken.None);

        Assert.Equal(ElevatedServiceControlOutcome.Completed, outcome);
        Assert.NotNull(observed);
        Assert.Equal("runas", observed.Verb);
    }

    [Fact]
    public async Task RequestAsyncCanceledElevationReturnsCanceledOutcome()
    {
        var launcher = new ElevatedServiceControlLauncher((_, _) =>
            throw new Win32Exception(1223));

        ElevatedServiceControlOutcome outcome = await launcher.RequestAsync(
            @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe",
            ElevatedServiceControlAction.Start,
            CancellationToken.None);

        Assert.Equal(ElevatedServiceControlOutcome.Canceled, outcome);
    }

    [Fact]
    public async Task RequestAsyncFailedHelperIsIndeterminateAndIsNotRetried()
    {
        int calls = 0;
        var launcher = new ElevatedServiceControlLauncher((_, _) =>
        {
            calls++;
            return Task.FromResult(5);
        });

        ElevatedServiceControlOutcome outcome = await launcher.RequestAsync(
            @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe",
            ElevatedServiceControlAction.Start,
            CancellationToken.None);

        Assert.Equal(ElevatedServiceControlOutcome.Indeterminate, outcome);
        Assert.Equal(1, calls);
    }
}
