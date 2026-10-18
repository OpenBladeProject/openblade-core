using System.ComponentModel;

namespace OpenBlade.Tray.Tests;

public sealed class RazerDriverInstallLauncherTests
{
    private static string InstalledPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenBlade", "OpenBlade.Tray.exe");

    [Fact]
    public void PrivateHelperRejectsPayloadsExtraFlagsAndNearMatches()
    {
        Assert.Equal(RazerDriverHelperParseResult.Valid,
            RazerDriverInstallLauncher.Parse(["app", RazerDriverInstallLauncher.Argument]));
        Assert.Equal(RazerDriverHelperParseResult.None, RazerDriverInstallLauncher.Parse(["app", "--startup"]));
        foreach (string[] arguments in new[]
        {
            new[] { "app", RazerDriverInstallLauncher.Argument, @"C:\untrusted.zip" },
            new[] { "app", RazerDriverInstallLauncher.Argument + "=url" },
            new[] { "app", RazerDriverInstallLauncher.Argument, "--shutdown-service" },
        })
        {
            Assert.Equal(RazerDriverHelperParseResult.Invalid, RazerDriverInstallLauncher.Parse(arguments));
        }
    }

    [Fact]
    public void ElevatesOnlyInstalledSelfWithOneFixedAction()
    {
        var start = RazerDriverInstallLauncher.CreateStartInfo(InstalledPath);
        Assert.Equal(InstalledPath, start.FileName);
        Assert.Equal(RazerDriverInstallLauncher.Argument, start.Arguments);
        Assert.True(start.UseShellExecute);
        Assert.Equal("runas", start.Verb);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Throws<InvalidOperationException>(() => RazerDriverInstallLauncher.CreateStartInfo(
            Path.Combine(Path.GetTempPath(), "OpenBlade.Tray.exe")));
    }

    [Fact]
    public async Task UacCancellationDoesNotReportAnInstalledDriver()
    {
        var launcher = new RazerDriverInstallLauncher((_, _) => throw new Win32Exception(1223));
        Assert.Equal(RazerDriverInstallOutcome.Canceled, await launcher.RequestAsync(InstalledPath, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBeforeLaunchDoesNotStartAHelper()
    {
        int starts = 0;
        var launcher = new RazerDriverInstallLauncher((_, _) => { starts++; return Task.FromResult(0); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(RazerDriverInstallOutcome.Canceled, await launcher.RequestAsync(InstalledPath, cancellation.Token));
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData(0, RazerDriverInstallOutcome.Installed)]
    [InlineData(3010, RazerDriverInstallOutcome.RestartRequired)]
    [InlineData(4, RazerDriverInstallOutcome.RecoveryRequired)]
    [InlineData(999, RazerDriverInstallOutcome.RecoveryRequired)]
    public async Task UsesOnlyKnownHelperResults(int code, RazerDriverInstallOutcome expected)
    {
        var launcher = new RazerDriverInstallLauncher((_, _) => Task.FromResult(code));
        Assert.Equal(expected, await launcher.RequestAsync(InstalledPath, CancellationToken.None));
    }

    [Fact]
    public async Task InterruptedExitObservationRemainsIndeterminate()
    {
        var launcher = new RazerDriverInstallLauncher((_, _) => throw new OperationCanceledException());
        Assert.Equal(RazerDriverInstallOutcome.RecoveryRequired,
            await launcher.RequestAsync(InstalledPath, CancellationToken.None));
    }
}
