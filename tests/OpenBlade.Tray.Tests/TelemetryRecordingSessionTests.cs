using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TelemetryRecordingSessionTests : IDisposable
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"OpenBlade.TelemetryRecording.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Stop_DrainsQueuedRowsAndAtomicallyPromotesPartialFile()
    {
        string path = Path.Combine(directory, "recording.csv");
        Directory.CreateDirectory(directory);
        var clock = new MutableTimeProvider(Start);
        await using TelemetryRecordingSession recording = await TelemetryRecordingSession.StartAsync(
            path,
            TelemetrySessionMetadata.CreateLive(Start),
            timeProvider: clock);

        Assert.True(recording.IsActive);
        Assert.True(recording.RequiresSampling);
        Assert.True(File.Exists(path + ".partial"));
        Assert.False(File.Exists(path));
        await recording.AppendAsync(Sample(1));
        await recording.AppendAsync(
            Sample(2, PowerSource.Battery, "Battery"),
            [new(Start.AddSeconds(2), TelemetryEventKind.PowerSourceChanged,
                PowerSource.PluggedIn.ToString(), PowerSource.Battery.ToString())]);
        clock.Now = Start.AddSeconds(3);

        string completed = await recording.StopAsync();

        Assert.Equal(path, completed);
        Assert.False(recording.IsActive);
        Assert.False(File.Exists(path + ".partial"));
        Assert.True(File.Exists(path));
        TelemetrySessionDocument replay = await TelemetryCsv.ReadAsync(path);
        Assert.Equal(2, replay.Samples.Count);
        Assert.Single(replay.Events);
        Assert.Equal(Start.AddSeconds(3), replay.Metadata.EndedAtUtc);
    }

    [Fact]
    public async Task Dispose_DrainsAndFinalizesOpenRecording()
    {
        string path = Path.Combine(directory, "disposed.csv");
        Directory.CreateDirectory(directory);
        var recording = await TelemetryRecordingSession.StartAsync(
            path,
            TelemetrySessionMetadata.CreateLive(Start),
            timeProvider: new MutableTimeProvider(Start.AddSeconds(2)));
        await recording.AppendAsync(Sample(1));

        await recording.DisposeAsync();

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".partial"));
        Assert.Single((await TelemetryCsv.ReadAsync(path)).Samples);
    }

    [Fact]
    public async Task CanceledStopCaller_DoesNotAbandonFinalization()
    {
        string path = Path.Combine(directory, "canceled-stop.csv");
        Directory.CreateDirectory(directory);
        await using TelemetryRecordingSession recording = await TelemetryRecordingSession.StartAsync(
            path,
            TelemetrySessionMetadata.CreateLive(Start),
            timeProvider: new MutableTimeProvider(Start.AddSeconds(2)));
        await recording.AppendAsync(Sample(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => recording.StopAsync(cancellation.Token));
        Assert.Equal(path, await recording.StopAsync());

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task Append_RejectsReplayAndGracefullyFinalizesAtDurationLimit()
    {
        string path = Path.Combine(directory, "bounded.csv");
        Directory.CreateDirectory(directory);
        var limits = TelemetryRecordingLimits.Default with
        {
            MaximumDuration = TimeSpan.FromSeconds(5),
        };
        await using TelemetryRecordingSession recording = await TelemetryRecordingSession.StartAsync(
            path,
            TelemetrySessionMetadata.CreateLive(Start),
            limits,
            new MutableTimeProvider(Start));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await recording.AppendAsync(Sample(1) with { Source = TelemetrySource.LocalCsvReplay }));
        await recording.AppendAsync(Sample(6));
        await ((ITelemetryRecordingSession)recording).Completion.WaitAsync(TimeSpan.FromSeconds(5));
        string completed = await recording.StopAsync();

        Assert.Equal(path, completed);
        Assert.Contains("duration", ((ITelemetryRecordingSession)recording).AutomaticCompletionReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".partial"));
        Assert.Empty((await TelemetryCsv.ReadAsync(path)).Samples);
    }

    [Fact]
    public async Task SampleCapacity_GracefullyFinalizesAReadableDestination()
    {
        string path = Path.Combine(directory, "sample-capacity.csv");
        Directory.CreateDirectory(directory);
        var limits = TelemetryRecordingLimits.Default with
        {
            Csv = TelemetryCsvLimits.Default with { MaximumSamples = 1 },
        };
        await using TelemetryRecordingSession recording = await TelemetryRecordingSession.StartAsync(
            path,
            TelemetrySessionMetadata.CreateLive(Start),
            limits,
            new MutableTimeProvider(Start.AddSeconds(3)));

        await recording.AppendAsync(Sample(1));
        await recording.AppendAsync(Sample(2));
        await ((ITelemetryRecordingSession)recording).Completion.WaitAsync(TimeSpan.FromSeconds(5));
        string completed = await recording.StopAsync();

        Assert.Equal(path, completed);
        Assert.Contains("sample limit",
            ((ITelemetryRecordingSession)recording).AutomaticCompletionReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path + ".partial"));
        Assert.Single((await TelemetryCsv.ReadAsync(path)).Samples);
    }

    [Fact]
    public async Task Start_PreservesExistingPartialRecoveryFile()
    {
        string path = Path.Combine(directory, "existing.csv");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path + ".partial", "recover me");

        await Assert.ThrowsAsync<IOException>(() => TelemetryRecordingSession.StartAsync(
            path,
            TelemetrySessionMetadata.CreateLive(Start)));

        Assert.Equal("recover me", File.ReadAllText(path + ".partial"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TelemetrySample Sample(
        int second,
        PowerSource power = PowerSource.PluggedIn,
        string profile = "AC") => new(
            Start.AddSeconds(second),
            second,
            TelemetrySource.LiveService,
            power,
            profile,
            10_000,
            2_000,
            2_100,
            55,
            60);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
