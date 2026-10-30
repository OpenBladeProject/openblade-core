using OpenBlade.Session;

namespace OpenBlade.Tray;

/// <summary>Completes report coverage for presentation without changing the source report.</summary>
public static class WindowsBatteryReportCoverage
{
    public static IReadOnlyList<WindowsBatteryReportInterval> CreateIntervals(WindowsBatteryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var intervals = new List<WindowsBatteryReportInterval>(report.Intervals.Count * 2 + 1);
        DateTimeOffset cursor = report.WindowStartUtc;
        foreach (WindowsBatteryReportInterval interval in report.Intervals)
        {
            AddUnknownUntil(interval.StartUtc);
            intervals.Add(interval);
            cursor = interval.EndUtc;
        }
        AddUnknownUntil(report.WindowEndUtc);
        return intervals.AsReadOnly();

        void AddUnknownUntil(DateTimeOffset end)
        {
            if (cursor < end)
            {
                intervals.Add(new WindowsBatteryReportInterval(
                    cursor, end, WindowsBatteryPowerSource.Unknown, WindowsBatteryActivity.Unknown,
                    null, null, null, null));
            }
        }
    }
}
