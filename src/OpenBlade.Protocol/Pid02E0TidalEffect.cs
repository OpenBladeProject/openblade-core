using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal interface IPid02E0RandomColorPairSource
{
    (MatrixRgb Primary, MatrixRgb Secondary) ColorsForCycle(long cycleIndex);
}

internal sealed class Pid02E0RandomPaletteColorPairSource : IPid02E0RandomColorPairSource
{
    private readonly byte[] seed;

    internal Pid02E0RandomPaletteColorPairSource()
        : this(RandomNumberGenerator.GetBytes(32))
    {
    }

    internal Pid02E0RandomPaletteColorPairSource(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Length < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(seed));
        }

        this.seed = (byte[])seed.Clone();
    }

    public (MatrixRgb Primary, MatrixRgb Secondary) ColorsForCycle(long cycleIndex)
    {
        if (cycleIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cycleIndex));
        }

        Span<byte> input = stackalloc byte[seed.Length + sizeof(long)];
        seed.CopyTo(input);
        BitConverter.TryWriteBytes(input[seed.Length..], cycleIndex);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);

        IReadOnlyList<MatrixRgb> palette = Pid02E0RandomPaletteColorSource.CapturedPalette;
        int first = (int)(BitConverter.ToUInt32(digest) % (uint)palette.Count);
        int second = (int)(BitConverter.ToUInt32(digest[4..]) % (uint)(palette.Count - 1));
        if (second >= first)
        {
            second++;
        }

        return (palette[first], palette[second]);
    }
}

/// <summary>
/// Capture-faithful Tidal renderer. The spatial intensity templates are
/// sanitized from one complete five-second Synapse cycle in each direction.
/// Profile colors are substituted at render time, so no user-selected RGB
/// values or raw USB traffic are embedded in the product.
/// </summary>
internal sealed class Pid02E0TidalEffect : IPid02E0LightingEffect
{
    private readonly MatrixRgb primaryColor;
    private readonly MatrixRgb secondaryColor;
    private readonly bool randomColors;
    private readonly LightingDirection direction;
    private readonly IPid02E0RandomColorPairSource? randomColorSource;

    internal Pid02E0TidalEffect(
        MatrixRgb primaryColor,
        MatrixRgb secondaryColor,
        bool randomColors,
        LightingDirection direction,
        IPid02E0RandomColorPairSource? randomColorSource = null)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        this.primaryColor = primaryColor;
        this.secondaryColor = secondaryColor;
        this.randomColors = randomColors;
        this.direction = direction;
        this.randomColorSource = randomColors
            ? randomColorSource ?? new Pid02E0RandomPaletteColorPairSource()
            : null;
    }

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0TidalOracle.FrameInterval;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        long cycleIndex = Pid02E0LightingMath.CycleIndex(
            elapsed,
            Pid02E0TidalOracle.CycleDuration);
        MatrixRgb first = primaryColor;
        MatrixRgb second = secondaryColor;
        if (randomColors)
        {
            (first, second) = randomColorSource!.ColorsForCycle(cycleIndex);
        }

        int frameIndex = (int)(
            (elapsed.Ticks % Pid02E0TidalOracle.CycleDuration.Ticks)
            / Pid02E0TidalOracle.FrameInterval.Ticks);
        return Pid02E0TidalOracle.Render(direction, frameIndex, first, second);
    }
}

