using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPowerProbeResponsesTests
{
    private const string Response =
        """{"dllFunc":"GetPowerLimitValue","dllName":"RzAMDOverClockDLL","error":"","powerLimit":60,"status":"SUCCESS"}""";

    private static void Publish(CpuPowerProbeResponses responses, string message)
    {
        nint pointer = Marshal.StringToCoTaskMemUTF8(message);
        try
        {
            responses.AddUtf8(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [Theory]
    [InlineData("FAIL", "Driver read failed.")]
    [InlineData("SUCCESS", "Native error despite success status.")]
    public async Task QueryValueAsync_MatchingNativeFailureTerminatesBeforeALaterSuccess(string status, string error)
    {
        var responses = new CpuPowerProbeResponses();
        string failed = JsonSerializer.Serialize(new
        {
            dllName = "RzAMDOverClockDLL",
            dllFunc = "GetPowerLimitValue",
            status,
            error,
        });

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            responses.QueryValueAsync(() =>
            {
                Publish(responses, failed);
                Publish(responses, Response);
                return true;
            }, "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Contains(status, failure.Message, StringComparison.Ordinal);
        Assert.Contains(error, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryValueAsync_MatchingNativeFailureDoesNotBecomeAMissingReplyTimeout()
    {
        var responses = new CpuPowerProbeResponses();
        const string failed = """{"dllName":"RzAMDOverClockDLL","dllFunc":"GetCurveOptimizerValue","status":"FAIL","error":"Backend unavailable."}""";

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            responses.QueryValueAsync(() =>
            {
                Publish(responses, failed);
                return true;
            }, "GetCurveOptimizerValue", "curveOptimizer", TimeSpan.FromMilliseconds(20), CancellationToken.None));

        Assert.Contains("Backend unavailable.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryValueAsync_UnrelatedNativeFailureCannotTerminateTheCurrentGetter()
    {
        var responses = new CpuPowerProbeResponses();
        const string failed = """{"dllName":"RzAMDOverClockDLL","dllFunc":"GetCurveOptimizerValue","status":"FAIL","error":"Unrelated failure."}""";

        Assert.Equal(60, await responses.QueryValueAsync(() =>
        {
            Publish(responses, failed);
            Publish(responses, Response);
            return true;
        }, "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task QueryValueAsync_NativeFailureDetailIsBoundedAndSingleLine()
    {
        var responses = new CpuPowerProbeResponses();
        string failed = JsonSerializer.Serialize(new
        {
            dllName = "RzAMDOverClockDLL",
            dllFunc = "GetPowerLimitValue",
            status = new string('F', 1000),
            error = string.Concat(Enumerable.Repeat("driver\nlost\t", 500)),
        });

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            responses.QueryValueAsync(() =>
            {
                Publish(responses, failed);
                return true;
            }, "GetPowerLimitValue", "powerLimit", TimeSpan.FromMilliseconds(20), CancellationToken.None));

        Assert.InRange(failure.Message.Length, 1, 512);
        Assert.DoesNotContain('\n', failure.Message);
        Assert.DoesNotContain('\t', failure.Message);
    }

    [Fact]
    public async Task QueryValueAsync_FloodFailsExplicitlyBeforeAnotherGetter()
    {
        var responses = new CpuPowerProbeResponses();
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            responses.QueryValueAsync(() =>
            {
                for (int index = 0; index <= CpuPowerProbeResponses.MaximumMessages; index++)
                {
                    Publish(responses, "{}");
                }
                return true;
            }, "GetPowerLimitValue", "powerLimit",
                TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Contains("message limit", failure.Message, StringComparison.Ordinal);
        bool anotherGetter = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => responses.QueryValueAsync(
            () => anotherGetter = true, "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(anotherGetter);
    }

    [Fact]
    public async Task QueryValueAsync_OversizedUtf8MessagePoisonsProbe()
    {
        var responses = new CpuPowerProbeResponses();
        await Assert.ThrowsAsync<InvalidDataException>(() => responses.QueryValueAsync(
            () =>
            {
                Publish(responses, new string('\u00e9', CpuPowerProbeResponses.MaximumMessageBytes / 2 + 1));
                Publish(responses, Response);
                return true;
            }, "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AddUtf8_EnforcesTheNativeByteBoundaryBeforeReadingTheResponse(int excessBytes)
    {
        var responses = new CpuPowerProbeResponses();
        byte[] bytes = Encoding.UTF8.GetBytes(Response.PadRight(
            CpuPowerProbeResponses.MaximumMessageBytes + excessBytes) + '\0');
        nint pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Task<int> read = responses.QueryValueAsync(() =>
            {
                responses.AddUtf8(pointer);
                return true;
            },
                "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), CancellationToken.None);
            if (excessBytes == 0)
            {
                Assert.Equal(60, await read);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => read);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public async Task QueryValueAsync_CancellationIsObservedInsideReadyQueueDrain()
    {
        var responses = new CpuPowerProbeResponses();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responses.QueryValueAsync(
            () =>
            {
                Publish(responses, "{}");
                Publish(responses, Response);
                cancellation.Cancel();
                return true;
            }, "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), cancellation.Token));
    }

    [Fact]
    public async Task QueryValueAsync_AdmitsBoundedUnrelatedMessagesBeforeExactResponse()
    {
        var responses = new CpuPowerProbeResponses();
        Assert.Equal(60, await responses.QueryValueAsync(
            () =>
            {
                for (int index = 1; index < CpuPowerProbeResponses.MaximumMessages; index++)
                {
                    Publish(responses, "{}");
                }
                Publish(responses, Response.PadRight(CpuPowerProbeResponses.MaximumMessageBytes));
                return true;
            }, "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task QueryValueAsync_PrequeryFloodAndQueuedReplyCannotAnswerTheGetter()
    {
        var responses = new CpuPowerProbeResponses();
        for (int index = 0; index < CpuPowerProbeResponses.MaximumMessages * 4; index++)
        {
            Publish(responses, Response);
        }
        Publish(responses, new string('x', CpuPowerProbeResponses.MaximumMessageBytes + 1));

        Assert.Equal(61, await responses.QueryValueAsync(() =>
        {
            Publish(responses, Response.Replace("\"powerLimit\":60", "\"powerLimit\":61", StringComparison.Ordinal));
            return true;
        }, "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task QueryValueAsync_CurveReplyQueuedDuringEarlierGetterIsDiscarded()
    {
        const string curve = """{"dllFunc":"GetCurveOptimizerValue","dllName":"RzAMDOverClockDLL","error":"","curveOptimizer":-1,"status":"SUCCESS"}""";
        var responses = new CpuPowerProbeResponses();
        Assert.Equal(60, await responses.QueryValueAsync(() =>
        {
            Publish(responses, Response);
            Publish(responses, curve);
            return true;
        }, "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal(-2, await responses.QueryValueAsync(() =>
        {
            Publish(responses, curve.Replace(":-1", ":-2", StringComparison.Ordinal));
            return true;
        }, "GetCurveOptimizerValue", "curveOptimizer", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task QueryValueAsync_OverlappingGetterIsRefusedWithoutReplacingTheActiveQuery()
    {
        var responses = new CpuPowerProbeResponses();
        using var cancellation = new CancellationTokenSource();
        Task<int> pending = responses.QueryValueAsync(() => true,
            "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), cancellation.Token);
        bool overlappingGetter = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => responses.QueryValueAsync(
            () => overlappingGetter = true, "GetCurveOptimizerValue", "curveOptimizer",
            TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(overlappingGetter);

        Publish(responses, Response);
        Assert.Equal(60, await pending);
    }

    [Theory]
    [InlineData("plain native status")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"dllName\":123}")]
    [InlineData("{\"dllName\":\"RzAMDOverClockDLL\",\"dllFunc\":[]}")]
    [InlineData("{\"dllName\":\"RzAMDOverClockDLL\",\"dllFunc\":\"GetPowerLimitValue\",\"status\":\"SUCCESS\",\"error\":null,\"powerLimit\":60}")]
    [InlineData("{\"dllName\":\"RzAMDOverClockDLL\",\"dllFunc\":\"GetPowerLimitValue\",\"status\":\"SUCCESS\",\"error\":\"\",\"powerLimit\":\"60\"}")]
    public async Task QueryValueAsync_MalformedOrWrongTypedRecordsDoNotHideTheValidReply(string malformed)
    {
        var responses = new CpuPowerProbeResponses();
        Assert.Equal(60, await responses.QueryValueAsync(() =>
        {
            Publish(responses, malformed);
            Publish(responses, Response);
            return true;
        }, "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task AddUtf8_BoundedInvalidUtf8DoesNotAcquireAFalseReencodedByteSize()
    {
        var responses = new CpuPowerProbeResponses();
        byte[] bytes = Enumerable.Repeat((byte)0xe9, 10_000).Append((byte)0).ToArray();
        nint pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Assert.Equal(60, await responses.QueryValueAsync(() =>
            {
                responses.AddUtf8(pointer);
                Publish(responses, Response);
                return true;
            }, "GetPowerLimitValue", "powerLimit", TimeSpan.FromSeconds(1), CancellationToken.None));
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public async Task QueryValueAsync_MissingResponseHonorsDeadline()
    {
        var responses = new CpuPowerProbeResponses();

        await Assert.ThrowsAsync<TimeoutException>(() => responses.QueryValueAsync(
            () => true, "GetPowerLimitValue", "powerLimit", TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }
}
