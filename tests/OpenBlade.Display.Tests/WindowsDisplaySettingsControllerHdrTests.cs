namespace OpenBlade.Display.Tests;

public sealed class WindowsDisplaySettingsControllerHdrTests
{
    [Fact]
    public void ResolveHdrMutationResult_AcceptsIndependentlyConfirmedTargetState()
    {
        DisplaySettingsSnapshot state = CreateSnapshot(hdrEnabled: true);

        DisplayOperationResult result =
            WindowsDisplaySettingsController.ResolveHdrMutationResult(
                requestedEnabled: true,
                "native setter returned access denied",
                state);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Same(state, result.State);
    }

    [Fact]
    public void ResolveHdrMutationResult_PreservesFailureWhenStateDidNotChange()
    {
        DisplaySettingsSnapshot state = CreateSnapshot(hdrEnabled: false);

        DisplayOperationResult result =
            WindowsDisplaySettingsController.ResolveHdrMutationResult(
                requestedEnabled: true,
                "native setter returned access denied",
                state);

        Assert.False(result.Succeeded);
        Assert.Equal("native setter returned access denied", result.Error);
        Assert.Same(state, result.State);
    }

    [Fact]
    public void ResolveHdrMutationResult_PreservesFailureWithoutUsableReadback()
    {
        DisplayOperationResult result =
            WindowsDisplaySettingsController.ResolveHdrMutationResult(
                requestedEnabled: true,
                "query failed",
                observedState: null);

        Assert.False(result.Succeeded);
        Assert.Equal("query failed", result.Error);
        Assert.Null(result.State);
    }

    private static DisplaySettingsSnapshot CreateSnapshot(bool hdrEnabled) => new(
        IsAvailable: true,
        DisplayName: "Internal display",
        Width: 2560,
        Height: 1600,
        CurrentRefreshKey: "fixed-60",
        RefreshOptions: [],
        HdrSupported: true,
        HdrEnabled: hdrEnabled,
        ColorProfilesEnabled: !hdrEnabled,
        ActiveColorProfileId: null,
        ColorProfiles: [],
        Message: null);
}