internal static class Pid02E0TidalOracle
{
    internal const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_02e0_3_01_TidalOracle.json";
    internal const string ApprovedFixtureSha256 =
        "35D69EB780ED6727951E630565D2873FAECE7B47450CB4B30AFAE38AC3271C4F";
    internal const int FrameCount = 100;
    internal static readonly TimeSpan CycleDuration = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);

    private static readonly Lazy<OracleTemplates> Templates = new(ReadTemplates);

    internal static Pid02E0MatrixFrame Render(
        LightingDirection direction,
        int frameIndex,
        MatrixRgb primary,
        MatrixRgb secondary)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }
        if ((uint)frameIndex >= FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        short[] template = direction == LightingDirection.Leftward
            ? Templates.Value.Leftward
            : Templates.Value.Rightward;
        MatrixRgb[] slots = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        int offset = frameIndex * Pid02E0MatrixFrame.SlotCount;
        for (int slot = 0; slot < slots.Length; slot++)
        {
            short value = template[offset + slot];
            slots[slot] = value switch
            {
                > 0 => primary.Scale((byte)value),
                < 0 => secondary.Scale((byte)-value),
                _ => MatrixRgb.Black,
            };
        }

        return new Pid02E0MatrixFrame(slots);
    }

    private static OracleTemplates ReadTemplates()
    {
        Assembly assembly = typeof(Pid02E0TidalOracle).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The Tidal oracle fixture is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(hash, ApprovedFixtureSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Tidal oracle fixture hash is not approved.");
        }

        TidalOracleDocument? document = JsonSerializer.Deserialize<TidalOracleDocument>(
            bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            });
        if (document is null
            || document.SchemaVersion != 1
            || document.EvidenceRole != "SanitizedTidalMatrixOracle"
            || document.SourceCaptureSha256 !=
                "6BDB15583C203BA8543A3A87D578144E84C502249E0C14B3CC9F50430C51B3C2"
            || document.Target != new TidalOracleTarget("RZ09-0581", "1532", "02E0", "3.01")
            || document.CycleMilliseconds != 5000
            || document.FrameIntervalMilliseconds != 50
            || document.FrameCount != FrameCount
            || document.SlotCount != Pid02E0MatrixFrame.SlotCount
            || document.CapturedColor1RgbHex != "00FF00"
            || document.CapturedColor2RgbHex != "0000FF"
            || document.Templates.Length != 2)
        {
            throw new InvalidOperationException("The Tidal oracle fixture metadata is invalid.");
        }

        TidalOracleTemplate left = document.Templates.Single(template =>
            template.Direction == nameof(LightingDirection.Leftward));
        TidalOracleTemplate right = document.Templates.Single(template =>
            template.Direction == nameof(LightingDirection.Rightward));
        return new(Decode(left), Decode(right));
    }

    private static short[] Decode(TidalOracleTemplate template)
    {
        const int expectedBytes = FrameCount * Pid02E0MatrixFrame.SlotCount * 2;
        if (template.Encoding !=
                "deflate-base64-signed-int16-color1-positive-color2-negative"
            || template.UncompressedBytes != expectedBytes)
        {
            throw new InvalidOperationException("A Tidal oracle template is invalid.");
        }

        byte[] compressed = Convert.FromBase64String(template.Data);
        using var input = new MemoryStream(compressed, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        byte[] raw = new byte[expectedBytes];
        deflate.ReadExactly(raw);
        if (deflate.ReadByte() != -1)
        {
            throw new InvalidOperationException("A Tidal oracle template has trailing data.");
        }

        short[] values = new short[expectedBytes / 2];
        Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
        for (int frame = 0; frame < FrameCount; frame++)
        {
            int offset = frame * Pid02E0MatrixFrame.SlotCount;
            for (int slot = 0; slot < Pid02E0MatrixFrame.SlotCount; slot++)
            {
                short value = values[offset + slot];
                if (value is < -255 or > 255
                    || Pid02E0MatrixLayout.IsHole(slot) && value != 0)
                {
                    throw new InvalidOperationException("A Tidal oracle slot is invalid.");
                }
            }
        }

        return values;
    }

    private sealed record OracleTemplates(short[] Leftward, short[] Rightward);

    private sealed record TidalOracleDocument(
        int SchemaVersion,
        string EvidenceRole,
        TidalOracleTarget Target,
        string SourceCaptureSha256,
        int CycleMilliseconds,
        int FrameIntervalMilliseconds,
        int FrameCount,
        int SlotCount,
        string CapturedColor1RgbHex,
        string CapturedColor2RgbHex,
        TidalOracleTemplate[] Templates);

    private sealed record TidalOracleTarget(
        string ModelNumber,
        string VendorIdHex,
        string ProductIdHex,
        string Bios);

    private sealed record TidalOracleTemplate(
        string Direction,
        double StartSeconds,
        int FirstSourceFrame,
        int LastSourceFrame,
        string Encoding,
        int UncompressedBytes,
        string Data);
}
