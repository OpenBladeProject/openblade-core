using System.Buffers.Binary;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public static class RazerChromaFileAdapter
{
    public const int SupportedVersion = 1;
    public const int MaximumFileSizeBytes = 5 * 1024 * 1024;
    public const int MaximumFrameCount = 1800;

    private const int HeaderSize = sizeof(int) + sizeof(byte) + sizeof(byte) + sizeof(uint);
    private const byte TwoDimensionalDeviceType = 1;
    private const byte KeyboardDevice = 0;
    private const byte KeyboardExtendedDevice = 3;

    public static KeyboardLightingPreset Deserialize(
        ReadOnlySpan<byte> content,
        string? presetName = null,
        string? keyboardLayoutId = null)
    {
        if (content.Length == 0)
        {
            throw new InvalidDataException("The .chroma file is empty.");
        }
        if (content.Length > MaximumFileSizeBytes)
        {
            throw new InvalidDataException(
                $".chroma files cannot exceed {MaximumFileSizeBytes / (1024 * 1024)} MiB.");
        }
        if (content.Length < HeaderSize)
        {
            throw InvalidDocument();
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(content);
        if (version != SupportedVersion)
        {
            throw new InvalidDataException(
                $"The .chroma file uses version {version}; only official version "
                + $"{SupportedVersion} files are supported.");
        }

        byte deviceType = content[sizeof(int)];
        if (deviceType != TwoDimensionalDeviceType)
        {
            throw new InvalidDataException(
                "Only two-dimensional keyboard .chroma files can be imported.");
        }

        byte device = content[sizeof(int) + sizeof(byte)];
        (int rows, int columns) = device switch
        {
            KeyboardDevice => (6, 22),
            KeyboardExtendedDevice => (8, 24),
            _ => throw new InvalidDataException(
                "Only Keyboard and KeyboardExtended .chroma files can be imported."),
        };

        uint frameCount = BinaryPrimitives.ReadUInt32LittleEndian(
            content.Slice(sizeof(int) + sizeof(byte) + sizeof(byte), sizeof(uint)));
        if (frameCount is 0 or > MaximumFrameCount)
        {
            throw new InvalidDataException(
                $"The .chroma file must contain 1 to {MaximumFrameCount} frames.");
        }

        int pixelsPerFrame = checked(rows * columns);
        int frameSize = checked(sizeof(float) + (pixelsPerFrame * sizeof(uint)));
        long expectedSize = HeaderSize + ((long)frameCount * frameSize);
        if (content.Length != expectedSize)
        {
            throw InvalidDocument();
        }

        var sourceFrames = new List<SourceFrame>(checked((int)frameCount));
        int offset = HeaderSize;
        uint? uniformAnimationColor = null;
        bool uniformAnimation = true;
        for (uint frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            float duration = BinaryPrimitives.ReadSingleLittleEndian(
                content.Slice(offset, sizeof(float)));
            offset += sizeof(float);
            if (!float.IsFinite(duration) || duration <= 0)
            {
                throw new InvalidDataException(
                    "The .chroma file contains a non-positive or non-finite frame duration.");
            }

            double durationMillisecondsValue = Math.Round(
                duration * 1000d,
                MidpointRounding.AwayFromZero);
            if (durationMillisecondsValue
                    is < KeyboardLightingSequenceCodec.MinimumFrameDurationMilliseconds
                    or > KeyboardLightingSequenceCodec.MaximumFrameDurationMilliseconds)
            {
                throw new InvalidDataException(
                    "A .chroma frame duration is outside OpenBlade's supported range.");
            }
            int durationMilliseconds = (int)durationMillisecondsValue;

            var colors = new uint[pixelsPerFrame];
            uint frameColor = BinaryPrimitives.ReadUInt32LittleEndian(
                content.Slice(offset, sizeof(uint)));
            for (int pixel = 0; pixel < pixelsPerFrame; pixel++)
            {
                uint color = BinaryPrimitives.ReadUInt32LittleEndian(
                    content.Slice(offset, sizeof(uint)));
                offset += sizeof(uint);
                ValidateColor(color);
                colors[pixel] = color;
                uniformAnimation &= color == frameColor;
            }

            uniformAnimation &= uniformAnimationColor is null
                || uniformAnimationColor.Value == frameColor;
            uniformAnimationColor ??= frameColor;
            sourceFrames.Add(new SourceFrame(durationMilliseconds, colors));
        }

        if (uniformAnimation)
        {
            uint importedColor = uniformAnimationColor ?? 0;
            LightingSettings uniformLighting = importedColor == 0
                ? LightingSettingsFactory.Off()
                : LightingSettingsFactory.Static(ToRgbColor(importedColor), brightness: 100);
            return Preset(
                presetName,
                "Imported losslessly from a uniform Razer Chroma version 1 animation.",
                uniformLighting);
        }

        if (device == KeyboardExtendedDevice)
        {
            throw new InvalidDataException(
                "Per-key KeyboardExtended animations include desktop underglow and media zones that have no admitted Blade projection. Uniform KeyboardExtended files remain supported.");
        }

        IReadOnlyDictionary<int, byte> map = CreateLogicalKeyProjection(
            keyboardLayoutId,
            extended: false);
        var unmappedColoredCells = new HashSet<int>();
        var projected = new List<KeyboardLightingSequenceFrame>(sourceFrames.Count);
        foreach (SourceFrame source in sourceFrames)
        {
            var slots = new RgbColor[AmbientLightingFrame.SlotCount];
            for (int cell = 0; cell < source.Colors.Length; cell++)
            {
                uint color = source.Colors[cell];
                if (map.TryGetValue(cell, out byte slot))
                {
                    slots[slot] = ToRgbColor(color);
                }
                else if (color != 0)
                {
                    unmappedColoredCells.Add(cell);
                }
            }

            AddOrCoalesce(projected, source.DurationMilliseconds, slots);
        }

        string timeline;
        try
        {
            timeline = KeyboardLightingSequenceCodec.Encode(
                projected);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                "The projected animation is too complex for a bounded OpenBlade profile. "
                + "Reduce its frame count or the number of keys that change per frame.",
                exception);
        }

        LightingSettings lighting = LightingSettingsFactory.Sequence(
            keyboardLayoutId!,
            timeline,
            loop: true,
            brightness: 100);
        string sourceKind = device == KeyboardExtendedDevice
            ? "KeyboardExtended"
            : "Keyboard";
        string warning = unmappedColoredCells.Count == 0
            ? "Razer's generic canvas was projected through OpenBlade's admitted named-key map; visual parity with Synapse is not guaranteed."
            : $"Razer's generic canvas was projected through OpenBlade's admitted named-key map. {unmappedColoredCells.Count} colored source cells have no admitted laptop binding and were omitted; visual parity with Synapse is not guaranteed.";
        return Preset(
            presetName,
            $"Imported from a Razer Chroma version 1 {sourceKind} animation using an exact-model logical-key projection.",
            lighting,
            warning);
    }

    private static IReadOnlyDictionary<int, byte> CreateLogicalKeyProjection(
        string? layoutId,
        bool extended)
    {
        if (layoutId is not (
            KeyboardLightingLayoutIds.Rz09_0581 or
            KeyboardLightingLayoutIds.Rz09_0528))
        {
            throw new InvalidDataException(
                "Per-key .chroma animations require an admitted RZ09-0581 or RZ09-0528 keyboard layout.");
        }

        var map = new Dictionary<int, byte>();
        void Add(int row, int column, byte slot)
        {
            if (extended)
            {
                row++;
                column++;
            }
            map.Add((row * (extended ? 24 : 22)) + column, slot);
        }

        Add(0, 1, 1);
        for (int column = 3; column <= 14; column++)
        {
            Add(0, column, checked((byte)(column - 1)));
        }
        Add(1, 1, 18);
        for (int column = 2; column <= 11; column++)
        {
            Add(1, column, checked((byte)(column + 17)));
        }
        Add(1, 12, 29);
        Add(1, 13, 30);
        Add(1, 14, 32);
        Add(2, 1, 35);
        for (int column = 2; column <= 11; column++)
        {
            Add(2, column, checked((byte)(column + 34)));
        }
        Add(2, 12, 46);
        Add(2, 13, 47);
        Add(2, 14, 49);
        Add(2, 15, 15);
        Add(3, 1, 52);
        for (int column = 2; column <= 10; column++)
        {
            Add(3, column, checked((byte)(column + 51)));
        }
        Add(3, 11, 62);
        Add(3, 12, 63);
        Add(3, 14, 66);
        Add(4, 1, 69);
        for (int column = 3; column <= 12; column++)
        {
            Add(4, column, checked((byte)(column + 68)));
        }
        Add(4, 14, 83);
        Add(5, 1, 86);
        Add(5, 2, 88);
        Add(5, 3, 90);
        Add(5, 11, layoutId == KeyboardLightingLayoutIds.Rz09_0528
            ? (byte)91
            : (byte)94);
        Add(5, 14, 96);
        Add(5, 15, 97);
        Add(4, 16, 98);
        Add(5, 17, 99);
        Add(5, 16, 100);

        if (layoutId == KeyboardLightingLayoutIds.Rz09_0528)
        {
            Add(1, 15, 14);
            Add(1, 17, 33);
            Add(2, 17, 50);
        }

        return map;
    }

    private static void AddOrCoalesce(
        List<KeyboardLightingSequenceFrame> frames,
        int durationMilliseconds,
        RgbColor[] slots)
    {
        if (frames.Count > 0
            && frames[^1].Slots.SequenceEqual(slots)
            && frames[^1].DurationMilliseconds + durationMilliseconds
                <= KeyboardLightingSequenceCodec.MaximumFrameDurationMilliseconds)
        {
            KeyboardLightingSequenceFrame previous = frames[^1];
            frames[^1] = previous with
            {
                DurationMilliseconds = previous.DurationMilliseconds + durationMilliseconds,
            };
            return;
        }

        frames.Add(new KeyboardLightingSequenceFrame(durationMilliseconds, slots));
    }

    private static KeyboardLightingPreset Preset(
        string? presetName,
        string description,
        LightingSettings lighting,
        string? warning = null) => new(
            NormalizeName(presetName),
            "Razer Chroma",
            description,
            Revision: 1,
            lighting,
            warning);

    private static void ValidateColor(uint color)
    {
        if ((color & 0xFF000000u) != 0)
        {
            throw new InvalidDataException(
                "The .chroma file contains a color value outside COLORREF RGB encoding.");
        }
    }

    private static RgbColor ToRgbColor(uint color) => new(
        Red: (byte)(color & 0xFF),
        Green: (byte)((color >> 8) & 0xFF),
        Blue: (byte)((color >> 16) & 0xFF));

    private static string NormalizeName(string? value)
    {
        string? trimmed = value?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed)
            && trimmed.Length <= KeyboardLightingFileSerializer.MaximumNameLength
                ? trimmed
                : "Imported Chroma lighting";
    }

    private static InvalidDataException InvalidDocument() =>
        new("The selected file is not a valid Razer Chroma version 1 animation.");

    private sealed record SourceFrame(int DurationMilliseconds, uint[] Colors);
}
