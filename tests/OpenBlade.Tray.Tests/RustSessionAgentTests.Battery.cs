using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustSessionAgentTests
{
    [RustSessionAgentFact]
    public async Task TrayBatteryClient_ReadsClippedTimelineFromRustFixture()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, mode: "battery-foreground");
        await host.WaitForReadyAsync(timeout.Token);

        var client = new SessionBatteryReportAdapter(BatteryPipeName(id));
        WindowsBatteryReport report = await client.ReadAsync(timeout.Token);
        Assert.Equal(WindowsBatteryReportSource.WindowsPowerCfg, report.Source);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero), report.GeneratedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), report.WindowStartUtc);
        Assert.Equal(report.GeneratedAtUtc, report.WindowEndUtc);
        Assert.Equal(60000, report.Status.DesignCapacityMilliwattHours);
        Assert.Equal(50000, report.Status.FullChargeCapacityMilliwattHours);
        Assert.Equal(120, report.Status.CycleCount);
        Assert.Equal(83.333, report.Status.HealthPercent!.Value, precision: 3);
        Assert.Collection(report.Intervals,
            active =>
            {
                Assert.Equal(report.WindowStartUtc, active.StartUtc);
                Assert.Equal(report.WindowStartUtc.AddMinutes(90), active.EndUtc);
                Assert.Equal(WindowsBatteryPowerSource.Battery, active.PowerSource);
                Assert.Equal(WindowsBatteryActivity.Active, active.Activity);
                Assert.Equal(40000, active.StartChargeMilliwattHours);
                Assert.Equal(5000, active.DischargeMilliwattHours);
                Assert.Equal(80, active.StartChargePercent);
            },
            gap =>
            {
                Assert.Equal(report.WindowStartUtc.AddMinutes(90), gap.StartUtc);
                Assert.Equal(report.WindowStartUtc.AddHours(2), gap.EndUtc);
                Assert.Equal(WindowsBatteryPowerSource.Unknown, gap.PowerSource);
                Assert.Equal(WindowsBatteryActivity.Unknown, gap.Activity);
                Assert.Null(gap.StartChargeMilliwattHours);
            },
            standby =>
            {
                Assert.Equal(report.WindowStartUtc.AddHours(2), standby.StartUtc);
                Assert.Equal(report.WindowStartUtc.AddMinutes(150), standby.EndUtc);
                Assert.Equal(WindowsBatteryPowerSource.Ac, standby.PowerSource);
                Assert.Equal(WindowsBatteryActivity.ConnectedStandby, standby.Activity);
            });

        WindowsBatteryReport next = await client.ReadAsync(timeout.Token);
        Assert.Equal(report.Intervals.ToArray(), next.Intervals.ToArray());
        await host.StopAsync(timeout.Token);
    }

    [RustSessionAgentFact]
    public async Task TrayBatteryClient_ReceivesTypedFailureWithoutAReport()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (string scenario in new[] { "missing", "malformed", "oversized", "failed" })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario, mode: "battery-foreground");
            await host.WaitForReadyAsync(timeout.Token);

            var client = new SessionBatteryReportAdapter(BatteryPipeName(id));
            // A valid failure response is distinct from an IOException caused by a bad frame or pipe.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.ReadAsync(timeout.Token));
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            await host.StopAsync(timeout.Token);
        }
    }

    [RustSessionAgentFact]
    public async Task TrayBatteryClient_PropagatesCancellationWhenReportStalls()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, "stalled", mode: "battery-foreground");
        await host.WaitForReadyAsync(timeout.Token);

        var client = new SessionBatteryReportAdapter(BatteryPipeName(id));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync(cancelled.Token));
        await host.StopAsync(timeout.Token);
    }

    private static string BatteryPipeName(string id) => $"OpenBlade.Rust.Session.Battery.Dev.{id}";
}
