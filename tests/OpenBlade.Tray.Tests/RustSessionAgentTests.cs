using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed class RustSessionAgentTests
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
            Assert.Null(result.BrightnessPercent);
            Assert.Null(result.SelectedColorProfileKey);
            Assert.Empty(result.ColorProfileOptions);
            Assert.Empty(result.SupportedBrightnessLevels);
            if (result.IsAvailable)
            {
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
                Assert.Null(result.DisplayName);
                Assert.Null(result.BitsPerColorChannel);
                Assert.Empty(result.RefreshRateOptions);
                Assert.False(result.IsHdrSupported);
                Assert.False(result.IsHdrEnabled);
                Assert.Equal("No active display topology is available.", result.StatusMessage);
            }

            DisplaySettingsSnapshot rejected = await client.SetHdrEnabledAsync(
                PowerSource.PluggedIn, enabled: true, token);
            Assert.False(rejected.OperationSucceeded);
            Assert.Equal(DisplaySettingsFailureKind.ControlsUnavailable, rejected.FailureKind);
            Assert.Equal(result.IsAvailable, (await client.GetStateAsync(token)).IsAvailable);
            await host.StopAsync(token);
        }
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

        await using (var duplicate = Host.Start(id))
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

        await using var restarted = Host.Start(id);
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

        private Host(Process process)
        {
            this.process = process;
            standardError = process.StandardError.ReadToEndAsync();
        }

        internal static Host Start(string id, string scenario = "available")
        {
            string path = Environment.GetEnvironmentVariable(RustSessionAgentFactAttribute.PathVariable)!;
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
            start.ArgumentList.Add("foreground");
            start.ArgumentList.Add(id);
            start.ArgumentList.Add(scenario);
            return new Host(Process.Start(start)!);
        }

        internal async Task WaitForReadyAsync(CancellationToken token)
        {
            string? line = await process.StandardOutput.ReadLineAsync(token);
            Assert.True(line == "READY", line ?? await standardError.WaitAsync(token));
        }

        internal async Task AssertRejectedAsync(CancellationToken token)
        {
            await process.WaitForExitAsync(token);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Null(await process.StandardOutput.ReadLineAsync(token));
        }

        internal async Task StopAsync(CancellationToken token)
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            Assert.True(process.ExitCode == 0, await standardError.WaitAsync(token));
        }

        public async ValueTask DisposeAsync()
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeout.Token);
                }
            }
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RustSessionAgentFactAttribute : FactAttribute
{
    internal const string PathVariable = "OPENBLADE_RUST_SESSION_AGENT_EXE";

    public RustSessionAgentFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable)))
        {
            Skip = $"Requires Windows and an explicit {PathVariable} path.";
        }
    }
}
