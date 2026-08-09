using System.ComponentModel;
using System.Diagnostics;

namespace OpenBlade.Tray.Tests;

public sealed class ElevatedServiceShutdownLauncherTests
{
    [Theory]
    [InlineData("--shutdown-service", true)]
    [InlineData("--SHUTDOWN-SERVICE", true)]
    [InlineData("--shutdown-service=1", false)]
    [InlineData("--shutdown", false)]
    [InlineData("--startup", false)]
    public void HelperLaunchRequiresExactPrivateArgument(string argument, bool expected)
    {
        Assert.Equal(
            expected,
            ElevatedServiceShutdownLauncher.IsHelperLaunch(
                ["OpenBlade.Tray.exe", argument]));
    }

    [Fact]
    public void CreateStartInfoElevatesOnlyTheExactTrayExecutable()
    {
        const string executable = @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe";

        ProcessStartInfo startInfo = ElevatedServiceShutdownLauncher.CreateStartInfo(executable);

        Assert.Equal(executable, startInfo.FileName);
        Assert.Equal(ElevatedServiceShutdownLauncher.HelperArgument, startInfo.Arguments);
        Assert.Equal(@"C:\Program Files\OpenBlade", startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
    }

    [Fact]
    public async Task RequestAsyncSuccessfulHelperReturnsCompletedOutcome()
    {
        ProcessStartInfo? observed = null;
        var launcher = new ElevatedServiceShutdownLauncher((startInfo, token) =>
        {
            token.ThrowIfCancellationRequested();
            observed = startInfo;
            return Task.FromResult(0);
        });

        ElevatedServiceShutdownOutcome outcome = await launcher.RequestAsync(
            @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe",
            CancellationToken.None);

        Assert.Equal(ElevatedServiceShutdownOutcome.Completed, outcome);
        Assert.NotNull(observed);
        Assert.Equal("runas", observed.Verb);
    }

    [Fact]
    public async Task RequestAsyncCanceledElevationReturnsCanceledOutcome()
    {
        var launcher = new ElevatedServiceShutdownLauncher((_, _) =>
            throw new Win32Exception(1223));

        ElevatedServiceShutdownOutcome outcome = await launcher.RequestAsync(
            @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe",
            CancellationToken.None);

        Assert.Equal(ElevatedServiceShutdownOutcome.Canceled, outcome);
    }

    [Fact]
    public async Task RequestAsyncFailedHelperIsIndeterminateAndIsNotRetried()
    {
        int calls = 0;
        var launcher = new ElevatedServiceShutdownLauncher((_, _) =>
        {
            calls++;
            return Task.FromResult(5);
        });

        ElevatedServiceShutdownOutcome outcome = await launcher.RequestAsync(
            @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe",
            CancellationToken.None);

        Assert.Equal(ElevatedServiceShutdownOutcome.Indeterminate, outcome);
        Assert.Equal(1, calls);
    }
}
