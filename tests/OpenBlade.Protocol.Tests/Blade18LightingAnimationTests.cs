using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Blade18LightingAnimationTests
{
    private const string BundleSha256 =
        "1485C6A5DFE829D2BFA0EE53BBAB60B49C19150F62487456C04DEF1F0DAB99CD";
    private const string WheelSequenceSha256 =
        "49679EB6418FC3778D5C692CCFC79A3B4E4B9DAC74FA451E05CA0F5583F92F4A";
    private const string FireSequenceSha256 =
        "40719DCD0EAB9D796BF6D686DFE5384DD835A6A1BC8FE844224191F94E5B5673";
    private const double WheelCycleMilliseconds = 999.6615;
    private const double FireSampleMilliseconds = 1450.5177;

    [Fact]
    public void Fixture_IsPinnedToExactTargetAndRecordedOperationRanges()
    {
        byte[] bytes = ReadFixture();

        Assert.Equal(
            Blade18LightingAnimation.ApprovedSha256,
            Convert.ToHexString(SHA256.HashData(bytes)));
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        JsonElement source = root.GetProperty("source");
        JsonElement transport = root.GetProperty("transport");
        JsonElement wheel = root.GetProperty("templates").GetProperty("wheelClockwise");
        JsonElement fire = root.GetProperty("templates").GetProperty("fire");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1532", source.GetProperty("vendorIdHex").GetString());
        Assert.Equal("02A0", source.GetProperty("productIdHex").GetString());
        Assert.Equal(BundleSha256, source.GetProperty("bundleSha256").GetString());
        Assert.Equal(Blade18LightingFrame.Rows, transport.GetProperty("rows").GetInt32());
        Assert.Equal(Blade18LightingFrame.Columns, transport.GetProperty("columns").GetInt32());

        AssertTemplateSource(
            wheel,
            "Wheel",
            21,
            WheelSequenceSha256,
            "recording-1-op-4435",
            "recording-1-op-4560");
        AssertTemplateSource(
            fire,
            "Fire",
            32,
            FireSequenceSha256,
            "recording-1-op-2725",
            "recording-1-op-2916");
        Assert.Contains("non-periodic", fire.GetProperty("status").GetString());
    }

    [Fact]
    public void RecordedFirstFrames_EncodeKnownWheelAndFireRows()
    {
        Blade18LightingFrame wheel = Blade18LightingAnimation.Render(
            LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 50),
            TimeSpan.Zero);
        Blade18LightingFrame fire = Blade18LightingAnimation.Render(
            LightingSettingsFactory.Fire(50),
            TimeSpan.Zero);

        Assert.Equal(
            "FF00000F000000FF148FFF1386FF1379FF1268FF124EFF1023FF370AFC9100ECFF00E0FF008CFF004CFF0023FF0003FF0000FF2A",
            Convert.ToHexString(wheel.EncodeRow(0)));
        Assert.Equal(
            "FF00000F000000B415005D16008A10005700004100003400005500002C00001C00003700003F0000470800780E006B0000490000",
            Convert.ToHexString(fire.EncodeRow(0)));
    }

    [Fact]
    public void Wheel_RepeatsObservedCycleAndCounterclockwiseReversesPlayback()
    {
        LightingSettings clockwise = LightingSettingsFactory.Wheel(
            LightingRotationDirection.Clockwise,
            50);
        LightingSettings counterclockwise = LightingSettingsFactory.Wheel(
            LightingRotationDirection.Counterclockwise,
            50);
        TimeSpan cycle = TimeSpan.FromMilliseconds(WheelCycleMilliseconds);
        TimeSpan elapsed = TimeSpan.FromMilliseconds(123.0176);

        AssertFrameEqual(
            Blade18LightingAnimation.Render(clockwise, TimeSpan.Zero),
            Blade18LightingAnimation.Render(clockwise, cycle));
        AssertFrameEqual(
            Blade18LightingAnimation.Render(clockwise, elapsed),
            Blade18LightingAnimation.Render(clockwise, cycle + elapsed));
        AssertFrameEqual(
            Blade18LightingAnimation.Render(clockwise, cycle - elapsed),
            Blade18LightingAnimation.Render(counterclockwise, elapsed));
        Assert.False(Blade18LightingAnimation.Render(clockwise, elapsed).Slots
            .SequenceEqual(Blade18LightingAnimation.Render(counterclockwise, elapsed).Slots));
    }

    [Fact]
    public void Fire_RepeatsBoundedObservedSampleWhilePreservingTemporalVariation()
    {
        LightingSettings fire = LightingSettingsFactory.Fire(50);
        TimeSpan sample = TimeSpan.FromMilliseconds(FireSampleMilliseconds);
        Blade18LightingFrame first = Blade18LightingAnimation.Render(fire, TimeSpan.Zero);

        AssertFrameEqual(first, Blade18LightingAnimation.Render(fire, sample));
        AssertFrameEqual(
            Blade18LightingAnimation.Render(fire, TimeSpan.FromMilliseconds(400)),
            Blade18LightingAnimation.Render(fire, sample + TimeSpan.FromMilliseconds(400)));
        Assert.False(first.Slots.SequenceEqual(
            Blade18LightingAnimation.Render(fire, TimeSpan.FromMilliseconds(50)).Slots));
    }

    [Fact]
    public void Frame_CopiesSlotsAndEncodesOnlyTheRequestedRow()
    {
        RgbColor[] colors = Enumerable.Range(0, Blade18LightingFrame.SlotCount)
            .Select(index => new RgbColor(
                (byte)index,
                (byte)(255 - index),
                (byte)(index / 2)))
            .ToArray();
        var frame = new Blade18LightingFrame(colors);
        colors[Blade18LightingFrame.Columns * 2] = default;

        byte[] row = frame.EncodeRow(2);
        Assert.Equal(4 + Blade18LightingFrame.Columns * 3, row.Length);
        Assert.Equal([0xFF, 0x02, 0x00, 0x0F], row[..4]);
        Assert.Equal([0x20, 0xDF, 0x10], row[4..7]);
        Assert.Equal(new RgbColor(0x20, 0xDF, 0x10),
            frame.Slots[Blade18LightingFrame.Columns * 2]);

        row[4] = 0;
        Assert.Equal(0x20, frame.EncodeRow(2)[4]);
        IList<RgbColor> readOnly = Assert.IsAssignableFrom<IList<RgbColor>>(frame.Slots);
        Assert.Throws<NotSupportedException>(() => readOnly[0] = default);
    }

    [Fact]
    public void Frame_RejectsWrongGeometryAndOutOfRangeRows()
    {
        Assert.Throws<ArgumentNullException>(() => new Blade18LightingFrame(null!));
        Assert.Throws<ArgumentException>(() =>
            new Blade18LightingFrame(new RgbColor[Blade18LightingFrame.SlotCount - 1]));
        var frame = new Blade18LightingFrame(
            new RgbColor[Blade18LightingFrame.SlotCount]);
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.EncodeRow(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            frame.EncodeRow(Blade18LightingFrame.Rows));
    }

    [Fact]
    public void Render_RejectsUntypedOrInvalidAnimationSettingsAndNegativeTime()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Blade18LightingAnimation.Render(null!, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Blade18LightingAnimation.Render(
                LightingSettingsFactory.Fire(50),
                TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentException>(() =>
            Blade18LightingAnimation.Render(
                LightingSettingsFactory.SpectrumCycling(50),
                TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() =>
            Blade18LightingAnimation.Render(
                LightingSettingsFactory.Fire(50) with
                {
                    Parameters = new WheelLightingParameters(
                        LightingRotationDirection.Clockwise),
                },
                TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() =>
            Blade18LightingAnimation.Render(
                LightingSettingsFactory.Wheel(
                    (LightingRotationDirection)int.MaxValue,
                    50),
                TimeSpan.Zero));
    }

    private static byte[] ReadFixture()
    {
        using Stream stream = typeof(Blade18LightingAnimation).Assembly
            .GetManifestResourceStream(Blade18LightingAnimation.ResourceName)
            ?? throw new InvalidOperationException("The test fixture is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void AssertTemplateSource(
        JsonElement template,
        string effect,
        int frameCount,
        string sequenceSha256,
        string firstOperation,
        string lastOperation)
    {
        Assert.Equal(effect, template.GetProperty("effect").GetString());
        Assert.Equal(frameCount, template.GetProperty("frameCount").GetInt32());
        Assert.Equal(frameCount, template.GetProperty("frames").GetArrayLength());
        Assert.Equal(sequenceSha256,
            template.GetProperty("rgbSequenceSha256").GetString());
        JsonElement range = template.GetProperty("sourceOperationRange");
        Assert.Equal(firstOperation, range.GetProperty("firstIncluded").GetString());
        Assert.Equal(lastOperation, range.GetProperty("lastIncluded").GetString());
    }

    private static void AssertFrameEqual(
        Blade18LightingFrame expected,
        Blade18LightingFrame actual) =>
        Assert.Equal(expected.Slots.ToArray(), actual.Slots.ToArray());
}
