using System.Globalization;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustSessionAgentTests
{
    [RustSessionAgentFact(requireService: true)]
    public async Task SharedSessionAgent_IsolatesReporterGpuAndDurableDisplayState()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = timeout.Token;
        string serviceId = Guid.NewGuid().ToString("N");
        await using var service = RustReadOnlyHostTests.DevHost.Start(serviceId, "foreground-integrated-application");
        await service.ExpectLineAsync("START_PENDING", token);
        (string rootId, _) = await service.ReadFixtureRootAsync(token);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", $"openblade-durable-host-{rootId}");
        using var pin = RustReadOnlyHostTests.OpenFixtureDirectory(root);
        Assert.False(pin.IsInvalid);
        try
        {
            await service.WaitUntilReadyAsync(token);
            await using var serviceClient = new ServiceClient($"OpenBlade.Rust.ReadOnly.Dev.{serviceId}");
            string id = Guid.NewGuid().ToString("N");
            string journal = DisplayJournalPath(id);
            var display = new SessionDisplaySettingsAdapter(PipeName(id));
            var gpu = new SessionGpuAppAdapter(GpuPipeName(id));
            var initial = await WaitAsync(status => status.Message == "Renderer frame in flight.");
            Guid profile = Assert.Single(initial.Profiles.ApplicationProfiles).Id;

            // Endpoint claims and the expected service child must be admitted before readiness.
            await using (var occupied = Host.Start(id, "available", gpu: true))
            {
                await occupied.WaitForReadyAsync(token);
                await using var refused = Start("new", "available");
                await refused.AssertRejectedAsync(token);
                Assert.False(File.Exists(journal));
                Assert.Equal(SessionGpuOperationStatus.Succeeded, (await gpu.QueryAsync(token)).Status);
                await occupied.StopAsync(token);
            }

            await using (var occupied = Host.Start(id, mode: "battery-foreground"))
            {
                await occupied.WaitForReadyAsync(token);
                await using var refused = Start("new", "available", battery: true);
                await refused.AssertRejectedAsync(token);
                Assert.False(File.Exists(journal));
                await occupied.StopAsync(token);
            }

            await using (var occupied = Host.Start(id, mode: "audio-foreground"))
            {
                await occupied.WaitForReadyAsync(token);
                await using var refused = Start("new", "available", audio: true);
                await refused.AssertRejectedAsync(token);
                Assert.False(File.Exists(journal));
                Assert.Equal(ThxAudioAvailability.AvailableReadOnly,
                    (await ReadFixtureAudioAsync(id, ThxAudioEndpoint.Speakers, token)).Availability);
                await occupied.StopAsync(token);
            }

            await using (var wrongService = Start("new", "available", Environment.ProcessId))
            {
                await wrongService.AssertRejectedAsync(token);
                Assert.False(File.Exists(journal));
            }

            await using var original = Start("new", "stalled");
            await original.WaitForReadyAsync(token);
            string clean = await File.ReadAllTextAsync(journal, token);
            var failure = await display.SetColorProfileAsync(PowerSource.PluggedIn, NativeProfile, token);
            Assert.False(failure.OperationSucceeded);
            Assert.Contains("indeterminate", failure.StatusMessage);
            string pending = await File.ReadAllTextAsync(journal, token);
            Assert.Contains("\"operation\":\"icc\"", pending);

            Task<SessionGpuResponse> scan = gpu.QueryAsync(token);
            Assert.Equal(FactoryProfile, (await display.GetStateAsync(token)).SelectedColorProfileKey);
            await original.ReportFocusAsync(true, token);
            await WaitAsync(status => status.ActiveApplicationProfileId == profile);
            Assert.False(scan.IsCompleted);
            var gpuResult = await scan;
            Assert.Equal(SessionGpuOperationStatus.Failed, gpuResult.Status);
            Assert.Equal("The GPU inventory scan timed out.", gpuResult.Error);
            Assert.Empty(gpuResult.Apps);
            await AssertIccAndDisplayWritesFencedAsync(display, token);
            Assert.Equal(pending, await File.ReadAllTextAsync(journal, token));

            await AdvanceAsync(status => status.Message?.Contains("confirmed by readback") == true);
            await AdvanceAsync(status => status.ActivePowerSource == PowerSource.Battery);
            await AdvanceAsync(status => status.ControlAccess == SessionControlAccess.InactiveSession);
            await AdvanceAsync(status => status.ControlAccess == SessionControlAccess.Active);
            await AdvanceAsync(status => status.ActivePowerSource == PowerSource.ExternalUnknown);
            await AdvanceAsync(status => status.ActivePowerSource == PowerSource.PluggedIn);
            await original.ReportFocusAsync(false, token);
            await WaitAsync(status => status.ActiveApplicationProfileId is null);
            var uncertain = await AdvanceAsync(status => status.UnconfirmedWrites.Count > 0);
            AssertServiceFence(uncertain);

            await original.ReportFocusAsync(true, token);
            AssertServiceFence(await WaitAsync(status => status.ActiveApplicationProfileId == profile));
            await original.ReportFocusAsync(false, token);
            AssertServiceFence(await WaitAsync(status => status.ActiveApplicationProfileId is null));
            Assert.Equal(pending, await File.ReadAllTextAsync(journal, token));
            await original.StopAsync(token);

            await using (var resumed = Start("resume", "available", battery: true, audio: true))
            {
                await resumed.WaitForReadyAsync(token);
                var displayQuery = display.GetStateAsync(token);
                var gpuQuery = gpu.QueryAsync(token);
                var battery = new SessionBatteryReportAdapter(BatteryPipeName(id));
                Task<WindowsBatteryReport> batteryQuery = battery.ReadAsync(token);
                Task<SessionAudioResponse> audioQuery = ReadFixtureAudioAsync(id, ThxAudioEndpoint.Speakers, token);
                await Task.WhenAll(displayQuery, gpuQuery, batteryQuery, audioQuery);
                SessionAudioResponse audio = await audioQuery;
                Assert.Equal(ThxAudioOperationStatus.Succeeded, audio.Status);
                Assert.Equal(ThxAudioAvailability.AvailableReadOnly, audio.Availability);
                Assert.True(Assert.IsType<ThxAudioState>(audio.State).SpatialEnabled);
                using (var pipe = AudioFixturePipe(id))
                {
                    await pipe.ConnectAsync(token);
                    await SessionAudioIpcCodec.WriteRequestAsync(pipe,
                        new SetThxSpatialRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
                            ThxAudioEndpoint.Speakers, false), token);
                    await AssertClosedAsync(pipe, token);
                }
                Assert.True(Assert.IsType<ThxAudioState>(
                    (await ReadFixtureAudioAsync(id, ThxAudioEndpoint.Speakers, token)).State).SpatialEnabled);
                WindowsBatteryReport report = await batteryQuery;
                Assert.Equal(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero), report.GeneratedAtUtc);
                Assert.Equal(50000, report.Status.FullChargeCapacityMilliwattHours);
                Assert.Equal(3, report.Intervals.Count);
                Assert.Equal(FactoryProfile, (await displayQuery).SelectedColorProfileKey);
                SessionGpuResponse grouped = await gpuQuery;
                Assert.Equal(SessionGpuOperationStatus.Succeeded, grouped.Status);
                SessionGpuApp owner = Assert.Single(grouped.Apps, app => app.ProcessId == 41);
                Assert.Equal(15.0, owner.UtilizationPercent);
                Assert.DoesNotContain(grouped.Apps, app => app.ProcessId == 46);
                AssertRejectedGpuClose(await gpu.CloseAsync([owner], force: false, token));
                AssertRejectedGpuClose(await gpu.CloseAsync([owner], force: true, token));
                await AssertIccAndDisplayWritesFencedAsync(display, token);
                Assert.Equal(pending, await File.ReadAllTextAsync(journal, token));
                await resumed.ReportFocusAsync(true, token);
                AssertServiceFence(await WaitAsync(status => status.ActiveApplicationProfileId == profile));
                await resumed.ReportFocusAsync(false, token);
                AssertServiceFence(await WaitAsync(status => status.ActiveApplicationProfileId is null));
                await resumed.StopAsync(token);
            }

            await using (var reconciled = Start("reconcile", "available"))
            {
                await reconciled.WaitForReadyAsync(token);
                Assert.Equal(FactoryProfile, (await display.GetStateAsync(token)).SelectedColorProfileKey);
                // The next connection observes retirement after delivery of the read-only result.
                Assert.Equal(FactoryProfile, (await display.GetStateAsync(token)).SelectedColorProfileKey);
                Assert.Equal(clean, await File.ReadAllTextAsync(journal, token));
                AssertServiceFence(await serviceClient.GetStatusAsync(token));
                await reconciled.StopAsync(token);
            }

            await service.RequestStopAsync(token);
            await service.ExpectLineAsync("STOP_PENDING", token);
            await service.ReleaseRendererAsync(token);
            await service.ExpectLineAsync("STOPPED", token);
            await service.StopAsync(token);

            Host Start(string mode, string gpuScenario, int? servicePid = null, bool battery = false, bool audio = false)
            {
                string[] arguments = [gpuScenario, serviceId, (servicePid ?? service.ProcessId).ToString(CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N")];
                if (battery)
                {
                    arguments = [.. arguments, "battery-available"];
                }
                if (audio)
                {
                    arguments = [.. arguments, "audio-available"];
                }
                return Host.Start(id, mode, mode: "integrated", arguments: arguments);
            }

            async Task<ServiceStatusSnapshot> AdvanceAsync(Func<ServiceStatusSnapshot, bool> predicate)
            {
                var before = await serviceClient.GetStatusAsync(token);
                await service.AdvanceAsync(token);
                return await WaitAsync(status => status.Revision > before.Revision && predicate(status));
            }

            async Task<ServiceStatusSnapshot> WaitAsync(Func<ServiceStatusSnapshot, bool> predicate)
            {
                while (true)
                {
                    var status = await serviceClient.GetStatusAsync(token);
                    if (predicate(status))
                    {
                        return status;
                    }
                    await Task.Delay(25, token);
                }
            }
        }
        finally
        {
            await service.DisposeAsync();
            foreach (string file in Directory.EnumerateFiles(root))
            {
                File.Delete(file);
            }
            pin.Dispose();
            Directory.Delete(root);
        }
    }

    private static void AssertServiceFence(ServiceStatusSnapshot status)
    {
        Assert.True(status.WritesBlocked);
        Assert.Equal(DeviceSubsystem.Performance, Assert.Single(status.UnconfirmedWrites));
        Assert.Equal(PerformancePreset.Performance, status.ConfirmedState?.Performance);
    }
}
