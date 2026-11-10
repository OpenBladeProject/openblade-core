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

            // Both claims and the expected service child must be admitted before readiness.
            await using (var occupied = Host.Start(id, "available", gpu: true))
            {
                await occupied.WaitForReadyAsync(token);
                await using var refused = Start("new", "available");
                await refused.AssertRejectedAsync(token);
                Assert.False(File.Exists(journal));
                Assert.Equal(SessionGpuOperationStatus.Succeeded, (await gpu.QueryAsync(token)).Status);
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

            await using (var resumed = Start("resume", "available"))
            {
                await resumed.WaitForReadyAsync(token);
                var displayQuery = display.GetStateAsync(token);
                var gpuQuery = gpu.QueryAsync(token);
                await Task.WhenAll(displayQuery, gpuQuery);
                Assert.Equal(FactoryProfile, (await displayQuery).SelectedColorProfileKey);
                Assert.Equal(SessionGpuOperationStatus.Succeeded, (await gpuQuery).Status);
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

            Host Start(string mode, string gpuScenario, int? servicePid = null) =>
                Host.Start(id, mode, mode: "integrated",
                    arguments: [gpuScenario, serviceId, (servicePid ?? service.ProcessId).ToString(CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N")]);

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
