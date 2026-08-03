using OpenBlade.Display;

namespace OpenBlade.Capture.Tests;

public sealed class DisplayHdrValidationCommandTests
{
    [Fact]
    public async Task RunAsync_EnablesReadsBackAndRestoresHdr()
    {
        var controller = new FakeDisplayController(CreateSnapshot(hdrEnabled: false));
        var output = new StringWriter();

        int exitCode = await DisplayHdrValidationCommand.RunAsync(
            controller,
            output,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal([true, false], controller.HdrWrites);
        Assert.False(controller.Current.HdrEnabled);
        Assert.Contains("HdrOnColorProfilesEnabled=False", output.ToString());
        Assert.Contains("Restored=True", output.ToString());
        Assert.Contains("Success=True", output.ToString());
    }

    [Fact]
    public async Task RunAsync_RestorationFailureFailsClosed()
    {
        var controller = new FakeDisplayController(
            CreateSnapshot(hdrEnabled: false),
            failDisable: true);
        var output = new StringWriter();

        int exitCode = await DisplayHdrValidationCommand.RunAsync(
            controller,
            output,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal([true, false], controller.HdrWrites);
        Assert.True(controller.Current.HdrEnabled);
        Assert.Contains("Restored=False", output.ToString());
        Assert.Contains("Success=False", output.ToString());
    }

    [Fact]
    public async Task RunAsync_UnsupportedHdrDoesNotWrite()
    {
        var controller = new FakeDisplayController(
            CreateSnapshot(hdrEnabled: false) with { HdrSupported = false });
        var output = new StringWriter();

        int exitCode = await DisplayHdrValidationCommand.RunAsync(
            controller,
            output,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Empty(controller.HdrWrites);
        Assert.Contains("HDR is not supported", output.ToString());
        Assert.Contains("Success=False", output.ToString());
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
        ActiveColorProfileId: hdrEnabled ? null : "native.icm",
        ColorProfiles: [],
        Message: null);

    private sealed class FakeDisplayController : IDisplaySettingsController
    {
        private readonly string? initialActiveColorProfileId;
        private readonly bool failDisable;

        public FakeDisplayController(
            DisplaySettingsSnapshot initial,
            bool failDisable = false)
        {
            Current = initial;
            initialActiveColorProfileId = initial.ActiveColorProfileId;
            this.failDisable = failDisable;
        }

        public DisplaySettingsSnapshot Current { get; private set; }

        public List<bool> HdrWrites { get; } = [];

        public Task<DisplaySettingsSnapshot> QueryAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<DisplayOperationResult> SetHdrAsync(
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            HdrWrites.Add(enabled);
            if (!enabled && failDisable)
            {
                return Task.FromResult(
                    DisplayOperationResult.Failure("restore rejected", Current));
            }

            Current = Current with
            {
                HdrEnabled = enabled,
                ColorProfilesEnabled = !enabled,
                ActiveColorProfileId = enabled ? null : initialActiveColorProfileId,
            };
            return Task.FromResult(DisplayOperationResult.Success(Current));
        }

        public Task<DisplayOperationResult> SetRefreshAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DisplayOperationResult> SetColorProfileAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
