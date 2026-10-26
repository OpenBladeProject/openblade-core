using System.Text;
using OpenBlade.Session;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsBatteryReportParserTests
{
    [Fact]
    public void TryParse_ReadsStatusAndClipsTypedTimelineToTheAvailableDay()
    {
        byte[] xml = Xml("""
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <ReportInformation>
                <ScanTime>2026-01-02T12:00:00Z</ScanTime>
                <ReportStartTime>2026-01-01T11:00:00Z</ReportStartTime>
              </ReportInformation>
              <Batteries>
                <Battery>
                  <DesignCapacity>60000</DesignCapacity>
                  <FullChargeCapacity>50000</FullChargeCapacity>
                  <CycleCount>120</CycleCount>
                </Battery>
              </Batteries>
              <RuntimeEstimates>
                <DesignCapacity><Capacity>60000</Capacity></DesignCapacity>
                <FullChargeCapacity><Capacity>50000</Capacity></FullChargeCapacity>
              </RuntimeEstimates>
              <RecentUsage>
                <UsageEntry Timestamp="2026-01-01T11:30:00Z" Duration="72000000000"
                  Ac="0" EntryType="Active" ChargeCapacity="40000"
                  FullChargeCapacity="50000" Discharge="5000" />
                <UsageEntry Timestamp="2026-01-01T14:00:00Z" Duration="18000000000"
                  Ac="1" EntryType="ConnectedStandby" ChargeCapacity="35000"
                  FullChargeCapacity="50000" Discharge="0" />
                <UsageEntry Timestamp="2026-01-02T12:00:00Z" Duration="18446744073709551615"
                  Ac="1" EntryType="ReportGenerated" />
              </RecentUsage>
            </BatteryReport>
            """);

        bool parsed = WindowsBatteryReportParser.TryParse(xml, out WindowsBatteryReport? report);

        Assert.True(parsed);
        Assert.NotNull(report);
        Assert.Equal(WindowsBatteryReportSource.WindowsPowerCfg, report.Source);
        Assert.Equal(Utc(2026, 1, 2, 12), report.GeneratedAtUtc);
        Assert.Equal(Utc(2026, 1, 1, 12), report.WindowStartUtc);
        Assert.Equal(report.GeneratedAtUtc, report.WindowEndUtc);
        Assert.Equal(60000, report.Status.DesignCapacityMilliwattHours);
        Assert.Equal(50000, report.Status.FullChargeCapacityMilliwattHours);
        Assert.Equal(120, report.Status.CycleCount);
        Assert.Equal(83.333, report.Status.HealthPercent!.Value, precision: 3);

        Assert.Collection(
            report.Intervals,
            interval =>
            {
                Assert.Equal(Utc(2026, 1, 1, 12), interval.StartUtc);
                Assert.Equal(Utc(2026, 1, 1, 13, 30), interval.EndUtc);
                Assert.Equal(WindowsBatteryPowerSource.Battery, interval.PowerSource);
                Assert.Equal(WindowsBatteryActivity.Active, interval.Activity);
                Assert.Equal(40000, interval.StartChargeMilliwattHours);
                Assert.Equal(5000, interval.DischargeMilliwattHours);
                Assert.Equal(80, interval.StartChargePercent);
            },
            interval =>
            {
                Assert.Equal(Utc(2026, 1, 1, 13, 30), interval.StartUtc);
                Assert.Equal(Utc(2026, 1, 1, 14), interval.EndUtc);
                Assert.Equal(WindowsBatteryPowerSource.Unknown, interval.PowerSource);
                Assert.Equal(WindowsBatteryActivity.Unknown, interval.Activity);
                Assert.Null(interval.StartChargeMilliwattHours);
            },
            interval =>
            {
                Assert.Equal(Utc(2026, 1, 1, 14), interval.StartUtc);
                Assert.Equal(Utc(2026, 1, 1, 14, 30), interval.EndUtc);
                Assert.Equal(WindowsBatteryPowerSource.Ac, interval.PowerSource);
                Assert.Equal(WindowsBatteryActivity.ConnectedStandby, interval.Activity);
            });
    }

    [Fact]
    public void TryParse_NormalizesExplicitOffsetsAndLeavesMissingDischargeUnknown()
    {
        byte[] xml = Xml("""
            <BatteryReport>
              <ReportInformation>
                <ScanTime>2026-01-02T18:00:00+06:00</ScanTime>
                <ReportStartTime>2026-01-02T16:00:00+06:00</ReportStartTime>
              </ReportInformation>
              <RecentUsage>
                <UsageEntry Timestamp="2026-01-02T17:00:00+06:00" Duration="36000000000"
                  Ac="0" EntryType="Active" ChargeCapacity="40000"
                  FullChargeCapacity="50000" />
              </RecentUsage>
            </BatteryReport>
            """);

        Assert.True(WindowsBatteryReportParser.TryParse(xml, out WindowsBatteryReport? report));
        Assert.Equal(Utc(2026, 1, 2, 12), report!.GeneratedAtUtc);
        Assert.Equal(Utc(2026, 1, 2, 10), report.WindowStartUtc);
        WindowsBatteryReportInterval interval = Assert.Single(report.Intervals);
        Assert.Equal(Utc(2026, 1, 2, 11), interval.StartUtc);
        Assert.Equal(Utc(2026, 1, 2, 12), interval.EndUtc);
        Assert.Null(interval.DischargeMilliwattHours);
    }

    [Fact]
    public void TryParse_ToleratesNoNamespaceAndMissingOptionalSections()
    {
        byte[] xml = Xml("""
            <BatteryReport>
              <ReportInformation>
                <ScanTime>2026-01-02T12:00:00Z</ScanTime>
                <ReportStartTime>2026-01-02T11:00:00Z</ReportStartTime>
              </ReportInformation>
            </BatteryReport>
            """);

        bool parsed = WindowsBatteryReportParser.TryParse(xml, out WindowsBatteryReport? report);

        Assert.True(parsed);
        Assert.NotNull(report);
        Assert.Null(report.Status.DesignCapacityMilliwattHours);
        Assert.Null(report.Status.FullChargeCapacityMilliwattHours);
        Assert.Null(report.Status.HealthPercent);
        Assert.Null(report.Status.CycleCount);
        Assert.Empty(report.Intervals);
        Assert.Equal(Utc(2026, 1, 2, 11), report.WindowStartUtc);
    }

    [Fact]
    public void TryParse_SumsMultipleBatteryCapacitiesWithoutInventingCycleCount()
    {
        byte[] xml = Xml("""
            <BatteryReport xmlns="urn:test">
              <ReportInformation><ScanTime>2026-01-02T12:00:00Z</ScanTime></ReportInformation>
              <Batteries>
                <Battery><DesignCapacity>30000</DesignCapacity><FullChargeCapacity>25000</FullChargeCapacity><CycleCount>20</CycleCount></Battery>
                <Battery><DesignCapacity>20000</DesignCapacity><FullChargeCapacity>15000</FullChargeCapacity><CycleCount>30</CycleCount></Battery>
              </Batteries>
            </BatteryReport>
            """);

        Assert.True(WindowsBatteryReportParser.TryParse(xml, out WindowsBatteryReport? report));
        Assert.Equal(50000, report!.Status.DesignCapacityMilliwattHours);
        Assert.Equal(40000, report.Status.FullChargeCapacityMilliwattHours);
        Assert.Equal(80, report.Status.HealthPercent);
        Assert.Null(report.Status.CycleCount);
    }

    [Fact]
    public void TryParse_LeavesMalformedReadingsUnknown()
    {
        byte[] xml = Xml("""
            <BatteryReport>
              <ReportInformation><ScanTime>2026-01-02T12:00:00Z</ScanTime></ReportInformation>
              <Batteries><Battery><DesignCapacity>0</DesignCapacity><FullChargeCapacity>-1</FullChargeCapacity><CycleCount>unknown</CycleCount></Battery></Batteries>
              <RecentUsage>
                <UsageEntry Timestamp="2026-01-02T11:00:00Z" Duration="6000000000"
                  Ac="other" EntryType="FutureState" ChargeCapacity="too-many"
                  FullChargeCapacity="0" Discharge="unknown" />
              </RecentUsage>
            </BatteryReport>
            """);

        Assert.True(WindowsBatteryReportParser.TryParse(xml, out WindowsBatteryReport? report));
        Assert.Null(report!.Status.HealthPercent);
        WindowsBatteryReportInterval interval = Assert.Single(report.Intervals);
        Assert.Equal(WindowsBatteryPowerSource.Unknown, interval.PowerSource);
        Assert.Equal(WindowsBatteryActivity.Unknown, interval.Activity);
        Assert.Null(interval.StartChargeMilliwattHours);
        Assert.Null(interval.DischargeMilliwattHours);
        Assert.Null(interval.StartChargePercent);
    }

    [Fact]
    public void TryParse_RejectsDtd()
    {
        byte[] xml = Xml("""
            <!DOCTYPE BatteryReport [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
            <BatteryReport><ReportInformation><ScanTime>&xxe;</ScanTime></ReportInformation></BatteryReport>
            """);

        Assert.False(WindowsBatteryReportParser.TryParse(xml, out _));
    }

    [Fact]
    public void TryParse_RejectsExcessUsageEntries()
    {
        var xml = new StringBuilder("<BatteryReport><ReportInformation><ScanTime>2026-01-02T12:00:00Z</ScanTime></ReportInformation><RecentUsage>");
        for (int index = 0; index <= WindowsBatteryReportParser.MaximumUsageEntries; index++)
        {
            xml.Append("<UsageEntry />");
        }
        xml.Append("</RecentUsage></BatteryReport>");

        Assert.False(WindowsBatteryReportParser.TryParse(Xml(xml.ToString()), out _));
    }

    private static byte[] Xml(string value) => Encoding.UTF8.GetBytes(value);

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
