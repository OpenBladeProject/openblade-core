using System.Buffers.Binary;
using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class Blade14LightingInputAndImportTests
{
    [Fact]
    public void ExactVerifiedModelClassifiesWithoutAdmittingAnUnknownKeyboardCollection()
    {
        Assert.Equal(RazerBladeInputDeviceKind.Rz09_0530,
            RazerBladeReactiveInputDevice.ClassifyModel("RZ09-0530"));
        Assert.Equal(RazerBladeInputDeviceKind.Unsupported,
            RazerBladeReactiveInputDevice.ClassifyPath("\\\\?\\HID#VID_1532&PID_02C5&MI_03#example"));
        Assert.Equal(RazerBladeInputDeviceKind.Unsupported,
            RazerBladeReactiveInputDevice.ClassifyModel("RZ09-0530-other"));
    }

    [Theory]
    [InlineData("VID_1532&PID_02C5&MI_00", true)]
    [InlineData("VID_1532&PID_02C5&MI_00&COL01", true)]
    [InlineData("VID_1532&PID_02C5&MI_01&COL01", true)]
    [InlineData("vid_1532&pid_02c5&mi_01&col01", true)]
    [InlineData("VID_1532&PID_02C5&MI_00&COL02", false)]
    [InlineData("VID_1532&PID_02C5&MI_01", false)]
    [InlineData("VID_1532&PID_02C5&MI_01&COL02", false)]
    [InlineData("VID_1532&PID_02C5&MI_01&COL03", false)]
    [InlineData("VID_1532&PID_02C5&MI_03&COL01", false)]
    [InlineData("VID_1532&PID_02C5&MI_01&COL010", false)]
    [InlineData("VID_1532&PID_02C5&MI_010&COL01", false)]
    [InlineData("VID_1532&PID_02C50&MI_01&COL01", false)]
    [InlineData("VID_1532&PID_02C5&MI_00&MI_01&COL01", false)]
    public void OnlyReviewedKeyboardTopLevelCollectionsClassify(string identity, bool admitted)
    {
        RazerBladeInputDeviceKind actual = RazerBladeReactiveInputDevice.ClassifyPath(
            "\\\\?\\HID#" + identity + "#example");
        Assert.Equal(admitted ? RazerBladeInputDeviceKind.Rz09_0530 : RazerBladeInputDeviceKind.Unsupported, actual);
    }

    [Theory]
    [InlineData(0x1E, 0, 50)]
    [InlineData(0x20, 0, 52)]
    [InlineData(0x4B, 2, 92)]
    [InlineData(0x48, 2, 93)]
    [InlineData(0x4D, 2, 94)]
    [InlineData(0x50, 2, 95)]
    public void RawKeyboardMakeEventsProduceOnlyCommonMappedPulses(int scan, int flags, int slot)
    {
        Assert.True(ReactiveRawKeyboardInput.TryCreatePulses(RazerBladeInputDeviceKind.Rz09_0530,
            null, (ushort)scan, (ushort)flags, out ReactiveKeyPulse primary, out ReactiveKeyPulse? secondary));
        Assert.Equal(slot, primary.MatrixSlot);
        Assert.Null(secondary);
        Assert.False(ReactiveRawKeyboardInput.TryCreatePulses(RazerBladeInputDeviceKind.Rz09_0530,
            null, (ushort)scan, (ushort)(flags | 1), out _, out _));
    }

    [Fact]
    public void ChromaProjectsGenericNamedKeysThroughTheCommonMapAndOmitsAmbiguousKeys()
    {
        var canvas = new uint[6 * 22];
        canvas[3 * 22 + 2] = 0x000000FF; // A: red.
        canvas[3 * 22 + 4] = 0x00FF0000; // D: blue.
        canvas[5 * 22 + 15] = 0x0000FF00; // Left arrow: green.
        canvas[3 * 22 + 14] = 0x00010203; // Enter is region-dependent.
        canvas[5 * 22 + 7] = 0x00010203; // Space is region-dependent.
        byte[] file = CreateChroma(canvas);
        KeyboardLightingPreset imported = RazerChromaFileAdapter.Deserialize(file,
            keyboardLayoutId: KeyboardLightingLayoutIds.Rz09_0530);
        var settings = Assert.IsType<SequenceLightingParameters>(imported.Lighting.Parameters);
        Assert.Equal(KeyboardLightingLayoutIds.Rz09_0530, settings.LayoutId);
        IReadOnlyList<RgbColor> colors = Assert.Single(KeyboardLightingSequenceCodec.Decode(
            settings.TimelineBase64, settings.LayoutId).Frames).Slots;
        Assert.Equal(new RgbColor(255, 0, 0), colors[50]);
        Assert.Equal(new RgbColor(0, 0, 255), colors[52]);
        Assert.Equal(new RgbColor(0, 255, 0), colors[92]);
        Assert.Equal(3, colors.Count(color => color != default));
        Assert.Contains("2 colored source cells", imported.ImportWarning);
        Assert.All(colors.Skip(96), color => Assert.Equal(default, color));
    }

    private static byte[] CreateChroma(uint[] colors)
    {
        byte[] bytes = new byte[10 + 4 + colors.Length * 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        bytes[4] = 1;
        bytes[5] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(10), 0.1f);
        for (int i = 0; i < colors.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14 + i * 4), colors[i]);
        }
        return bytes;
    }
}
