namespace OpenBlade.Display.Tests;

public sealed class WindowsDisplayRefreshMutationTests
{
    [Fact]
    public void Refresh_RequiresIndependentConfirmationOfRequestedMode()
    {
        List<string> calls = [];
        DisplayOperationResult result = WindowsDisplaySettingsController.ExecuteRefreshMutation("fixed-120",
            () => calls.Add("submit"), () => { calls.Add("read"); return Snapshot("fixed-120"); });
        Assert.True(result.Succeeded);
        Assert.Equal(["submit", "read"], calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_PossibleSubmissionOnlyReconcilesReadOnly(bool submitThrows)
    {
        List<string> calls = [];
        DisplaySettingsSnapshot current = Snapshot("fixed-60");
        DisplayOperationResult result = WindowsDisplaySettingsController.ExecuteRefreshMutation("fixed-120",
            () =>
            {
                calls.Add("submit");
                if (submitThrows)
                {
                    throw new IOException("native failure after possible submission");
                }
            }, () => { calls.Add("read"); return current; });
        Assert.False(result.Succeeded);
        Assert.True(result.SubmissionMayHaveOccurred);
        Assert.Same(current, result.State);
        Assert.Equal(["submit", "read"], calls);
    }

    [Fact]
    public void Refresh_FailedReadbackRetainsUnknownStateWithoutAnotherWrite()
    {
        int submissions = 0;
        int reads = 0;
        DisplayOperationResult result = WindowsDisplaySettingsController.ExecuteRefreshMutation("fixed-120",
            () => submissions++, () => { reads++; throw new IOException("read failed"); });
        Assert.False(result.Succeeded);
        Assert.True(result.SubmissionMayHaveOccurred);
        Assert.Null(result.State);
        Assert.Equal(1, submissions);
        Assert.Equal(1, reads);
    }

    private static DisplaySettingsSnapshot Snapshot(string key) =>
        new(true, "Internal", 2560, 1600, key, [], true, false, true, null, [], null);
}
