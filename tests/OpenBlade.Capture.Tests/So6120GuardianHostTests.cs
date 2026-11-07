using System.IO.Pipes;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class So6120GuardianHostTests
{
    [Fact]
    public async Task PumpSendsAutoOnceAndKeepsTheControlPipeOpen()
    {
        using var output = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int checks = 0;

        await So6120GuardianHost.PumpControlAsync(output, () => ++checks > 1, timeout.Token).WaitAsync(timeout.Token);

        Assert.Equal(new byte[] { 1, 2 }, output.ToArray());
        Assert.True(output.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderDrainsHeartbeatsAfterStopWithoutRenewingAuthority(bool expireLease)
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new So6120DirectDutySessionTests.Clock();
        await using var signals = new So6120GuardianSignals(clock);
        Task reader = So6120GuardianHost.ReadControlAsync(client, signals, timeout.Token);
        try
        {
            server.WriteByte(1);
            await signals.FirstHeartbeat.WaitAsync(timeout.Token);

            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = signals.StopToken.Register(() => stopped.TrySetResult());
            if (expireLease)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                signals.CheckDeadline();
            }
            else { server.WriteByte(2); }

            await stopped.Task.WaitAsync(timeout.Token);
            server.Write(Enumerable.Repeat((byte)1, 128).ToArray());
            while (true)
            {
                Assert.True(So6120GuardianProcess.TryGetAvailableControlBytes(client.SafePipeHandle, out uint available));
                if (available == 0) { break; }
                await Task.Delay(20, timeout.Token);
            }

            Assert.False(reader.IsCompleted);
            Assert.Equal(expireLease ? So6120GuardianStopReason.LeaseExpired : So6120GuardianStopReason.ExplicitAuto,
                signals.StopReason);
        }
        finally
        {
            await timeout.CancelAsync();
            await reader;
        }
    }

    [Theory]
    [InlineData(0, "completed trial")]
    [InlineData(2, "Leave OpenBlade stopped.")]
    [InlineData(3, "declined without setting writes")]
    [InlineData(So6120GuardianHost.OwnerFailure, "Leave OpenBlade stopped.")]
    [InlineData(99, "Leave OpenBlade stopped.")]
    public void TrialExitRelaysOutcomeAndEvidencePath(int exitCode, string expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "guardian evidence");
        string report = So6120GuardianHost.FormatOwnerExit(exitCode, root);

        Assert.Contains(expected, report);
        Assert.Contains(Path.Combine(root, "attended-run.jsonl"), report);
        Assert.DoesNotContain("restored", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("released", report, StringComparison.OrdinalIgnoreCase);
    }
}
