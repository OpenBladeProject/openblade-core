using System.Buffers.Binary;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class KeyboardLightingSequenceCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTripsCanonicalDeltaTimeline()
    {
        RgbColor[] first = CreateBlackSlots();
        first[1] = new RgbColor(1, 2, 3);
        RgbColor[] second = (RgbColor[])first.Clone();
        second[1] = new RgbColor(4, 5, 6);
        second[2] = new RgbColor(7, 8, 9);
        var sequence = new KeyboardLightingSequence(
        [
            new KeyboardLightingSequenceFrame(25, first),
            new KeyboardLightingSequenceFrame(50, second),
        ]);

        string encoded = KeyboardLightingSequenceCodec.Encode(sequence.Frames);
        KeyboardLightingSequence decoded = KeyboardLightingSequenceCodec.Decode(encoded);

        Assert.True(sequence.IsWellFormed());
        Assert.True(KeyboardLightingSequenceCodec.IsWellFormed(encoded));
        Assert.Equal(encoded, KeyboardLightingSequenceCodec.Encode(decoded.Frames));
        Assert.Collection(
            decoded.Frames,
            frame =>
            {
                Assert.Equal(25, frame.DurationMilliseconds);
                Assert.Equal(first, frame.Slots);
            },
            frame =>
            {
                Assert.Equal(50, frame.DurationMilliseconds);
                Assert.Equal(second, frame.Slots);
            });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60_001)]
    public void Encode_RejectsFrameDurationOutsideBounds(int durationMilliseconds)
    {
        var frame = new KeyboardLightingSequenceFrame(
            durationMilliseconds,
            CreateBlackSlots());

        Assert.Throws<InvalidDataException>(() =>
            KeyboardLightingSequenceCodec.Encode([frame]));
    }

    [Fact]
    public void Encode_RejectsMoreThanTenMinutes()
    {
        KeyboardLightingSequenceFrame[] frames = Enumerable.Range(0, 11)
            .Select(_ => new KeyboardLightingSequenceFrame(60_000, CreateBlackSlots()))
            .ToArray();

        Assert.Throws<InvalidDataException>(() =>
            KeyboardLightingSequenceCodec.Encode(frames));
    }

    [Fact]
    public void Encode_RejectsMoreThanEighteenHundredFrames()
    {
        var frame = new KeyboardLightingSequenceFrame(1, CreateBlackSlots());

        Assert.Throws<InvalidDataException>(() =>
            KeyboardLightingSequenceCodec.Encode(Enumerable.Repeat(frame, 1801)));
    }

    [Fact]
    public void Encode_RejectsWrongSlotCountAndColoredLayoutHole()
    {
        var shortFrame = new KeyboardLightingSequenceFrame(1, new RgbColor[101]);
        RgbColor[] coloredHole = CreateBlackSlots();
        coloredHole[0] = new RgbColor(1, 2, 3);

        Assert.Throws<InvalidDataException>(() =>
            KeyboardLightingSequenceCodec.Encode([shortFrame]));
        Assert.Throws<InvalidDataException>(() =>
            KeyboardLightingSequenceCodec.Encode(
                [new KeyboardLightingSequenceFrame(1, coloredHole)]));
    }

    [Fact]
    public void Encode_EnforcesDecodedBinaryLimit()
    {
        KeyboardLightingSequenceFrame[] maximum = CreateAlternatingFrames(584);
        KeyboardLightingSequenceFrame[] oversized = CreateAlternatingFrames(585);

        string encoded = KeyboardLightingSequenceCodec.Encode(maximum);

        Assert.Equal(4095, Convert.FromBase64String(encoded).Length);
        Assert.Throws<InvalidDataException>(() =>
            KeyboardLightingSequenceCodec.Encode(oversized));
    }

    [Theory]
    [MemberData(nameof(MalformedTimelines))]
    public void Decode_RejectsMalformedOrNoncanonicalTimeline(string timelineBase64)
    {
        Assert.False(KeyboardLightingSequenceCodec.IsWellFormed(timelineBase64));
        Assert.Throws<InvalidDataException>(() =>
            KeyboardLightingSequenceCodec.Decode(timelineBase64));
    }

    public static TheoryData<string> MalformedTimelines()
    {
        TheoryData<string> data =
        [
            string.Empty,
            "not base64",
            Convert.ToBase64String(new byte[4097]),
            EncodeRaw(0, []),
            EncodeRaw(1, [0, 0, 0]),
            EncodeRaw(1, [1, 0, 1, 0, 1, 2, 3]),
            EncodeRaw(1, [1, 0, 2, 2, 1, 2, 3, 1, 4, 5, 6]),
            EncodeRaw(1, [1, 0, 2, 1, 1, 2, 3, 1, 4, 5, 6]),
            EncodeRaw(1, [1, 0, 1, 1, 0, 0, 0]),
            EncodeRaw(1, [1, 0, 0, 0]),
        ];
        string canonical = EncodeRaw(1, [1, 0, 0]);
        data.Add($" {canonical}");
        return data;
    }

    private static RgbColor[] CreateBlackSlots() =>
        new RgbColor[AmbientLightingFrame.SlotCount];

    private static KeyboardLightingSequenceFrame[] CreateAlternatingFrames(int count)
    {
        var frames = new KeyboardLightingSequenceFrame[count];
        for (int index = 0; index < count; index++)
        {
            RgbColor[] slots = CreateBlackSlots();
            slots[1] = index % 2 == 0
                ? new RgbColor(1, 2, 3)
                : new RgbColor(4, 5, 6);
            frames[index] = new KeyboardLightingSequenceFrame(1, slots);
        }

        return frames;
    }

    private static string EncodeRaw(int frameCount, byte[] payload)
    {
        byte[] encoded = new byte[7 + payload.Length];
        encoded[0] = (byte)'O';
        encoded[1] = (byte)'B';
        encoded[2] = (byte)'S';
        encoded[3] = (byte)'Q';
        encoded[4] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(
            encoded.AsSpan(5),
            checked((ushort)frameCount));
        payload.CopyTo(encoded, 7);
        return Convert.ToBase64String(encoded);
    }
}
