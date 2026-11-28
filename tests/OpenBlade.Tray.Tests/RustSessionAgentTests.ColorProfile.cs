using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustSessionAgentTests
{
    private const string FactoryProfile = "Synthetic Factory.icm";
    private const string NativeProfile = "Native.icm";

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_QueriesAndSetsSdrProfilesWithHdrPreferenceGating()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, "icc-available");
        await host.WaitForReadyAsync(token);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));

        var before = await client.GetStateAsync(token);
        Assert.True(before.OperationSucceeded);
        Assert.False(before.IsHdrEnabled);
        Assert.Equal(FactoryProfile, before.SelectedColorProfileKey);
        Assert.Contains(before.ColorProfileOptions, profile => profile.Key == NativeProfile);
        Assert.Equal((byte)40, before.BrightnessPercent);
        Assert.NotEmpty(before.RefreshRateOptions);
        var wireBefore = await SendColorProfileRequestAsync(id, requestId =>
            new QueryDisplayRequest(SessionDisplayIpc.ProtocolVersion, requestId), token);
        Assert.True(wireBefore.State.ColorProfilesEnabled);
        Assert.Equal(FactoryProfile, Assert.Single(wireBefore.State.ColorProfiles, profile => profile.IsActive).Id);

        var applied = await client.SetColorProfileAsync(PowerSource.PluggedIn, @"C:\synthetic\NATIVE.ICM", token);
        Assert.True(applied.OperationSucceeded, applied.StatusMessage);
        Assert.Equal(NativeProfile, applied.SelectedColorProfileKey);
        Assert.Equal(NativeProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);

        var brightness = await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token);
        Assert.True(brightness.OperationSucceeded);
        Assert.Equal(NativeProfile, brightness.SelectedColorProfileKey);
        var refresh = await client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", token);
        Assert.True(refresh.OperationSucceeded, refresh.StatusMessage);
        Assert.Equal(NativeProfile, refresh.SelectedColorProfileKey);

        var hdr = await client.SetHdrEnabledAsync(PowerSource.PluggedIn, true, token);
        Assert.True(hdr.IsHdrEnabled);
        Assert.Null(hdr.SelectedColorProfileKey);
        var gated = await SetColorOnPipeAsync(id, PowerSource.PluggedIn, NativeProfile, token);
        Assert.Equal(SessionDisplayOperationStatus.Blocked, gated.Status);
        var hdrQuery = await SendColorProfileRequestAsync(id, requestId =>
            new QueryDisplayRequest(SessionDisplayIpc.ProtocolVersion, requestId), token);
        Assert.False(hdrQuery.State.ColorProfilesEnabled);
        Assert.Null(hdrQuery.State.ActiveColorProfileId);
        Assert.Contains(hdrQuery.State.ColorProfiles, profile => profile.Id == NativeProfile && !profile.IsActive);
        Assert.Equal(NativeProfile, (await client.SetHdrEnabledAsync(PowerSource.PluggedIn, false, token)).SelectedColorProfileKey);
        await host.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task PrivateHost_RejectsIccWithoutFreshAdmissionAndKeepsOtherObservations()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        foreach (var (scenario, power, key, status, reason) in new[]
        {
            ("icc-unavailable", PowerSource.PluggedIn, NativeProfile, SessionDisplayOperationStatus.Failed, "configuration could not be confirmed"),
            ("icc-available", PowerSource.Battery, NativeProfile, SessionDisplayOperationStatus.Blocked, "expected power"),
            ("icc-inactive", PowerSource.PluggedIn, NativeProfile, SessionDisplayOperationStatus.Blocked, "active session"),
            ("icc-stale-power", PowerSource.PluggedIn, NativeProfile, SessionDisplayOperationStatus.Blocked, "became stale"),
            ("icc-stale-target", PowerSource.PluggedIn, FactoryProfile, SessionDisplayOperationStatus.Blocked, "display changed"),
            ("icc-stale-hdr", PowerSource.PluggedIn, NativeProfile, SessionDisplayOperationStatus.Blocked, "SDR preference"),
            ("icc-available", PowerSource.PluggedIn, "Unlisted.icm", SessionDisplayOperationStatus.Failed, "profile is unavailable"),
        })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario);
            await host.WaitForReadyAsync(token);
            SessionDisplayResponse result = await SetColorOnPipeAsync(id, power, key, token);
            Assert.Equal(status, result.Status);
            Assert.Contains(reason, result.Error);

            var observed = await SendColorProfileRequestAsync(id, requestId =>
                new QueryDisplayRequest(SessionDisplayIpc.ProtocolVersion, requestId), token);
            bool wcsAvailable = scenario != "icc-unavailable";
            Assert.Equal(SessionDisplayOperationStatus.Succeeded, observed.Status);
            Assert.True(observed.State.IsAvailable);
            Assert.True(observed.State.HdrSupported);
            Assert.False(observed.State.HdrEnabled);
            Assert.NotEmpty(observed.State.RefreshOptions);
            Assert.Equal((byte)40, observed.State.BrightnessPercent);
            Assert.Equal(wcsAvailable, observed.State.ColorProfilesEnabled);
            Assert.Equal(wcsAvailable ? FactoryProfile : null, observed.State.ActiveColorProfileId);
            if (!wcsAvailable)
            {
                Assert.Empty(observed.State.ColorProfiles);
            }
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task PrivateHost_DistinguishesExactIccRestorationFromUnresolvedSubmission()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        foreach (var (scenario, restored, observedProfile) in new[]
        {
            ("icc-restored", true, FactoryProfile),
            ("icc-submission-error", false, NativeProfile),
            ("icc-readback-mismatch", false, null),
        })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario);
            await host.WaitForReadyAsync(token);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            // The default setter is faulted in these fixtures. A verified no-op avoids it.
            Assert.True((await client.SetColorProfileAsync(PowerSource.PluggedIn, FactoryProfile, token)).OperationSucceeded);
            SessionDisplayResponse result = await SetColorOnPipeAsync(id, PowerSource.PluggedIn, NativeProfile, token);
            Assert.Equal(restored ? SessionDisplayOperationStatus.Failed : SessionDisplayOperationStatus.Indeterminate, result.Status);
            Assert.Contains(restored ? "was restored" : "writes are paused", result.Error);
            Assert.Equal(restored ? FactoryProfile : null, result.State.ActiveColorProfileId);
            Assert.Equal(observedProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
            Assert.Equal(restored, (await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token)).OperationSucceeded);
            if (!restored)
            {
                Assert.Equal(SessionDisplayOperationStatus.Blocked,
                    (await SetColorOnPipeAsync(id, PowerSource.PluggedIn, NativeProfile, token)).Status);
                Assert.False((await client.SetHdrEnabledAsync(PowerSource.PluggedIn, true, token)).OperationSucceeded);
                Assert.False((await client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", token)).OperationSucceeded);
            }
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_LostIccReplyPausesWritesAfterIndependentReadback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, "icc-stalled-reply");
        await host.WaitForReadyAsync(token);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        var pending = client.SetColorProfileAsync(PowerSource.PluggedIn, NativeProfile, cancelled.Token);
        await host.WaitForSignalAsync("ICC CONFIRMED", token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal((byte)40, await ObserveBrightnessAsync(client, token));
        Assert.Equal(NativeProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
        Assert.Equal(SessionDisplayOperationStatus.Blocked,
            (await SetColorOnPipeAsync(id, PowerSource.PluggedIn, FactoryProfile, token)).Status);
        Assert.False((await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token)).OperationSucceeded);
        await host.StopAsync(token);
    }

    private static Task<SessionDisplayResponse> SetColorOnPipeAsync(
        string id, PowerSource power, string key, CancellationToken token) =>
        SendColorProfileRequestAsync(id, requestId =>
            new SetColorProfileRequest(SessionDisplayIpc.ProtocolVersion, requestId, power, key), token);

    private static async Task<SessionDisplayResponse> SendColorProfileRequestAsync(
        string id, Func<Guid, SessionDisplayRequest> request, CancellationToken token)
    {
        using var pipe = Pipe(id);
        await pipe.ConnectAsync(token);
        Guid requestId = Guid.NewGuid();
        await SessionDisplayIpcCodec.WriteRequestAsync(pipe, request(requestId), token);
        SessionDisplayResponse response = await SessionDisplayIpcCodec.ReadResponseAsync(pipe, token);
        Assert.Equal(requestId, response.RequestId);
        return response;
    }
}
