using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal static class WindowsBatteryReportParser
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const int MaximumUsageEntries = 1024;
    private const long MaximumCapacityMilliwattHours = 1_000_000_000;
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromHours(24);

    internal static bool TryParse(
        ReadOnlyMemory<byte> xml,
        out WindowsBatteryReport? report)
    {
        report = null;
        if (xml.IsEmpty || xml.Length > MaximumBytes)
        {
            return false;
        }

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = MaximumBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var stream = new MemoryStream(xml.ToArray(), writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            return false;
        }

        XElement? root = document.Root;
        if (root is null || !HasName(root, "BatteryReport"))
        {
            return false;
        }

        XElement? information = Child(root, "ReportInformation");
        if (!TryReadUtcElement(information, "ScanTime", out DateTimeOffset generatedAtUtc))
        {
            return false;
        }

        DateTimeOffset earliest = generatedAtUtc - MaximumWindow;
        if (TryReadUtcElement(information, "ReportStartTime", out DateTimeOffset reportStartUtc)
            && reportStartUtc > earliest
            && reportStartUtc <= generatedAtUtc)
        {
            earliest = reportStartUtc;
        }

        WindowsBatteryStatus status = ReadStatus(root);
        XElement? recentUsage = Child(root, "RecentUsage");
        XElement[] usageEntries = recentUsage?.Elements()
            .Where(element => HasName(element, "UsageEntry"))
            .Take(MaximumUsageEntries + 1)
            .ToArray() ?? [];
        if (usageEntries.Length > MaximumUsageEntries)
        {
            return false;
        }

        List<ParsedInterval> parsed = [];
        foreach (XElement entry in usageEntries)
        {
            if (string.Equals(
                Attribute(entry, "EntryType"),
                "ReportGenerated",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!TryReadUtcAttribute(entry, "Timestamp", out DateTimeOffset startUtc)
                || !TryReadDuration(entry, out TimeSpan duration)
                || duration <= TimeSpan.Zero
                || duration > MaximumWindow)
            {
                continue;
            }

            DateTimeOffset endUtc;
            try
            {
                endUtc = startUtc + duration;
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }
            startUtc = startUtc < earliest ? earliest : startUtc;
            endUtc = endUtc > generatedAtUtc ? generatedAtUtc : endUtc;
            if (startUtc >= endUtc)
            {
                continue;
            }

            long? charge = ReadCapacityAttribute(entry, "ChargeCapacity");
            long? fullCharge = ReadCapacityAttribute(entry, "FullChargeCapacity");
            long? discharge = ReadSignedCapacityAttribute(entry, "Discharge");
            double? percent = charge is not null && fullCharge is > 0
                ? 100d * charge.Value / fullCharge.Value
                : null;
            parsed.Add(new ParsedInterval(
                startUtc,
                endUtc,
                ReadPowerSource(entry),
                ReadActivity(entry),
                charge,
                fullCharge,
                discharge,
                percent));
        }

        parsed.Sort(static (left, right) => left.StartUtc.CompareTo(right.StartUtc));
        List<WindowsBatteryReportInterval> intervals = [];
        DateTimeOffset? previousEnd = null;
        foreach (ParsedInterval item in parsed)
        {
            DateTimeOffset start = item.StartUtc;
            if (previousEnd is { } end)
            {
                if (start > end)
                {
                    intervals.Add(new WindowsBatteryReportInterval(
                        end,
                        start,
                        WindowsBatteryPowerSource.Unknown,
                        WindowsBatteryActivity.Unknown,
                        null,
                        null,
                        null,
                        null));
                }
                else if (start < end)
                {
                    start = end;
                }
            }
            if (start >= item.EndUtc)
            {
                continue;
            }
            intervals.Add(new WindowsBatteryReportInterval(
                start,
                item.EndUtc,
                item.PowerSource,
                item.Activity,
                item.StartChargeMilliwattHours,
                item.FullChargeCapacityMilliwattHours,
                item.DischargeMilliwattHours,
                item.StartChargePercent));
            previousEnd = item.EndUtc;
            if (intervals.Count > WindowsBatteryReport.MaximumIntervals)
            {
                return false;
            }
        }

        report = new WindowsBatteryReport(
            WindowsBatteryReportSource.WindowsPowerCfg,
            generatedAtUtc,
            earliest,
            generatedAtUtc,
            status,
            intervals);
        return true;
    }

    private static WindowsBatteryStatus ReadStatus(XElement root)
    {
        XElement? estimates = Child(root, "RuntimeEstimates");
        long? design = ReadCapacityElement(Child(estimates, "DesignCapacity"), "Capacity");
        long? fullCharge = ReadCapacityElement(Child(estimates, "FullChargeCapacity"), "Capacity");

        XElement[] batteries = Child(root, "Batteries")?.Elements()
            .Where(element => HasName(element, "Battery"))
            .Take(17)
            .ToArray() ?? [];
        if (batteries.Length <= 16)
        {
            design ??= SumCapacities(batteries, "DesignCapacity");
            fullCharge ??= SumCapacities(batteries, "FullChargeCapacity");
        }

        int? cycleCount = batteries.Length == 1
            ? ReadIntElement(batteries[0], "CycleCount", 0, 10_000_000)
            : null;
        double? health = design is > 0 && fullCharge is not null
            ? 100d * fullCharge.Value / design.Value
            : null;
        return new WindowsBatteryStatus(design, fullCharge, health, cycleCount);
    }

    private static long? SumCapacities(IEnumerable<XElement> batteries, string name)
    {
        long sum = 0;
        bool found = false;
        foreach (XElement battery in batteries)
        {
            long? value = ReadCapacityElement(battery, name);
            if (value is null || sum > MaximumCapacityMilliwattHours - value.Value)
            {
                return null;
            }
            sum += value.Value;
            found = true;
        }
        return found ? sum : null;
    }

    private static WindowsBatteryPowerSource ReadPowerSource(XElement entry) =>
        Attribute(entry, "Ac") switch
        {
            "0" => WindowsBatteryPowerSource.Battery,
            "1" => WindowsBatteryPowerSource.Ac,
            _ => WindowsBatteryPowerSource.Unknown,
        };

    private static WindowsBatteryActivity ReadActivity(XElement entry) =>
        Attribute(entry, "EntryType")?.Trim() switch
        {
            string value when value.Equals("Active", StringComparison.OrdinalIgnoreCase) =>
                WindowsBatteryActivity.Active,
            string value when value.Equals("ConnectedStandby", StringComparison.OrdinalIgnoreCase) =>
                WindowsBatteryActivity.ConnectedStandby,
            string value when value.Equals("Suspended", StringComparison.OrdinalIgnoreCase) =>
                WindowsBatteryActivity.Suspended,
            _ => WindowsBatteryActivity.Unknown,
        };

    private static bool TryReadDuration(XElement element, out TimeSpan duration)
    {
        duration = default;
        return ulong.TryParse(
                Attribute(element, "Duration"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong ticks)
            && ticks <= long.MaxValue
            && TryCreateDuration((long)ticks, out duration);
    }

    private static bool TryCreateDuration(long ticks, out TimeSpan duration)
    {
        try
        {
            duration = TimeSpan.FromTicks(ticks);
            return true;
        }
        catch (OverflowException)
        {
            duration = default;
            return false;
        }
    }

    private static bool TryReadUtcElement(
        XElement? parent,
        string name,
        out DateTimeOffset timestamp) =>
        TryReadUtc(Child(parent, name)?.Value, out timestamp);

    private static bool TryReadUtcAttribute(
        XElement element,
        string name,
        out DateTimeOffset timestamp) =>
        TryReadUtc(Attribute(element, name), out timestamp);

    private static bool TryReadUtc(string? value, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp))
        {
            timestamp = timestamp.ToUniversalTime();
            return true;
        }
        timestamp = default;
        return false;
    }

    private static long? ReadCapacityElement(XElement? parent, string name) =>
        ReadCapacity(Child(parent, name)?.Value);

    private static long? ReadCapacityAttribute(XElement element, string name) =>
        ReadCapacity(Attribute(element, name));

    private static long? ReadCapacity(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            && parsed is >= 0 and <= MaximumCapacityMilliwattHours
                ? parsed
                : null;

    private static long? ReadSignedCapacityAttribute(XElement element, string name) =>
        long.TryParse(
            Attribute(element, name),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out long parsed)
            && parsed is >= -MaximumCapacityMilliwattHours and <= MaximumCapacityMilliwattHours
                ? parsed
                : null;

    private static int? ReadIntElement(XElement parent, string name, int minimum, int maximum) =>
        int.TryParse(
            Child(parent, name)?.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int parsed)
            && parsed >= minimum
            && parsed <= maximum
                ? parsed
                : null;

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(element => HasName(element, name));

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))?.Value;

    private static bool HasName(XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.Ordinal);

    private sealed record ParsedInterval(
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        WindowsBatteryPowerSource PowerSource,
        WindowsBatteryActivity Activity,
        long? StartChargeMilliwattHours,
        long? FullChargeCapacityMilliwattHours,
        long? DischargeMilliwattHours,
        double? StartChargePercent);
}
