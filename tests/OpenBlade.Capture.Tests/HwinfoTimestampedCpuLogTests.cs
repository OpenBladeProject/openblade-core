using System.Text;
using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class HwinfoTimestampedCpuLogTests
{
    [Fact]
    public void ReadsExactNamedSensorAndIncrementallyCompletesSplitUtf8Tail()
    {
        using var fixture = new Fixture();
        string first = Member(fixture.Clock.GetUtcNow(), "55.5");
        fixture.Append("{" + first + ",");
        using var reader = fixture.Reader();
        Assert.Equal(55.5, reader.ReadFresh().Celsius);
        fixture.Clock.Advance(2000);
        byte[] next = Encoding.UTF8.GetBytes(Member(fixture.Clock.GetUtcNow(), "56.5"));
        int split = Array.IndexOf(next, (byte)0xC2) + 1;
        fixture.Append(next[..split]);
        var partial = reader.ReadFresh();
        Assert.Null(partial.Failure);
        Assert.Equal(55.5, partial.Celsius);
        fixture.Append(next[split..]);
        var complete = reader.ReadFresh();
        Assert.Null(complete.Failure);
        Assert.Equal(56.5, complete.Celsius);
        Assert.Equal(fixture.Clock.GetUtcNow(), complete.SourceTimestampUtc);
    }

    [Fact]
    public void UnchangedOrPartialRowsCannotKeepSourceFreshAndFailureLatches()
    {
        using var fixture = new Fixture();
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow()));
        using var reader = fixture.Reader();
        Assert.Null(reader.ReadFresh().Failure);
        fixture.Clock.Advance(4001);
        fixture.Append(",\"unfinished");
        Assert.Contains("StaleOrFutureTimestamp", reader.ReadFresh().Failure);
        Assert.NotNull(reader.ReadFresh().Failure);
    }

    [Theory]
    [InlineData(-4001)]
    [InlineData(1)]
    public void RefusesStaleOrFutureSource(int offset)
    {
        using var fixture = new Fixture();
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow().AddMilliseconds(offset)));
        using var reader = fixture.Reader();
        Assert.Contains("StaleOrFutureTimestamp", reader.ReadFresh().Failure);
    }

    [Fact]
    public void RejectsDuplicateAndRegressingTimestamps()
    {
        foreach (int offset in new[] { 0, -1 })
        {
            using var fixture = new Fixture();
            fixture.Append("{" + Member(fixture.Clock.GetUtcNow()) + "," + Member(fixture.Clock.GetUtcNow().AddMilliseconds(offset)));
            using var reader = fixture.Reader();
            Assert.Contains("NonAdvancingTimestamp", reader.ReadFresh().Failure);
        }
    }

    [Theory]
    [InlineData("\"Value\":\"52\",\"Value\":\"53\",\"Unit\":\"°C\"", "DuplicateProperty")]
    [InlineData("\"Value\":\"52\",\"Unit\":\"F\"", "SensorUnitChanged")]
    [InlineData("\"Value\":\"0\",\"Unit\":\"°C\"", "InvalidCpuTemperature")]
    public void RejectsAmbiguousOrInvalidSelectedReading(string fields, string reason)
    {
        using var fixture = new Fixture();
        string data = Member(fixture.Clock.GetUtcNow());
        int position = data.IndexOf("{\"Value\"", StringComparison.Ordinal);
        data = data[..position] + "{" + fields + "}}}";
        fixture.Append("{" + data);
        using var reader = fixture.Reader();
        Assert.Contains(reason, reader.ReadFresh().Failure);
    }

    [Fact]
    public void RequiresVerifiedSameProcessOnEveryRead()
    {
        using var fixture = new Fixture();
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow()));
        using var reader = fixture.Reader();
        Assert.Null(reader.ReadFresh().Failure);
        fixture.Current = fixture.Current with { ProcessId = fixture.Current.ProcessId + 1 };
        Assert.Contains("HwinfoProcessIdentityChanged", reader.ReadFresh().Failure);
    }

    [Fact]
    public void RefusesUnsignedConfiguration()
    {
        using var fixture = new Fixture();
        fixture.Current = fixture.Current with { SignatureVerified = false };
        Assert.Throws<ArgumentException>(() => fixture.Reader());
    }

    [Fact]
    public void TruncationAndReplacementAreNotSilentlyFollowed()
    {
        using var fixture = new Fixture();
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow()));
        using var reader = fixture.Reader();
        Assert.Null(reader.ReadFresh().Failure);
        Assert.Throws<IOException>(() => File.Move(fixture.Path, fixture.Path + ".moved"));
        using (var writer = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.SetLength(0);
        }
        Assert.Contains("FileTruncated", reader.ReadFresh().Failure);
    }

    [Fact]
    public void RejectsWallClockJumpAndCanceledReads()
    {
        using var fixture = new Fixture();
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow()));
        using var reader = fixture.Reader();
        Assert.Null(reader.ReadFresh().Failure);
        fixture.Clock.UtcJump = 1000;
        Assert.Contains("WallClockDiscontinuity", reader.ReadFresh().Failure);
        using var canceled = fixture.Reader();
        Assert.Equal("Canceled", canceled.ReadFresh(new CancellationToken(true)).Failure);
    }

    [Fact]
    public void MidnightUsesFullDateAndConstantTemperatureStillAdvances()
    {
        using var fixture = new Fixture();
        fixture.Clock.Utc = new DateTimeOffset(2026, 9, 27, 23, 59, 59, TimeSpan.Zero);
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow()));
        using var reader = fixture.Reader();
        Assert.Null(reader.ReadFresh().Failure);
        fixture.Clock.Advance(2000);
        fixture.Append("," + Member(fixture.Clock.GetUtcNow()));
        var value = reader.ReadFresh();
        Assert.Null(value.Failure);
        Assert.Equal(28, value.SourceTimestampUtc!.Value.Day);
    }

    [Fact]
    public void OversizedPartialObjectFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow()));
        using var reader = fixture.Reader();
        Assert.Null(reader.ReadFresh().Failure);
        fixture.Append(",\"unfinished" + new string('x', 530000));
        Assert.Contains("IncompleteObjectTooLarge", reader.ReadFresh().Failure);
    }

    [Fact]
    public void InvalidUtf8IsNeverReplacedWithASuccessfulSample()
    {
        using var fixture = new Fixture();
        byte[] content = Encoding.UTF8.GetBytes("{" + Member(fixture.Clock.GetUtcNow()));
        content[Array.IndexOf(content, (byte)0xC2)] = 0xFF;
        fixture.Append(content);
        using var reader = fixture.Reader();
        Assert.Contains("DecoderFallbackException", reader.ReadFresh().Failure);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"55\"")]
    [InlineData("[]")]
    public void CompleteInvalidNextTimestampCannotReusePriorTemperature(string invalid)
    {
        using var fixture = new Fixture();
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow()));
        using var reader = fixture.Reader();
        Assert.Null(reader.ReadFresh().Failure);
        fixture.Append(",\"27.9.2026 12:0:2.000 \":" + invalid);
        Assert.Contains("ExpectedTimestampObject", reader.ReadFresh().Failure);
    }

    [Fact]
    public void MissingExactGroupCannotFallBackToAnotherCpuSensor()
    {
        using var fixture = new Fixture();
        fixture.Append(("{" + Member(fixture.Clock.GetUtcNow())).Replace("Enhanced", "Other", StringComparison.Ordinal));
        using var reader = fixture.Reader();
        Assert.NotNull(reader.ReadFresh().Failure);
        Assert.Null(reader.ReadFresh().Celsius);
    }

    [Fact]
    public void PrimesHistoryButPreservesHotIntermediateNewSample()
    {
        using var fixture = new Fixture();
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow().AddSeconds(-10), "90") + "," + Member(fixture.Clock.GetUtcNow(), "60"));
        using var reader = fixture.Reader();
        var initial = reader.ReadFresh();
        Assert.Null(initial.Failure);
        Assert.True(initial.IsPrimingRead);
        Assert.Null(initial.NewSamplesMaximumCelsius);
        Assert.Equal(60, initial.Celsius);
        fixture.Clock.Advance(2000);
        DateTimeOffset hotAt = fixture.Clock.GetUtcNow();
        fixture.Append("," + Member(hotAt, "80"));
        fixture.Clock.Advance(2000);
        fixture.Append("," + Member(fixture.Clock.GetUtcNow(), "60"));
        var next = reader.ReadFresh();
        Assert.Null(next.Failure);
        Assert.False(next.IsPrimingRead);
        Assert.Equal(60, next.Celsius);
        Assert.Equal(80, next.NewSamplesMaximumCelsius);
        Assert.Equal(hotAt, next.FirstNewSourceTimestampUtc);
        Assert.Equal(fixture.Clock.GetUtcNow(), next.LastNewSourceTimestampUtc);
        var repeated = reader.ReadFresh();
        Assert.Null(repeated.NewSamplesMaximumCelsius);
        Assert.Equal(60, repeated.Celsius);
    }

    [Fact]
    public void ExistingLogCannotBeAttributedToNewerProcess()
    {
        using var fixture = new Fixture();
        fixture.Current = fixture.Current with { StartedAtUtc = fixture.Clock.GetUtcNow().AddMilliseconds(1) };
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow()));
        using var reader = fixture.Reader();
        Assert.Contains("LogPredatesVerifiedProcess", reader.ReadFresh().Failure);
    }

    [Fact]
    public void CancelDuringProcessVerificationDoesNotReturnSuccess()
    {
        using var fixture = new Fixture();
        fixture.Append("{" + Member(fixture.Clock.GetUtcNow()));
        using var cancellation = new CancellationTokenSource();
        bool cancel = false;
        using var reader = new HwinfoTimestampedCpuLog(fixture.Path, fixture.Current, () =>
        {
            if (cancel)
            {
                cancellation.Cancel();
            }
            return fixture.Current;
        }, fixture.Clock, TimeZoneInfo.Utc);
        cancel = true;
        Assert.Equal("Canceled", reader.ReadFresh(cancellation.Token).Failure);
    }

    private static string Member(DateTimeOffset time, string value = "52") =>
        JsonSerializer.Serialize(time.ToString("d.M.yyyy H:m:s.fff ", System.Globalization.CultureInfo.InvariantCulture)) + ":{" +
        JsonSerializer.Serialize(HwinfoTimestampedCpuLog.SensorGroup) + ":{" +
        JsonSerializer.Serialize(HwinfoTimestampedCpuLog.SensorName) + ":{\"Value\":\"" + value + "\",\"Unit\":\"°C\"}}}";

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Utc = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        internal long Ticks;
        internal int UtcJump;
        public override long TimestampFrequency => 1000;
        public override DateTimeOffset GetUtcNow() => Utc.AddMilliseconds(UtcJump);
        public override long GetTimestamp() => Ticks;
        internal void Advance(int milliseconds) { Utc = Utc.AddMilliseconds(milliseconds); Ticks += milliseconds; }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Path = System.IO.Path.GetTempFileName();
        internal readonly Clock Clock = new();
        internal HwinfoProcessIdentity Current = new(123, DateTimeOffset.Parse("2026-09-27T10:00:00Z"),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HWiNFO64.exe"), new string('A', 64), "8.48", true);
        internal HwinfoTimestampedCpuLog Reader() => new(Path, Current, () => Current, Clock, TimeZoneInfo.Utc);
        internal void Append(string value) => Append(Encoding.UTF8.GetBytes(value));
        internal void Append(byte[] value)
        {
            using var writer = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            writer.Write(value);
        }
        public void Dispose() => File.Delete(Path);
    }
}
