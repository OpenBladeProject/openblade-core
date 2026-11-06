using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class So6120SilentPwmCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAndPlanNeverInvokeLiveCode(bool explicitPlan)
    {
        bool called = false;
        using var output = new StringWriter();
        int result = await So6120SilentPwmCommand.RunAsync(explicitPlan ? ["--plan"] : [],
            new StringReader(""), output, (_, _, _) => { called = true; return Task.FromResult(9); });
        Assert.Equal(0, result);
        Assert.False(called);
        Assert.Contains("PREPARATION ONLY", output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BalancedDefaultAndPlanAreOfflineAndDescribePowerScope(bool explicitPlan)
    {
        using var output = new StringWriter();
        int result = await So6120SilentPwmCommand.RunBalancedAsync(explicitPlan ? ["--plan"] : [],
            new StringReader(""), output, (_, _, _) => throw new InvalidOperationException("Live path invoked"));
        Assert.Equal(0, result);
        Assert.Contains("Balanced + Automatic", output.ToString());
        Assert.Contains("operator-reported USB-C", output.ToString());
        Assert.Contains("Windows checks external power only", output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HalfCyclePlanIsOfflineAndStatesTheSeparateTargetRule(bool explicitPlan)
    {
        using var output = new StringWriter();
        int result = await So6120SilentPwmCommand.RunBalancedHalfCycleAsync(explicitPlan ? ["--plan"] : [],
            new StringReader(""), output, (_, _, _) => throw new InvalidOperationException("Live path invoked"));
        Assert.Equal(0, result);
        Assert.Contains(So6120SilentPwmCommand.HalfCycleName, output.ToString());
        Assert.Contains("raw 91/183 per fan, about 49.7% of the cycle count", output.ToString());
        Assert.Contains("Either fan at or above its target stops entry", output.ToString());
        Assert.Contains("a larger increase than the separate +4-count trial", output.ToString());
    }

    [Theory]
    [InlineData("--rpm", "1500")]
    [InlineData("--register", "B8")]
    [InlineData("--profile", "Silent")]
    [InlineData("--duty", "91")]
    [InlineData("--run-root", "relative")]
    [InlineData("--yes", "true")]
    [InlineData("--run-root", @"\\server\share\attempt")]
    [InlineData("--run-root", @"C:\safe\nested\..\attempt")]
    [InlineData("--run-root", @"C:\safe\attempt:alternate")]
    [InlineData("--run-root", @"C:\safe\attempt.")]
    public async Task UnsupportedInputCannotEnterLivePath(string option, string value)
    {
        bool called = false;
        Assert.Equal(1, await So6120SilentPwmCommand.RunAsync([option, value], new StringReader(""),
            new StringWriter(), (_, _, _) => { called = true; return Task.FromResult(9); }));
        Assert.Equal(1, await So6120SilentPwmCommand.RunBalancedAsync([option, value], new StringReader(""),
            new StringWriter(), (_, _, _) => { called = true; return Task.FromResult(9); }));
        Assert.Equal(1, await So6120SilentPwmCommand.RunBalancedHalfCycleAsync([option, value], new StringReader(""),
            new StringWriter(), (_, _, _) => { called = true; return Task.FromResult(9); }));
        Assert.False(called);
    }

    [Theory]
    [InlineData("YES", false)]
    [InlineData("APPLY run digest ", false)]
    [InlineData("apply run digest", false)]
    [InlineData("APPLY run digest", true)]
    public async Task ApprovalRequiresExactDisplayedScopeToken(string response, bool accepted)
    {
        Assert.Equal(accepted, await So6120SilentPwmCommand.ConfirmAsync(new StringReader(response),
            new StringWriter(), "APPLY run digest", CancellationToken.None));
    }

    [Fact]
    public async Task BlockingConsoleReaderCannotOutliveApprovalDeadline()
    {
        using var release = new ManualResetEventSlim();
        var reader = new BlockingReader(release);
        try
        {
            Assert.False(await So6120SilentPwmCommand.ConfirmAsync(reader, new StringWriter(), "YES",
                CancellationToken.None, TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task CancellationCannotBeOverriddenByMatchingInput()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.False(await So6120SilentPwmCommand.ConfirmAsync(new StringReader("YES"), new StringWriter(), "YES", cancel.Token));
    }

    private sealed class BlockingReader(ManualResetEventSlim release) : TextReader
    { public override string ReadLine() { release.Wait(); return "YES"; } }
}
