namespace OpenBlade.Display.Tests;

public sealed class WindowsDisplayRefreshMutationTests
{
    [Theory]
    [InlineData(0u, 2u, 0xFFFFFFFFu)]
    [InlineData(WindowsDisplayNative.DisplayConfigPathSupportVirtualMode, 0x00020003u, 0xFFFF0003u)]
    public void Refresh_FixedRateLetsWindowsSelectNewTargetTiming(uint flags, uint oldIndex, uint expectedIndex)
    {
        DisplayConfigPathInfo path = new()
        {
            Flags = flags | WindowsDisplayNative.DisplayConfigPathBoostRefreshRate,
            TargetInfo = new() { ModeInfoIdx = oldIndex },
        };
        DisplayConfigTargetMode mode = TargetMode(240);
        DisplayRefreshOption requested = new("fixed-60", "60 Hz", 60, 60, 60, false);

        WindowsDisplaySettingsController.PrepareRefreshPath(ref path, ref mode, requested, null);

        Assert.Equal(60, path.TargetInfo.RefreshRate.Hertz);
        Assert.Equal(flags, path.Flags);
        Assert.Equal(expectedIndex, path.TargetInfo.ModeInfoIdx);
    }

    [Fact]
    public void Refresh_FixedRateSuppliesTheNewSignalTiming()
    {
        DisplayConfigPathInfo path = new()
        {
            Flags = WindowsDisplayNative.DisplayConfigPathSupportVirtualMode |
                WindowsDisplayNative.DisplayConfigPathBoostRefreshRate,
            TargetInfo = new() { ModeInfoIdx = 0x00020003u },
        };
        DisplayConfigTargetMode mode = TargetMode(240);
        DisplayConfigTargetMode advertised = TargetMode(60);
        advertised.TargetVideoSignalInfo.VSyncFreq = new() { Numerator = 60_000, Denominator = 1_001 };
        DisplayRefreshOption requested = new("fixed-60", "60 Hz", 60, 60, 60, false);

        WindowsDisplaySettingsController.PrepareRefreshPath(ref path, ref mode, requested, advertised);

        Assert.Equal(0x00020003u, path.TargetInfo.ModeInfoIdx);
        Assert.Equal(WindowsDisplayNative.DisplayConfigPathSupportVirtualMode, path.Flags);
        Assert.Equal(advertised.TargetVideoSignalInfo.VSyncFreq, path.TargetInfo.RefreshRate);
        Assert.Equal(advertised, mode);
    }

    [Fact]
    public void Refresh_DynamicUsesHigherTargetTimingFromFixedLowRate()
    {
        DisplayConfigPathInfo path = new()
        {
            Flags = WindowsDisplayNative.DisplayConfigPathSupportVirtualMode,
            TargetInfo = new() { ModeInfoIdx = 0x00020003u },
        };
        DisplayConfigTargetMode mode = TargetMode(60);
        DisplayConfigTargetMode preferred = TargetMode(240);
        DisplayRefreshOption requested = new("dynamic-48-240", "Dynamic (48-240 Hz)", 48, 240, 60, true);

        WindowsDisplaySettingsController.PrepareRefreshPath(ref path, ref mode, requested, preferred);

        Assert.Equal(60, path.TargetInfo.RefreshRate.Hertz);
        Assert.Equal(240, mode.TargetVideoSignalInfo.VSyncFreq.Hertz);
        Assert.Equal(0x00020003u, path.TargetInfo.ModeInfoIdx);
        Assert.NotEqual(0u, path.Flags & WindowsDisplayNative.DisplayConfigPathBoostRefreshRate);
    }

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

    private static DisplayConfigTargetMode TargetMode(uint hertz) => new()
    {
        TargetVideoSignalInfo = new() { VSyncFreq = DisplayConfigRational.FromHertz(hertz) },
    };
}
