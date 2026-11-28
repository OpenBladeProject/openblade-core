using System.Globalization;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustSessionAgentTests
{
    [RustSessionAgentFact]
    public async Task TrayDisplayClient_ConfirmsHdrPreferenceInBothDirections()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id);
        await host.WaitForReadyAsync(token);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));

        AssertHdrOracle("hdr_user_enabled_with_policy_limit", await client.GetStateAsync(token));
        AssertHdrOracle("hdr_user_enabled_with_policy_limit",
            await client.SetHdrEnabledAsync(PowerSource.PluggedIn, true, token)); // Confirmed no-op.
        AssertHdrOracle("hdr_supported_disabled",
            await client.SetHdrEnabledAsync(PowerSource.PluggedIn, false, token));
        AssertHdrOracle("hdr_supported_disabled", await client.GetStateAsync(token));
        AssertHdrOracle("hdr_user_enabled_with_policy_limit",
            await client.SetHdrEnabledAsync(PowerSource.PluggedIn, true, token));
        AssertHdrOracle("hdr_user_enabled_with_policy_limit", await client.GetStateAsync(token));
        await host.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task PrivateHost_RejectsHdrWithoutFreshAdmission()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        foreach (var (scenario, power, status, available, supported, error) in new[]
        {
            ("available", PowerSource.Battery, SessionDisplayOperationStatus.Blocked, true, true, "expected power"),
            ("hdr-stale-power", PowerSource.PluggedIn, SessionDisplayOperationStatus.Blocked, true, true, "became stale"),
            ("hdr-stale-target", PowerSource.PluggedIn, SessionDisplayOperationStatus.Blocked, true, true, "display changed"),
            ("hdr-inactive", PowerSource.PluggedIn, SessionDisplayOperationStatus.Blocked, true, true, "not active"),
            ("unavailable", PowerSource.PluggedIn, SessionDisplayOperationStatus.Failed, false, false, "unavailable"),
            ("hdr-unsupported", PowerSource.PluggedIn, SessionDisplayOperationStatus.Failed, true, false, "not supported"),
            ("hdr-error", PowerSource.PluggedIn, SessionDisplayOperationStatus.Failed, false, false, "status: 50"),
        })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario);
            await host.WaitForReadyAsync(token);
            SessionDisplayResponse response = await SetHdrOnPipeAsync(id, power, false, token);
            Assert.Equal(status, response.Status);
            Assert.Contains(error, response.Error);
            Assert.Equal(available, response.State.IsAvailable);
            Assert.Equal(supported, response.State.HdrSupported);
            Assert.Equal(supported, response.State.HdrEnabled);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            Assert.Equal(supported, (await client.GetStateAsync(token)).IsHdrEnabled);
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task PrivateHost_LeavesUncertainHdrPendingAcrossReadOnlyRecovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        foreach (var (scenario, error, observed) in new[]
        {
            ("hdr-submission-error", "native result", false),
            ("hdr-sibling-readback", "exact target", false),
            ("hdr-readback-mismatch", "did not confirm", true),
        })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario);
            await host.WaitForReadyAsync(token);
            // A redundant call would activate the scripted submission fault.
            SessionDisplayResponse noOp = await SetHdrOnPipeAsync(id, PowerSource.PluggedIn, true, token);
            Assert.Equal(SessionDisplayOperationStatus.Succeeded, noOp.Status);
            Assert.True(noOp.State.HdrEnabled);

            SessionDisplayResponse response = await SetHdrOnPipeAsync(id, PowerSource.PluggedIn, false, token);
            Assert.Equal(SessionDisplayOperationStatus.Indeterminate, response.Status);
            Assert.Contains(error, response.Error);
            Assert.True(response.State.HdrEnabled); // No sibling or requested value may replace the last observation.
            Assert.False(response.State.ColorProfilesEnabled);

            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            AssertHdrOracle(observed ? "hdr_user_enabled_with_policy_limit" : "hdr_supported_disabled",
                await client.GetStateAsync(token));
            var blocked = await client.SetHdrEnabledAsync(PowerSource.PluggedIn, !observed, token);
            Assert.False(blocked.OperationSucceeded);
            Assert.Contains("writes are paused", blocked.StatusMessage);
            Assert.Equal(observed, (await client.GetStateAsync(token)).IsHdrEnabled);
            Assert.False((await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token)).OperationSucceeded);
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_HdrCancellationBeforeAndAfterSubmission()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        foreach (bool submitted in new[] { false, true })
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, submitted ? "hdr-stalled-submission" : "hdr-stalled-admission");
            await host.WaitForReadyAsync(token);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            Task<DisplaySettingsSnapshot> apply = client.SetHdrEnabledAsync(PowerSource.PluggedIn, false, cancellation.Token);
            await host.WaitForSignalAsync(submitted ? "HDR SUBMITTED" : "HDR ADMISSION", token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
            Assert.Equal((byte)40, await ObserveBrightnessAsync(client, token)); // Wait for the one request slot.
            AssertHdrOracle(submitted ? "hdr_supported_disabled" : "hdr_user_enabled_with_policy_limit",
                await client.GetStateAsync(token));
            var brightness = await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token);
            Assert.Equal(!submitted, brightness.OperationSucceeded);
            await host.StopAsync(token);
        }
    }

    private static async Task<SessionDisplayResponse> SetHdrOnPipeAsync(
        string id, PowerSource power, bool enabled, CancellationToken token)
    {
        using var pipe = Pipe(id);
        await pipe.ConnectAsync(token);
        Guid requestId = Guid.NewGuid();
        await SessionDisplayIpcCodec.WriteRequestAsync(
            pipe, new SetHdrRequest(SessionDisplayIpc.ProtocolVersion, requestId, power, enabled), token);
        SessionDisplayResponse response = await SessionDisplayIpcCodec.ReadResponseAsync(pipe, token);
        Assert.Equal(requestId, response.RequestId);
        return response;
    }

    private static void AssertHdrOracle(string fixture, DisplaySettingsSnapshot actual)
    {
        // The managed WindowsDisplayNativeContractTests run the same rows through
        // WindowsDisplaySettingsController.ProjectAdvancedColor.
        using var stream = typeof(RustSessionAgentTests).Assembly
            .GetManifestResourceStream("display-advanced-color.csv")!;
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null && !line.StartsWith(fixture + ",", StringComparison.Ordinal))
        {
        }

        Assert.NotNull(line);
        string[] fields = line.Split(',');
        Assert.True(actual.OperationSucceeded, actual.StatusMessage);
        Assert.True(actual.IsAvailable);
        Assert.Equal(bool.Parse(fields[4]), actual.IsHdrSupported);
        Assert.Equal(bool.Parse(fields[5]), actual.IsHdrEnabled);
        Assert.Equal(int.Parse(fields[6], CultureInfo.InvariantCulture), actual.BitsPerColorChannel);
        Assert.Equal("Synthetic panel", actual.DisplayName);
        Assert.Empty(actual.ColorProfileOptions); // No SDR profile acquisition in this fixture.
        Assert.Null(actual.SelectedColorProfileKey);
    }
}
