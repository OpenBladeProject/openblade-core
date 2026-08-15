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
        string? presetName = null)
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

        int offset = HeaderSize;
        uint? animationColor = null;
        for (uint frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            float duration = BinaryPrimitives.ReadSingleLittleEndian(
                content.Slice(offset, sizeof(float)));
            offset += sizeof(float);
            if (!float.IsFinite(duration) || duration < 0)
            {
                throw new InvalidDataException(
                    "The .chroma file contains an invalid frame duration.");
            }

            uint frameColor = BinaryPrimitives.ReadUInt32LittleEndian(
                content.Slice(offset, sizeof(uint)));
            ValidateColor(frameColor);
            for (int pixel = 0; pixel < pixelsPerFrame; pixel++)
            {
                uint color = BinaryPrimitives.ReadUInt32LittleEndian(
                    content.Slice(offset, sizeof(uint)));
                offset += sizeof(uint);
                ValidateColor(color);
                if (color != frameColor)
                {
                    throw new InvalidDataException(
                        "The .chroma file contains per-key colors. OpenBlade can currently "
                        + "import only uniform keyboard .chroma files.");
                }
            }

            if (animationColor is { } previousColor && previousColor != frameColor)
            {
                throw new InvalidDataException(
                    "The .chroma file changes color between frames. OpenBlade can currently "
                    + "import only files that reduce exactly to Off or Static lighting.");
            }
            animationColor = frameColor;
        }

        uint importedColor = animationColor ?? 0;
        LightingSettings lighting = importedColor == 0
            ? LightingSettingsFactory.Off()
            : LightingSettingsFactory.Static(ToRgbColor(importedColor), brightness: 100);
        return new KeyboardLightingPreset(
            NormalizeName(presetName),
            "Razer Chroma",
            "Imported from a Razer Chroma version 1 keyboard animation.",
            Revision: 1,
            lighting);
    }

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
}
