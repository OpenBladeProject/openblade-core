using System.Globalization;
using OpenBlade.Display;

namespace OpenBlade.Display.Tests;

public sealed class DisplayRefreshParityFixtureTests
{
    private const string FixtureFileName = "display-refresh.psv";

    [Fact]
    public void SharedFixture_MatchesManagedRefreshPolicy()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, FixtureFileName);
        string[] lines = File.ReadAllLines(fixturePath);

        Assert.Contains(lines, line => line.StartsWith("refresh|", StringComparison.Ordinal));
        foreach (string line in lines
            .Select(line => line.TrimStart('\uFEFF'))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')))
        {
            string[] fields = line.Split('|').Select(field => field.Trim()).ToArray();
            switch (fields[0])
            {
                case "fixed":
                    AssertFixed(fields);
                    break;
                case "classify":
                    AssertClassification(fields);
                    break;
                case "refresh":
                    AssertRefreshComposition(fields);
                    break;
                case "edid":
                    AssertEdidTiming(fields);
                    break;
                default:
                    throw new InvalidDataException($"Unknown refresh parity row: {line}");
            }
        }
    }

    private static void AssertFixed(string[] fields)
    {
        Assert.Equal(7, fields.Length);
        int[] actual = RefreshRateVisibilityFilter.SelectFixedRates(
            ParseInts(fields[2]), ParseInts(fields[3]), ParseInt(fields[4]), ParseInt(fields[5]));
        Assert.Equal(ParseInts(fields[6]), actual);
    }

    private static void AssertClassification(string[] fields)
    {
        Assert.Equal(7, fields.Length);
        DisplayRefreshOption[] options = ParseOptions(fields[5]);
        string? actual = RefreshRateClassifier.Classify(
            fields[2] == "1" ? WindowsDisplayNative.DisplayConfigPathBoostRefreshRate : 0,
            ParseDouble(fields[3]), ParseDouble(fields[4]), options);
        Assert.Equal(fields[6] == "-" ? null : fields[6], actual);
    }

    private static void AssertRefreshComposition(string[] fields)
    {
        Assert.Equal(16, fields.Length);
        int[] driverRates = ParseInts(fields[2]);
        int currentModeHertz = ParseInt(fields[3]);
        int virtualHertz = fields[4] == "-" ? currentModeHertz : ParseInt(fields[4]);
        int[] activeTiming = ParseInts(fields[5]);
        int width = ParseInt(fields[6]);
        int height = ParseInt(fields[7]);
        IReadOnlyList<EdidDetailedTiming> timings = ParseTimings(fields[8]);
        DisplayConfigTargetMode activeMode = TargetMode(
            activeTiming[0], width, height, activeTiming.Length == 2 ? checked((uint)activeTiming[1]) : 1);
        bool supportsVirtualMode = fields[10] == "1";
        DisplayConfigTargetMode? preferred = supportsVirtualMode
            ? ParsePreferredMode(fields[9], width, height)
            : null;
        bool boostEnabled = fields[11] == "1";
        EdidVerticalRange? advertisedRange = fields[12] == "-"
            ? null
            : ParseRange(fields[12]);

        IEnumerable<int> advertisedRates = timings
            .Where(timing => timing.Width == width && timing.Height == height)
            .Select(timing => timing.Hertz);
        if (preferred is { } preferredMode)
        {
            advertisedRates = advertisedRates.Append(
                RefreshRateClassifier.Normalize(preferredMode.TargetVideoSignalInfo.VSyncFreq.Hertz));
        }

        int[] fixedRates = RefreshRateVisibilityFilter.SelectFixedRates(
            driverRates,
            advertisedRates,
            currentModeHertz,
            RefreshRateClassifier.Normalize(activeMode.TargetVideoSignalInfo.VSyncFreq.Hertz));
        Assert.Equal(ParseInts(fields[13]), fixedRates);

        List<DisplayRefreshOption> options = fixedRates
            .Select(hertz => new DisplayRefreshOption(
                DisplayRefreshKeys.Fixed(hertz), $"{hertz} Hz", hertz, hertz, hertz, false))
            .ToList();
        if (supportsVirtualMode)
        {
            Dictionary<int, DisplayConfigTargetMode> targetModes =
                WindowsDisplaySettingsController.SelectDynamicTargetModes(
                    activeMode, preferred, timings, driverRates);
            _ = WindowsDisplaySettingsController.AddDynamicRefreshOptions(
                options,
                fixedRates,
                targetModes,
                boostEnabled ? RefreshRateClassifier.Normalize(activeMode.TargetVideoSignalInfo.VSyncFreq.Hertz) : null,
                advertisedRange,
                requestedKey: null);
        }

        AssertOptionsEqual(ParseOptions(fields[14]), options);
        string? currentKey = RefreshRateClassifier.Classify(
            boostEnabled ? WindowsDisplayNative.DisplayConfigPathBoostRefreshRate : 0,
            virtualHertz,
            activeMode.TargetVideoSignalInfo.VSyncFreq.Hertz,
            options);
        Assert.Equal(fields[15] == "-" ? null : fields[15], currentKey);
    }

    private static void AssertEdidTiming(string[] fields)
    {
        Assert.Equal(10, fields.Length);
        int width = ParseInt(fields[2]);
        int height = ParseInt(fields[3]);
        ulong pixelRate = ParseULong(fields[4]);
        uint totalWidth = checked((uint)ParseInt(fields[5]));
        uint totalHeight = checked((uint)ParseInt(fields[6]));
        byte[] edid = CreateEdidWithType7Timing(
            width, height, pixelRate, totalWidth, totalHeight, fields[7] == "1", fields[8] == "1");

        if (fields[9] == "overflow")
        {
            Assert.Throws<OverflowException>(() => EdidDetailedTimingParser.Parse(edid));
            return;
        }

        IReadOnlyList<EdidDetailedTiming> timings = EdidDetailedTimingParser.Parse(edid);
        if (fields[9] == "-")
        {
            Assert.Empty(timings);
            return;
        }

        EdidDetailedTiming actual = Assert.Single(timings);
        Assert.Equal(width, actual.Width);
        Assert.Equal(height, actual.Height);
        Assert.Equal(ParseInt(fields[9]), actual.Hertz);
        Assert.Equal(pixelRate, actual.PixelRate);
        Assert.Equal(totalWidth, actual.TotalWidth);
        Assert.Equal(totalHeight, actual.TotalHeight);
    }

    private static IReadOnlyList<EdidDetailedTiming> ParseTimings(string field)
    {
        if (field == "-")
        {
            return [];
        }

        List<EdidDetailedTiming> result = [];
        foreach (string entry in field.Split(';'))
        {
            string[] values = entry.Split(',');
            Assert.Equal(6, values.Length);
            result.Add(new EdidDetailedTiming(
                ParseInt(values[0]), ParseInt(values[1]), ParseInt(values[2]), ParseULong(values[3]),
                checked((uint)ParseInt(values[4])), checked((uint)ParseInt(values[5]))));
        }

        return result;
    }

    private static DisplayRefreshOption[] ParseOptions(string field) => field == "-"
        ? []
        : field.Split(';').Select(entry =>
        {
            string[] values = entry.Split('~');
            Assert.Equal(5, values.Length);
            int minimum = ParseInt(values[1]);
            int maximum = ParseInt(values[2]);
            int desktop = ParseInt(values[3]);
            bool dynamic = values[4] == "1";
            return new DisplayRefreshOption(values[0], values[0], minimum, maximum, desktop, dynamic);
        }).ToArray();

    private static void AssertOptionsEqual(
        IReadOnlyList<DisplayRefreshOption> expected,
        IReadOnlyList<DisplayRefreshOption> actual)
    {
        Assert.Equal(
            expected.Select(option => (option.Key, option.MinimumHertz, option.MaximumHertz,
                option.DesktopHertz, option.IsDynamic)),
            actual.Select(option => (option.Key, option.MinimumHertz, option.MaximumHertz,
                option.DesktopHertz, option.IsDynamic)));
    }

    private static DisplayConfigTargetMode? ParsePreferredMode(string field, int activeWidth, int activeHeight)
    {
        if (field == "-")
        {
            return null;
        }

        int[] values = ParseInts(field);
        Assert.InRange(values.Length, 3, 4);
        int scanline = values.Length == 4 ? values[3] : 1;
        // GetPreferredTargetMode admits only positive, progressive timings at the active size.
        return values[0] == activeWidth && values[1] == activeHeight && values[2] > 0 && scanline is 0 or 1
            ? TargetMode(values[2], values[0], values[1], checked((uint)scanline))
            : null;
    }

    private static int[] ParseInts(string field) => field == "-"
        ? []
        : field.Split(',').Select(ParseInt).ToArray();

    private static int ParseInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    private static ulong ParseULong(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);

    private static double ParseDouble(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static EdidVerticalRange ParseRange(string value)
    {
        int[] bounds = ParseInts(value);
        Assert.Equal(2, bounds.Length);
        return new EdidVerticalRange(bounds[0], bounds[1]);
    }

    private static DisplayConfigTargetMode TargetMode(int hertz, int width, int height, uint scanlineOrdering) => new()
    {
        TargetVideoSignalInfo = new DisplayConfigVideoSignalInfo
        {
            VSyncFreq = DisplayConfigRational.FromHertz(checked((uint)hertz)),
            ActiveSize = new DisplayConfig2DRegion { Width = checked((uint)width), Height = checked((uint)height) },
            TotalSize = new DisplayConfig2DRegion { Width = checked((uint)width), Height = checked((uint)height) },
            ScanLineOrdering = scanlineOrdering,
        },
    };

    private static byte[] CreateEdidWithType7Timing(
        int width,
        int height,
        ulong pixelRate,
        uint totalWidth,
        uint totalHeight,
        bool interlaced,
        bool validExtensionChecksum)
    {
        Assert.Equal(0UL, pixelRate % 1000UL);
        byte[] edid = new byte[256];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(edid, 0);
        edid[126] = 1;
        SetChecksum(edid.AsSpan(0, 128));

        Span<byte> extension = edid.AsSpan(128, 128);
        extension[0] = 0x70;
        extension[1] = 0x20;
        extension[2] = 23;
        extension[5] = 0x22;
        extension[7] = 20;
        Span<byte> timing = extension.Slice(8, 20);
        uint encodedPixelRate = checked((uint)(pixelRate / 1000UL - 1));
        timing[0] = (byte)encodedPixelRate;
        timing[1] = (byte)(encodedPixelRate >> 8);
        timing[2] = (byte)(encodedPixelRate >> 16);
        timing[3] = interlaced ? (byte)0x95 : (byte)0x85;
        WriteUInt16(timing[4..], width - 1);
        WriteUInt16(timing[6..], checked((int)totalWidth - width - 1));
        WriteUInt16(timing[8..], 7);
        WriteUInt16(timing[10..], 31);
        WriteUInt16(timing[12..], height - 1);
        WriteUInt16(timing[14..], checked((int)totalHeight - height - 1));
        WriteUInt16(timing[16..], 164);
        WriteUInt16(timing[18..], 7);
        SetChecksum(extension);
        if (!validExtensionChecksum)
        {
            extension[10] ^= 0x01;
        }

        return edid;
    }

    private static void WriteUInt16(Span<byte> destination, int value)
    {
        destination[0] = checked((byte)(value & 0xFF));
        destination[1] = checked((byte)(value >> 8));
    }

    private static void SetChecksum(Span<byte> block)
    {
        block[127] = 0;
        int sum = 0;
        foreach (byte value in block[..127])
        {
            sum += value;
        }

        block[127] = (byte)((-sum) & 0xFF);
    }
}
