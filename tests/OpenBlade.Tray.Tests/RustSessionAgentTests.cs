using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustSessionAgentTests
{
    [RustSessionAgentFact]
    public async Task TrayDisplayClient_ReadsScriptedAcquisitionAndPreservesUnavailableQuerySuccess()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        foreach (string scenario in new[] { "available", "unavailable" })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario);
            await host.WaitForReadyAsync(token);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));

            DisplaySettingsSnapshot result = await client.GetStateAsync(token);
            Assert.True(result.OperationSucceeded);
            Assert.Equal(scenario == "available", result.IsAvailable);
            Assert.Null(result.SelectedColorProfileKey);
            Assert.Empty(result.ColorProfileOptions);
            if (result.IsAvailable)
            {
                Assert.Equal((byte)40, result.BrightnessPercent);
                Assert.Equal(new byte[] { 10, 40, 70, 100 }, result.SupportedBrightnessLevels);
                Assert.Equal("Synthetic panel", result.DisplayName);
                Assert.Equal(2560, result.Width);
                Assert.Equal(1600, result.Height);
                Assert.Equal(10, result.BitsPerColorChannel);
                Assert.True(result.IsHdrSupported);
                Assert.True(result.IsHdrEnabled);
                // Managed oracle case fixed_split_is_not_fixed (60/240, boost off).
                Assert.Null(result.SelectedRefreshRateKey);
                Assert.Collection(result.RefreshRateOptions,
                    option => Assert.Equal(new DisplayRefreshRateOption("fixed-60", "60 Hz"), option),
                    option => Assert.Equal(new DisplayRefreshRateOption("fixed-240", "240 Hz"), option));
            }
            else
            {
                Assert.Null(result.BrightnessPercent);
                Assert.Empty(result.SupportedBrightnessLevels);
                Assert.Null(result.DisplayName);
                Assert.Null(result.BitsPerColorChannel);
                Assert.Empty(result.RefreshRateOptions);
                Assert.False(result.IsHdrSupported);
                Assert.False(result.IsHdrEnabled);
                Assert.Equal("No active display topology is available.", result.StatusMessage);
            }

            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_ConfirmsBrightnessAndRecognizesNoOp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id);
        await host.WaitForReadyAsync(token);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));

        DisplaySettingsSnapshot blocked = await client.SetBrightnessAsync(PowerSource.Battery, 70, token);
        Assert.False(blocked.OperationSucceeded);
        Assert.Contains("was not submitted", blocked.StatusMessage);
        Assert.Equal((byte)40, (await client.GetStateAsync(token)).BrightnessPercent);

        DisplaySettingsSnapshot noOp = await client.SetBrightnessAsync(PowerSource.PluggedIn, 55, token);
        Assert.True(noOp.OperationSucceeded);
        Assert.Equal((byte)40, noOp.BrightnessPercent);
        Assert.Equal("Brightness was already at the selected level.", noOp.StatusMessage);

        DisplaySettingsSnapshot confirmed = await client.SetBrightnessAsync(PowerSource.PluggedIn, 67, token);
        Assert.True(confirmed.OperationSucceeded);
        Assert.True(confirmed.IsAvailable);
        Assert.Equal((byte)70, confirmed.BrightnessPercent);
        Assert.Equal(new byte[] { 10, 40, 70, 100 }, confirmed.SupportedBrightnessLevels);
        Assert.Equal((byte)70, (await client.GetStateAsync(token)).BrightnessPercent);
        await host.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_KeepsConfirmedDisplayFieldsWhenBrightnessIsUnavailable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        foreach (string scenario in new[] { "missing-wmi", "replaced-panel" })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, brightness: scenario);
            await host.WaitForReadyAsync(token);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            DisplaySettingsSnapshot before = await client.GetStateAsync(token);
            Assert.Equal(scenario == "missing-wmi" ? (byte?)null : 40, before.BrightnessPercent);
            DisplaySettingsSnapshot blocked = await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token);
            Assert.False(blocked.OperationSucceeded);
            Assert.Contains(scenario == "missing-wmi" ? "MissingData" : "PanelChanged", blocked.StatusMessage);
            Assert.Null(blocked.BrightnessPercent);

            DisplaySettingsSnapshot result = await client.GetStateAsync(token);
            Assert.True(result.OperationSucceeded);
            Assert.True(result.IsAvailable);
            Assert.Equal("Synthetic panel", result.DisplayName);
            Assert.Equal(2560, result.Width);
            Assert.Equal(10, result.BitsPerColorChannel);
            Assert.True(result.IsHdrSupported);
            Assert.True(result.IsHdrEnabled);
            Assert.Equal(2, result.RefreshRateOptions.Count);
            Assert.Null(result.BrightnessPercent);
            Assert.Empty(result.SupportedBrightnessLevels);
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task PrivateHost_RequiresExactReadbackAndPausesWritesAcrossConnections()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        foreach (string scenario in new[] { "sibling-readback", "stalled-write" })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, brightness: scenario);
            await host.WaitForReadyAsync(token);
            using var pipe = Pipe(id);
            await pipe.ConnectAsync(token);
            Guid requestId = Guid.NewGuid();
            await SessionDisplayIpcCodec.WriteRequestAsync(
                pipe, new SetBrightnessRequest(SessionDisplayIpc.ProtocolVersion,
                    requestId, PowerSource.PluggedIn, 70), token);
            SessionDisplayResponse response = await SessionDisplayIpcCodec.ReadResponseAsync(pipe, token);
            Assert.Equal(requestId, response.RequestId);
            Assert.Equal(SessionDisplayOperationStatus.Indeterminate, response.Status);
            Assert.Contains(scenario == "sibling-readback" ? "PanelChanged" : "DeadlineExceeded", response.Error);
            Assert.Equal(scenario == "sibling-readback" ? (byte?)null : 70, response.State.BrightnessPercent);

            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            Assert.Equal((byte)70, (await client.GetStateAsync(token)).BrightnessPercent);
            DisplaySettingsSnapshot blocked = await client.SetBrightnessAsync(PowerSource.PluggedIn, 10, token);
            Assert.False(blocked.OperationSucceeded);
            Assert.Contains("writes are paused", blocked.StatusMessage);
            Assert.Equal((byte)70, (await client.GetStateAsync(token)).BrightnessPercent);
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_CancellationDuringAdmissionLeavesTheBaseline()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, brightness: "stalled-admission");
        await host.WaitForReadyAsync(token);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));

        Task<DisplaySettingsSnapshot> apply = client.SetBrightnessAsync(
            PowerSource.PluggedIn, 70, cancellation.Token);
        await host.WaitForSignalAsync("BRIGHTNESS ADMISSION", token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.Equal((byte)40, await ObserveBrightnessAsync(client, token));
        await host.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task PrivateHost_PausesAfterClientDisappearsDuringSubmission()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, brightness: "stalled-write");
        await host.WaitForReadyAsync(token);
        using (NamedPipeClientStream pipe = Pipe(id))
        {
            await pipe.ConnectAsync(token);
            await SessionDisplayIpcCodec.WriteRequestAsync(
                pipe, new SetBrightnessRequest(SessionDisplayIpc.ProtocolVersion,
                    Guid.NewGuid(), PowerSource.PluggedIn, 70), token);
            await host.WaitForSignalAsync("BRIGHTNESS SUBMITTED", token);
        }

        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        Assert.Equal((byte)70, await ObserveBrightnessAsync(client, token));
        DisplaySettingsSnapshot blocked = await client.SetBrightnessAsync(PowerSource.PluggedIn, 10, token);
        Assert.False(blocked.OperationSucceeded);
        Assert.Contains("writes are paused", blocked.StatusMessage);
        Assert.Equal((byte)70, (await client.GetStateAsync(token)).BrightnessPercent);
        await host.StopAsync(token);
    }

    private static async Task<byte> ObserveBrightnessAsync(
        SessionDisplaySettingsAdapter client,
        CancellationToken token)
    {
        // A cancelled caller can leave the one worker occupied briefly.
        while (true)
        {
            DisplaySettingsSnapshot state = await client.GetStateAsync(token);
            if (state.BrightnessPercent is byte percent)
            {
                Assert.True(state.OperationSucceeded);
                return percent;
            }

            await Task.Delay(25, token);
        }
    }

    [RustSessionAgentFact]
    public async Task PrivateHost_ShutsDownWithAnOutstandingScriptedSubmission()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, brightness: "stalled-write");
        await host.WaitForReadyAsync(token);
        using var pipe = Pipe(id);
        await pipe.ConnectAsync(token);
        await SessionDisplayIpcCodec.WriteRequestAsync(
            pipe, new SetBrightnessRequest(SessionDisplayIpc.ProtocolVersion,
                Guid.NewGuid(), PowerSource.PluggedIn, 70), token);
        await host.WaitForSignalAsync("BRIGHTNESS SUBMITTED", token);

        using var stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        stopDeadline.CancelAfter(TimeSpan.FromSeconds(1));
        await host.StopAsync(stopDeadline.Token);
        await AssertClosedAsync(pipe, token);
    }

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_DistinguishesStaleIdentityAndNativeErrorsFromUnavailable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        foreach ((string scenario, string expected) in new[]
        {
            ("stale-target", "Display read failed: TopologyChanging."),
            ("native-error", "Display read failed: Native { operation: ActivePaths, status: 5 }."),
            ("hdr-error", "HDR read failed: Native { status: 50 }."),
        })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario);
            await host.WaitForReadyAsync(token);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));

            DisplaySettingsSnapshot result = await client.GetStateAsync(token);
            Assert.False(result.OperationSucceeded);
            Assert.False(result.IsAvailable);
            Assert.Null(result.DisplayName);
            Assert.Null(result.BitsPerColorChannel);
            Assert.Null(result.SelectedRefreshRateKey);
            Assert.Empty(result.RefreshRateOptions);
            Assert.False(result.IsHdrEnabled);
            Assert.Equal(expected, result.StatusMessage);
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task PrivateHost_RefusesSecondOwnerAndRecoversFromRejectedAndCanceledConnections()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        await using var host = Host.Start(id);
        await host.WaitForReadyAsync(token);

        await using (var duplicate = Host.Start(id, mode: "resume-display"))
        {
            await duplicate.AssertRejectedAsync(token);
        }

        using (var early = Pipe(id))
        {
            await early.ConnectAsync(token);
        }

        using (var malformed = Pipe(id))
        {
            await malformed.ConnectAsync(token);
            byte[] prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, SessionDisplayIpc.MaximumMessageBytes + 1);
            await malformed.WriteAsync(prefix, token);
            await AssertClosedAsync(malformed, token);
        }

        using (var anonymous = Pipe(id, TokenImpersonationLevel.Anonymous))
        {
            await anonymous.ConnectAsync(token);
            await SessionDisplayIpcCodec.WriteRequestAsync(anonymous,
                new QueryDisplayRequest(SessionDisplayIpc.ProtocolVersion, Guid.NewGuid()), token);
            await AssertClosedAsync(anonymous, token);
        }

        using (var stalled = Pipe(id))
        {
            await stalled.ConnectAsync(token);
            await stalled.WriteAsync(new byte[] { 1 }, token);
            await AssertClosedAsync(stalled, token);
        }
        Assert.True((await client.GetStateAsync(token)).OperationSucceeded);

        using (var pending = Pipe(id))
        {
            await pending.ConnectAsync(token);
            await pending.WriteAsync(new byte[] { 1 }, token);
            await host.StopAsync(token);
            await AssertClosedAsync(pending, token);
        }

        await using var restarted = Host.Start(id, mode: "resume-display");
        await restarted.WaitForReadyAsync(token);
        Assert.True((await client.GetStateAsync(token)).IsAvailable);
        await restarted.StopAsync(token);
    }

    private static string PipeName(string id) => $"OpenBlade.Rust.Session.Display.Dev.{id}";

    private static NamedPipeClientStream Pipe(
        string id,
        TokenImpersonationLevel impersonationLevel = TokenImpersonationLevel.Identification) =>
        new(".", PipeName(id), PipeDirection.InOut, PipeOptions.Asynchronous, impersonationLevel);

    private static async Task AssertClosedAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        try
        {
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], token));
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 109 or 232 or 233)
        {
            // Windows may report retirement as a pipe error instead of EOF.
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> standardError;
        private readonly bool gpu;
        private readonly bool ownsScratch;
        private readonly string? scratch;

        private Host(Process process, bool gpu, string? scratch, bool ownsScratch)
        {
            this.process = process;
            this.gpu = gpu;
            this.scratch = scratch;
            this.ownsScratch = ownsScratch;
            standardError = process.StandardError.ReadToEndAsync();
        }

        internal static Host Start(
            string id, string scenario = "available", string? brightness = null, bool gpu = false,
            string mode = "foreground", string[]? arguments = null, bool requireGpuClose = false)
        {
            string path = Environment.GetEnvironmentVariable(requireGpuClose
                ? RustSessionAgentFactAttribute.GpuClosePathVariable
                : RustSessionAgentFactAttribute.PathVariable)!;
            Assert.True(Path.IsPathFullyQualified(path)
                && string.Equals(Path.GetFileName(path), "openblade-rust-session-agent.exe",
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(path), $"Expected an absolute path to the Rust Session Agent: {path}");
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            string? scratch = gpu || mode == "audio-foreground" ? null : DisplayFixtureDirectory(id);
            bool createsScratch = mode == "foreground" || mode == "integrated" && scenario == "new";
            bool ownsScratch = createsScratch && scratch is not null && !Directory.Exists(scratch);
            start.ArgumentList.Add(gpu ? "gpu-foreground" : mode);
            start.ArgumentList.Add(id);
            start.ArgumentList.Add(scenario);
            if (brightness is not null)
            {
                start.ArgumentList.Add(brightness);
            }
            foreach (string argument in arguments ?? [])
            {
                start.ArgumentList.Add(argument);
            }
            try
            {
                return new Host(Process.Start(start)!, gpu, scratch, ownsScratch);
            }
            catch
            {
                if (ownsScratch && Directory.Exists(scratch))
                {
                    Directory.Delete(scratch!, recursive: true);
                }
                throw;
            }
        }

        internal Task WaitForReadyAsync(CancellationToken token) => WaitForSignalAsync("READY", token);

        internal async Task WaitForSignalAsync(string signal, CancellationToken token)
        {
            string? line = await process.StandardOutput.ReadLineAsync(token);
            Assert.True(line == signal, line ?? await standardError.WaitAsync(token));
        }

        internal async Task AssertRejectedAsync(CancellationToken token)
        {
            await process.WaitForExitAsync(token);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Null(await process.StandardOutput.ReadLineAsync(token));
        }

        internal async Task ReportFocusAsync(bool active, CancellationToken token)
        {
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { active ? (byte)'a' : (byte)'c' }, token);
            await process.StandardInput.BaseStream.FlushAsync(token);
        }

        internal async Task StopAsync(CancellationToken token)
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            Assert.True(process.ExitCode == 0, await standardError.WaitAsync(token));
        }

        internal async Task KillAsync(CancellationToken token)
        {
            process.Kill();
            await process.WaitForExitAsync(token);
        }

        public async ValueTask DisposeAsync()
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    if (gpu)
                    {
                        process.StandardInput.Close();
                    }
                    else
                    {
                        process.Kill();
                    }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeout.Token);
                }
            }
            if (ownsScratch && Directory.Exists(scratch))
            {
                Directory.Delete(scratch!, recursive: true);
            }
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RustSessionAgentFactAttribute : FactAttribute
{
    internal const string PathVariable = "OPENBLADE_RUST_SESSION_AGENT_EXE";

    internal const string GpuClosePathVariable = "OPENBLADE_RUST_GPU_CLOSE_FIXTURE_EXE";

    public RustSessionAgentFactAttribute(bool requireService = false, bool requireGpuClose = false)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable)))
        {
            Skip = $"Requires Windows and an explicit {PathVariable} path.";
        }
        else if (requireGpuClose && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GpuClosePathVariable)))
        {
            Skip = $"Requires an explicit {GpuClosePathVariable} path.";
        }
        else if (requireService && string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(RustReadOnlyHostFactAttribute.PathVariable)))
        {
            Skip = $"Requires an explicit {RustReadOnlyHostFactAttribute.PathVariable} path.";
        }
    }
}
