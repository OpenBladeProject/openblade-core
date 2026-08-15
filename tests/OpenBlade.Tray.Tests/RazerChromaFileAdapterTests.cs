using System.Buffers.Binary;
using System.Security.Cryptography;
using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class RazerChromaFileAdapterTests
{
    private const uint GreenColorRef = 0x000CE147;

    [Fact]
    public void Deserialize_MatchesTheOfficialBlankKeyboardGoldenFile()
    {
        byte[] content = CreateAnimation(
            device: 0,
            CreateUniformFrame(6, 22, 0));

        Assert.Equal(542, content.Length);
        Assert.Equal(
            "4F663FA2E17131112C424B56F79616140716234EC0E49495E5FFE8024C07F34C",
            Convert.ToHexString(SHA256.HashData(content)));
        Assert.Equal(
            LightingSettingsFactory.Off(),
            RazerChromaFileAdapter.Deserialize(content).Lighting);
    }

    [Fact]
    public void Deserialize_ImportsUniformKeyboardColorAsStatic()
    {
        byte[] content = CreateAnimation(
            device: 0,
            CreateUniformFrame(6, 22, GreenColorRef));

        KeyboardLightingPreset preset = RazerChromaFileAdapter.Deserialize(
            content,
            "Northern Chroma");

        Assert.Equal("Northern Chroma", preset.Name);
        Assert.Equal("Razer Chroma", preset.Author);
        Assert.Equal(1, preset.Revision);
        Assert.Equal(
            LightingSettingsFactory.Static(new RgbColor(0x47, 0xE1, 0x0C), 100),
            preset.Lighting);
    }

    [Fact]
    public void Deserialize_ImportsUniformBlackAsOff()
    {
        byte[] content = CreateAnimation(
            device: 0,
            CreateUniformFrame(6, 22, 0));

        KeyboardLightingPreset preset = RazerChromaFileAdapter.Deserialize(content);

        Assert.Equal("Imported Chroma lighting", preset.Name);
        Assert.Equal(LightingSettingsFactory.Off(), preset.Lighting);
    }

    [Fact]
    public void Deserialize_AcceptsExtendedKeyboardWhenEveryFrameHasTheSameUniformColor()
    {
        uint[] first = CreateUniformFrame(8, 24, GreenColorRef);
        uint[] second = CreateUniformFrame(8, 24, GreenColorRef);
        byte[] content = CreateAnimation(device: 3, first, second);

        KeyboardLightingPreset preset = RazerChromaFileAdapter.Deserialize(content);

        Assert.Equal(LightingEffect.Static, preset.Lighting.Effect);
    }

    [Fact]
    public void Deserialize_RejectsPerKeyColorsRatherThanGuessingALaptopMapping()
    {
        uint[] frame = CreateUniformFrame(6, 22, GreenColorRef);
        frame[1] = 0;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(CreateAnimation(device: 0, frame)));

        Assert.Contains("per-key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uniform", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsChangingFramesRatherThanDiscardingAnimation()
    {
        byte[] content = CreateAnimation(
            device: 0,
            CreateUniformFrame(6, 22, GreenColorRef),
            CreateUniformFrame(6, 22, 0x00E22B8A));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(content));

        Assert.Contains("changes color", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Off or Static", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "version 0")]
    [InlineData(2, "version 2")]
    [InlineData(int.MaxValue, "version 2147483647")]
    public void Deserialize_RejectsUnknownVersions(int version, string expectedMessage)
    {
        byte[] content = CreateAnimation(
            device: 0,
            CreateUniformFrame(6, 22, GreenColorRef));
        BinaryPrimitives.WriteInt32LittleEndian(content, version);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(content));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 0, "two-dimensional")]
    [InlineData(1, 1, "Keyboard")]
    [InlineData(1, 2, "Keyboard")]
    public void Deserialize_RejectsNonKeyboardDevices(
        byte deviceType,
        byte device,
        string expectedMessage)
    {
        byte[] content = CreateAnimation(
            device,
            CreateUniformFrame(6, 22, GreenColorRef));
        content[sizeof(int)] = deviceType;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(content));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsInvalidFrameCountsBeforeReadingFrames()
    {
        byte[] content = CreateAnimation(
            device: 0,
            CreateUniformFrame(6, 22, GreenColorRef));
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(6), 0);

        InvalidDataException zero = Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(content));

        Assert.Contains("1 to 1800", zero.Message, StringComparison.Ordinal);

        BinaryPrimitives.WriteUInt32LittleEndian(
            content.AsSpan(6),
            RazerChromaFileAdapter.MaximumFrameCount + 1u);
        InvalidDataException excessive = Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(content));
        Assert.Contains("1 to 1800", excessive.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsTruncatedAndTrailingData()
    {
        byte[] content = CreateAnimation(
            device: 0,
            CreateUniformFrame(6, 22, GreenColorRef));

        Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(content.AsSpan(0, content.Length - 1)));
        Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize([.. content, 0]));
    }

    [Fact]
    public void Deserialize_RejectsNonFiniteOrNegativeDurations()
    {
        byte[] content = CreateAnimation(
            device: 0,
            CreateUniformFrame(6, 22, GreenColorRef));

        BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(10), float.NaN);
        Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(content));

        BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(10), float.PositiveInfinity);
        Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(content));

        BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(10), -1f);
        Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(content));
    }

    [Fact]
    public void Deserialize_RejectsColorValuesOutsideWin32ColorRef()
    {
        uint[] frame = CreateUniformFrame(6, 22, 0xFF0CE147);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(CreateAnimation(device: 0, frame)));

        Assert.Contains("COLORREF", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsFilesLargerThanTheAdapterLimit()
    {
        byte[] content = new byte[RazerChromaFileAdapter.MaximumFileSizeBytes + 1];

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            RazerChromaFileAdapter.Deserialize(content));

        Assert.Contains("5 MiB", exception.Message, StringComparison.Ordinal);
    }

    private static uint[] CreateUniformFrame(int rows, int columns, uint color) =>
        Enumerable.Repeat(color, rows * columns).ToArray();

    private static byte[] CreateAnimation(byte device, params uint[][] frames)
    {
        (int rows, int columns) = device == 3 ? (8, 24) : (6, 22);
        int pixelsPerFrame = rows * columns;
        Assert.All(frames, frame => Assert.Equal(pixelsPerFrame, frame.Length));

        int frameSize = sizeof(float) + (pixelsPerFrame * sizeof(uint));
        byte[] content = new byte[10 + (frames.Length * frameSize)];
        BinaryPrimitives.WriteInt32LittleEndian(
            content,
            RazerChromaFileAdapter.SupportedVersion);
        content[4] = 1;
        content[5] = device;
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(6), (uint)frames.Length);

        int offset = 10;
        foreach (uint[] frame in frames)
        {
            BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(offset), 0.1f);
            offset += sizeof(float);
            foreach (uint color in frame)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(offset), color);
                offset += sizeof(uint);
            }
        }
        return content;
    }
}
