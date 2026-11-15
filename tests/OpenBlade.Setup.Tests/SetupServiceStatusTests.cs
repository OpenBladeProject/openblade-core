namespace OpenBlade.Setup.Tests;

public sealed class SetupServiceStatusTests
{
    [Theory]
    [InlineData("STATE : 4 RUNNING\r\nPID : 123\r\n", SetupServiceState.Running, 123)]
    [InlineData("STATE : 1 STOPPED\r\nPID : 0\r\n", SetupServiceState.Stopped, null)]
    [InlineData(" STATE : 4 ARRET_EN_COURS\n PID : 2147483647\n", SetupServiceState.Running, int.MaxValue)]
    public async Task QueryEx_UsesNumericStateAndProcessIdentity(string output, SetupServiceState state, int? pid)
    {
        ScSetupServiceControl control = Control(0, output);
        Assert.Equal(new SetupServiceStatus(state, pid), await control.GetStatusAsync("OpenBlade", CancellationToken.None));
    }

    [Theory]
    [InlineData("STATE : 4 RUNNING\nPID : 0")]
    [InlineData("STATE : 4 RUNNING\nPID : -1")]
    [InlineData("STATE : 4 RUNNING\nPID : 2147483648")]
    [InlineData("STATE : 4 RUNNING\nPID : 123junk")]
    [InlineData("STATE : 4 RUNNING\nPID : 123 extra")]
    [InlineData("STATE : 4 RUNNING\nPID : 123\nPID : 124")]
    [InlineData("STATE : 4 RUNNING\nSTATE : 1 STOPPED\nPID : 123")]
    [InlineData("STATE : 4 RUNNING")]
    [InlineData("PID : 123")]
    [InlineData("STATE : 4RUNNING\nPID : 123")]
    [InlineData("STATE : 1 STOPPED\nPID : 123")]
    [InlineData("STATE : 2 START_PENDING\nPID : 123")]
    [InlineData("STATE : 3 STOP_PENDING\nPID : 123")]
    public async Task QueryEx_AmbiguousIdentityFailsClosed(string output)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control(0, output).GetStatusAsync("OpenBlade", CancellationToken.None));
    }

    [Theory]
    [InlineData(1060)]
    [InlineData(5)]
    public async Task QueryEx_AbsentServiceIsDistinctFromFailure(int exitCode)
    {
        ScSetupServiceControl control = Control(exitCode, "");
        if (exitCode == 1060) { Assert.Null(await control.GetStatusAsync("OpenBlade", CancellationToken.None)); }
        else { await Assert.ThrowsAsync<InvalidOperationException>(() => control.GetStatusAsync("OpenBlade", CancellationToken.None)); }
    }

    private static ScSetupServiceControl Control(int exitCode, string output) => new((arguments, _) =>
    {
        Assert.Equal(["queryex", "OpenBlade"], arguments);
        return Task.FromResult(new ScProcessResult(exitCode, output, ""));
    });
}
