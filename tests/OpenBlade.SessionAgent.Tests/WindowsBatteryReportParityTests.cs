using System.Globalization;
using System.Text;
using OpenBlade.Session;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsBatteryReportParityTests
{
    [Fact]
    public void SharedTimestampCasesMatchManagedParser()
    {
        foreach (string line in File.ReadLines(Fixture("datetime-cases.tsv")))
        {
            string[] fields = line.Split('\t');
            byte[] xml = Encoding.UTF8.GetBytes(
                $"<BatteryReport><ReportInformation><ScanTime>{fields[0]}</ScanTime></ReportInformation></BatteryReport>");
            bool parsed = WindowsBatteryReportParser.TryParse(xml, out WindowsBatteryReport? report);
            if (fields[1] == "invalid")
            {
                Assert.False(parsed, fields[0]);
                continue;
            }

            Assert.True(parsed, fields[0]);
            Assert.Equal(DateTimeOffset.Parse(fields[1], CultureInfo.InvariantCulture), report!.GeneratedAtUtc);
        }
    }

    [Fact]
    public void EqualClippedStartsKeepReportOrderAcrossLargeSort()
    {
        Assert.True(WindowsBatteryReportParser.TryParse(
            File.ReadAllBytes(Fixture("equal-start.xml")), out WindowsBatteryReport? report));
        Assert.Collection(report!.Intervals,
            first =>
            {
                Assert.Equal(report.WindowStartUtc, first.StartUtc);
                Assert.Equal(report.WindowStartUtc.AddHours(1), first.EndUtc);
                Assert.Equal(1, first.StartChargeMilliwattHours);
            },
            second =>
            {
                Assert.Equal(report.WindowStartUtc.AddHours(1), second.StartUtc);
                Assert.Equal(report.WindowStartUtc.AddHours(2), second.EndUtc);
                Assert.Equal(2, second.StartChargeMilliwattHours);
            });
    }

    private static string Fixture(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
