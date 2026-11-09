using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Tray;

DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
var results = new List<object>();
foreach (int count in new[] { 3_600, 86_400 })
{
    var history = new TelemetryHistory(count);
    for (int index = 0; index < count; index++)
    {
        history.AddSample(new TelemetrySample(
            start.AddSeconds(index + index / 113 * 5), index, TelemetrySource.LiveService,
            index % 600 < 300 ? PowerSource.PluggedIn : PowerSource.Battery,
            index % 400 < 200 ? "AC" : "Game",
            index % 17 == 0 ? null : index % 80_000 - 40_000,
            index % 19 == 0 ? null : 1_000 + index % 5_000,
            index % 23 == 0 ? null : 1_200 + index % 4_800,
            index % 29 == 0 ? null : 30 + index % 70,
            index % 31 == 0 ? null : 25 + index % 65));
    }
    DateTimeOffset end = start.AddSeconds(count + count / 113 * 5);
    foreach (bool restricted in new[] { false, true })
    {
        DateTimeOffset visibleStart = restricted ? end.AddSeconds(-600) : start;
        TelemetryHistorySnapshot Run()
        {
            TelemetryHistorySnapshot retained = history.Snapshot(rangeEndUtc: end,
                expectedSampleInterval: TimeSpan.FromSeconds(1));
            return TelemetryChartViewport.BuildSnapshot(retained, visibleStart, end);
        }
        for (int iteration = 0; iteration < 8; iteration++)
        {
            _ = Run();
        }
        var rounds = new List<object>();
        string? digest = null;
        TelemetryHistorySnapshot? finalResult = null;
        for (int round = 0; round < 5; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long began = Stopwatch.GetTimestamp();
            TelemetryHistorySnapshot? result = null;
            const int iterations = 10;
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                result = Run();
            }
            double elapsedMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds / iterations;
            long allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;
            digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result)));
            finalResult = result;
            rounds.Add(new { elapsedMs, allocatedBytes = allocated });
        }
        var selectedPointCounts = finalResult!.Series.Select(series => new
        {
            metric = series.Metric.ToString(),
            valid = series.Points.Count(point => point.Value is not null),
            missing = series.Points.Count(point => point.Value is null),
        }).ToArray();
        results.Add(new { count, restricted, digest, selectedPointCounts, rounds });
    }
}
string output = JsonSerializer.Serialize(new
{
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    assembly = typeof(TelemetryHistory).Assembly.FullName,
    results,
}, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(output);
if (args.Length > 0)
{
    File.WriteAllText(args[0], output);
}
