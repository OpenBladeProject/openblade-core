using System.Management;
using OpenBlade.Display;

namespace OpenBlade.Display.Tests;

public sealed class WindowsDisplayBrightnessTests
{
    [Fact]
    public void SetResult_PreservesNativeStatusAndUnexpectedType()
    {
        ManagementException status = Assert.Throws<ManagementException>(
            () => WindowsDisplayBrightness.ValidateSetResult(5u));
        Assert.Contains("status 5 (0x00000005)", status.Message, StringComparison.Ordinal);

        ManagementException type = Assert.Throws<ManagementException>(
            () => WindowsDisplayBrightness.ValidateSetResult(5));
        Assert.Contains("System.Int32", type.Message, StringComparison.Ordinal);

        WindowsDisplayBrightness.ValidateSetResult(0u);
    }

    [Theory]
    [InlineData(null, InternalInstance, (byte)40, true)]
    [InlineData(null, InternalInstance, (byte)30, false)]
    [InlineData(null, null, null, false)]
    [InlineData(null, @"DISPLAY\SDC4226\4&abc&0&UID8388688_1", (byte)40, false)]
    [InlineData(false, InternalInstance, (byte)40, true)]
    [InlineData(true, InternalInstance, (byte)40, true)]
    [InlineData(false, InternalInstance, (byte)30, false)]
    [InlineData(true, null, null, false)]
    [InlineData(true, @"DISPLAY\SDC4226\4&abc&0&UID8388688_1", (byte)40, false)]
    public void MissingOrBooleanAcknowledgement_RequiresTargetReadbackFromSameInstance(
        bool? acknowledgement, string? instanceName, byte? percent, bool succeeds)
    {
        WindowsDisplayBrightness.ValidateSetResult(acknowledgement);
        DisplayBrightnessReading? observed = instanceName is not null && percent is byte value
            ? new(instanceName, value, [30, 40])
            : null;
        var snapshot = new DisplaySettingsSnapshot(true, null, 0, 0, null, [], false, false,
            false, null, [], null)
        {
            BrightnessPercent = percent,
        };

        DisplayOperationResult result = WindowsDisplaySettingsController.ConfirmBrightnessReadback(
            InternalInstance, 40, observed, snapshot);

        Assert.Equal(succeeds, result.Succeeded);
        Assert.Equal(!succeeds, result.SubmissionMayHaveOccurred);
        Assert.Same(snapshot, result.State);
    }

    private const string InternalInstance = @"display\sdc4226\4&ABC&0&uid8388688_0";

    private const string InternalPath = @"\\?\DISPLAY#SDC4226#4&abc&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    [Theory]
    [InlineData(@"DISPLAY\SDC4226\4&abc&0&UID8388688_0", true)]
    [InlineData(@"display\sdc4226\4&ABC&0&uid8388688_12", true)]
    [InlineData(@"DISPLAY\SDC4226\4&abc&0&UID8388688", true)]
    [InlineData(@"DISPLAY\SDC4226\4&abc&0&UID8388689_0", false)]
    [InlineData(@"DISPLAY\OTHER\4&abc&0&UID8388688_0", false)]
    [InlineData(@"DISPLAY\SDC4226\4&abc&0&UID8388688_suffix", false)]
    [InlineData("", false)]
    public void MatchesMonitor_BindsBrightnessToExactInternalPanel(string instance, bool expected)
    {
        Assert.Equal(expected, WindowsDisplayBrightness.MatchesMonitor(InternalPath, instance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DISPLAY")]
    [InlineData(@"\\?\USB#SDC4226#4&abc&0&UID8388688#{guid}")]
    public void MatchesMonitor_RejectsMissingOrUnrelatedTarget(string? path)
    {
        Assert.False(WindowsDisplayBrightness.MatchesMonitor(path, @"DISPLAY\SDC4226\4&abc&0&UID8388688_0"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 20)]
    [InlineData(26, 30)]
    [InlineData(100, 100)]
    public void SelectLevel_ResolvesRequestedIntentToSupportedReadbackTarget(byte requested, byte expected)
    {
        Assert.Equal(expected, WindowsDisplayBrightness.SelectLevel(requested, [100, 30, 20, 0, 20, 255]));
    }

    [Fact]
    public void SelectLevel_RejectsInvalidIntentAndMissingLevels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsDisplayBrightness.SelectLevel(101, [0, 100]));
        Assert.Throws<InvalidOperationException>(() => WindowsDisplayBrightness.SelectLevel(50, []));
        Assert.Throws<InvalidOperationException>(() => WindowsDisplayBrightness.SelectLevel(50, [255]));
    }

    [Fact]
    public async Task Brightness_UsesSerializedNativePreparationAndRejectsOutOfRangeBeforeSubmission()
    {
        bool called = false;
        using var controller = new WindowsDisplaySettingsController(
            (_, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(),
            (percent, _) =>
            {
                called = true;
                Assert.Equal(25, percent);
                return new(() => DisplayOperationResult.Failure("Unavailable", submissionMayHaveOccurred: false));
            });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetBrightnessAsync(101));
        Assert.False(called);
        DisplayOperationResult result = await controller.SetBrightnessAsync(25);
        Assert.True(called);
        Assert.False(result.Succeeded);
        Assert.False(result.SubmissionMayHaveOccurred);
    }
}
