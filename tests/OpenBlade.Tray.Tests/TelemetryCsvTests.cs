using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TelemetryCsvTests : IDisposable
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"OpenBlade.TelemetryCsv.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task WriteRead_RoundTripsMissingValuesQuotedTextEventsAndReplayMetadata()
    {
        string path = Path.Combine(directory, "session.csv");
        Directory.CreateDirectory(directory);
        TelemetrySessionMetadata metadata = TelemetrySessionMetadata.CreateLive(Start, Guid.Parse(
            "f77d0cbd-e910-4ec8-b77c-bec55f5c23df")) with
        {
            EndedAtUtc = Start.AddSeconds(2),
        };
        TelemetrySample[] samples =
        [
            Sample(0, "AC, quiet", battery: null, cpu: 50),
            Sample(2, "Battery \"safe\"", battery: -12_345, cpu: null),
        ];
        TelemetryEvent[] events =
        [
            new(Start.AddSeconds(2), TelemetryEventKind.ProfileChanged,
                "AC, quiet", "Battery \"safe\""),
        ];

        await TelemetryCsv.WriteAsync(path, new(metadata, samples, events));
        var importedAt = Start.AddHours(1);
        TelemetrySessionDocument replay = await TelemetryCsv.ReadAsync(
            path,
            timeProvider: new FixedTimeProvider(importedAt));

        Assert.Equal(TelemetrySource.LocalCsvReplay, replay.Metadata.Source);
        Assert.Equal(TelemetrySource.LiveService, replay.Metadata.RecordedSource);
        Assert.Equal(importedAt, replay.Metadata.ImportedAtUtc);
        Assert.Equal(Path.GetFullPath(path), replay.Metadata.OriginPath);
        Assert.Equal(metadata.SessionId, replay.Metadata.SessionId);
        Assert.Equal(metadata.EndedAtUtc, replay.Metadata.EndedAtUtc);
        Assert.Collection(
            replay.Samples,
            first =>
            {
                Assert.Null(first.BatteryPowerFlowMilliwatts);
                Assert.Equal("AC, quiet", first.ProfileIdentity);
                Assert.Equal(TelemetrySource.LocalCsvReplay, first.Source);
            },
            second =>
            {
                Assert.Equal(-12_345, second.BatteryPowerFlowMilliwatts);
                Assert.Null(second.CpuTemperatureCelsius);
                Assert.Equal("Battery \"safe\"", second.ProfileIdentity);
            });
        Assert.Single(replay.Events);
    }

    [Theory]
    [InlineData("OpenBladeTelemetry,2\r\n")]
    [InlineData("OpenBladeTelemetry,1\r\nMetadata,not-a-guid,2026-10-21T12:00:00.0000000+00:00,LiveService\r\n")]
    [InlineData("\"unterminated")]
    public async Task Read_RejectsUnsupportedOrMalformedContent(string content)
    {
        string path = WriteText("invalid.csv", content);

        await Assert.ThrowsAsync<InvalidDataException>(() => TelemetryCsv.ReadAsync(path));
    }

    [Fact]
    public async Task Read_RejectsOversizedFileBeforeParsing()
    {
        string path = WriteText("large.csv", new string('x', 100));
        var limits = TelemetryCsvLimits.Default with { MaximumBytes = 20 };

        await Assert.ThrowsAsync<InvalidDataException>(() => TelemetryCsv.ReadAsync(path, limits));
    }

    [Fact]
    public async Task Read_ObservesCancellation()
    {
        string path = Path.Combine(directory, "canceled.csv");
        Directory.CreateDirectory(directory);
        await TelemetryCsv.WriteAsync(path, new(
            TelemetrySessionMetadata.CreateLive(Start) with { EndedAtUtc = Start },
            [Sample(0, "AC")],
            []));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TelemetryCsv.ReadAsync(path, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Writer_ReservesByteCapacityForTheRequiredEndMarker()
    {
        TelemetrySessionMetadata metadata = TelemetrySessionMetadata.CreateLive(Start, Guid.Parse(
            "f77d0cbd-e910-4ec8-b77c-bec55f5c23df"));
        int requiredDocumentBytes = TelemetryCsv.Encoding.GetByteCount(
            TelemetryCsv.FormatRow("OpenBladeTelemetry", "1")
            + TelemetryCsv.FormatRow(
                "Metadata",
                metadata.SessionId.ToString("D"),
                Start.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                TelemetrySource.LiveService.ToString())
            + TelemetryCsv.FormatRow(TelemetryCsv.ColumnHeader.ToArray())
            + TelemetryCsv.FormatRow(
                "End",
                Start.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        var limits = TelemetryCsvLimits.Default with { MaximumBytes = requiredDocumentBytes };
        await using var stream = new MemoryStream();
        await using TelemetryCsvWriter writer = await TelemetryCsv.CreateWriterAsync(
            stream,
            metadata,
            limits,
            leaveOpen: true,
            CancellationToken.None);

        await Assert.ThrowsAsync<TelemetryCapacityReachedException>(
            () => writer.WriteSampleAsync(Sample(0, "AC"), CancellationToken.None));
        await writer.CompleteAsync(Start, CancellationToken.None);

        Assert.Equal(requiredDocumentBytes, stream.Length);
        Assert.EndsWith(
            TelemetryCsv.FormatRow("End", Start.ToString(
                "O", System.Globalization.CultureInfo.InvariantCulture)),
            TelemetryCsv.Encoding.GetString(stream.ToArray()),
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private string WriteText(string name, string content)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static TelemetrySample Sample(
        int second,
        string profile,
        int? battery = 10_000,
        int? cpu = 55) => new(
            Start.AddSeconds(second),
            second,
            TelemetrySource.LiveService,
            second == 0 ? PowerSource.PluggedIn : PowerSource.Battery,
            profile,
            battery,
            2_000,
            null,
            cpu,
            60);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
