using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustReadOnlyHostTests
{
    [RustReadOnlyHostFact]
    public async Task TrayClient_IntegratedApplicationLeaseSelectsAndReconcilesThroughScopedControls()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken token = timeout.Token;
        string endpoint = Guid.NewGuid().ToString("N");
        const string mode = "foreground-integrated-application";
        await using var host = DevHost.Start(endpoint, mode);
        await host.ExpectLineAsync("START_PENDING", token);
        (string rootId, _) = await host.ReadFixtureRootAsync(token);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", $"openblade-durable-host-{rootId}");
        using SafeFileHandle pin = OpenFixtureDirectory(root);
        Assert.False(pin.IsInvalid);
        try
        {
            await host.WaitUntilReadyAsync(token);
            await using var client = new ServiceClient(PipeName(endpoint));
            await WaitAsync(status => status.Message == "Renderer frame in flight.");
            ServiceStatusSnapshot initial = await client.GetStatusAsync(token);
            ApplicationProfileDefinition rule = Assert.Single(initial.Profiles.ApplicationProfiles);
            byte[][] prior = ConfigurationNames.Select(name => File.ReadAllBytes(Path.Combine(root, name))).ToArray();
            Guid instance = Guid.NewGuid();
            var manual = new ManualOverride<PerformancePreset>(
                PerformancePreset.Balanced, OverrideLifetime.UntilPowerSourceChange);

            await AssertIdleAsync(initial, 1, null, initial.Profiles.PluggedIn.Performance,
                PerformancePreset.Balanced);
            await AssertIdleAsync(initial, 2, @"C:\Other\Game.exe", initial.Profiles.PluggedIn.Performance,
                PerformancePreset.Balanced);

            Assert.Equal(ControlOperationStatus.Stored, (await ReportAsync(3, rule.ExecutablePath)).Status);
            var selected = await client.GetStatusAsync(token);
            AssertSelection(selected, rule.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Balanced);
            var renewal = await ReportAsync(4, rule.ExecutablePath);
            Assert.Equal(ControlOperationStatus.Stored, renewal.Status);
            Assert.Equal("The active application profile lease was renewed.", renewal.Message);
            var renewed = await client.GetStatusAsync(token);
            Assert.True(renewed.Revision > selected.Revision);
            Assert.Equal(renewal.Message, renewed.Message);
            AssertSelection(renewed, rule.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Balanced);
            Assert.Equal(ControlOperationStatus.Blocked,
                (await client.UpdateProfilesAsync(initial.Profiles with { ApplicationProfiles = [] }, token)).Status);
            AssertPrimaryBytes(root, prior);
            var profiles = initial.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) };
            Assert.Equal(ControlOperationStatus.Stored, (await client.UpdateProfilesAsync(profiles, token)).Status);
            var stored = await client.GetStatusAsync(token);
            Assert.Equal(75, stored.Profiles.BatteryPolicy.ChargeLimitPercent);
            AssertSelection(stored, rule.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Balanced);
            byte[][] saved = ConfigurationNames.Select(name => File.ReadAllBytes(Path.Combine(root, name))).ToArray();
            Assert.Equal(prior[1], saved[1]);
            AssertCommitHashes(saved);

            var applied = await AdvanceAsync();
            Assert.Contains("confirmed by readback", applied.Message);
            AssertSelection(applied, rule.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Performance);
            Assert.True(applied.EffectiveLaptopBehavior.GamingModeEnabled);

            Assert.Equal(ControlOperationStatus.Applied, (await client.SetPerformanceOverrideAsync(manual, token)).Status);
            AssertSelection(await client.GetStatusAsync(token), rule.Id, PerformancePreset.Balanced,
                SettingValueSource.TemporaryOverride, PerformancePreset.Balanced);
            Assert.Equal(ControlOperationStatus.Applied, (await client.SetPerformanceOverrideAsync(null, token)).Status);
            AssertSelection(await client.GetStatusAsync(token), rule.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Performance);
            Assert.Equal(ControlOperationStatus.Applied, (await client.SetPerformanceOverrideAsync(manual, token)).Status);

            var battery = await AdvanceAsync();
            Assert.Equal(PowerSource.Battery, battery.ActivePowerSource);
            Assert.Null(battery.PerformanceOverride);
            AssertSelection(battery, rule.Id, PerformancePreset.BatteryBalanced,
                SettingValueSource.ApplicationProfile, PerformancePreset.Balanced);
            Assert.Null(battery.EffectiveLaptopBehavior.GamingModeEnabled);

            var inactive = await AdvanceAsync();
            Assert.Equal(SessionControlAccess.InactiveSession, inactive.ControlAccess);
            Assert.True(inactive.WritesBlocked);
            var refused = await Assert.ThrowsAsync<InvalidDataException>(() => ReportAsync(999, null));
            Assert.Contains("active local interactive Windows session", refused.Message);
            Assert.Equal(inactive.Revision, (await client.GetStatusAsync(token)).Revision);
            Assert.Equal(ControlOperationStatus.Blocked,
                (await client.SetBatteryPolicyAsync(BatteryPolicy.Create(70), token)).Status);
            var reactivated = await AdvanceAsync();
            Assert.Equal(SessionControlAccess.Active, reactivated.ControlAccess);
            var clear = await ReportAsync(5, null);
            Assert.Equal(ControlOperationStatus.Stored, clear.Status);
            Assert.Equal("The active application profile was recorded.", clear.Message);
            var cleared = await client.GetStatusAsync(token);
            Assert.True(cleared.Revision > reactivated.Revision);
            Assert.Equal(clear.Message, cleared.Message);
            AssertSelection(cleared, null, initial.Profiles.Battery.Performance,
                SettingValueSource.PowerProfile, PerformancePreset.Balanced);
            Assert.Equal(ControlOperationStatus.Stored, (await ReportAsync(6, rule.ExecutablePath)).Status);

            var planned = await AdvanceAsync();
            Assert.Equal(PowerSource.ExternalUnknown, planned.ActivePowerSource);
            Assert.True(planned.WritesBlocked);
            Assert.Equal(PerformancePreset.Balanced, planned.ConfirmedState?.Performance);
            var full = await AdvanceAsync();
            Assert.Equal(PowerSource.PluggedIn, full.ActivePowerSource);
            Assert.Contains("confirmed by readback", full.Message);
            AssertSelection(full, rule.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Performance);

            var expired = await WaitAsync(status => status.Message?.Contains("lease expired", StringComparison.Ordinal) == true);
            AssertSelection(expired, null, initial.Profiles.PluggedIn.Performance,
                SettingValueSource.PowerProfile, PerformancePreset.Performance);
            Assert.Contains("stale", (await ReportAsync(6, rule.ExecutablePath)).Message);
            Assert.Equal(expired.Revision, (await client.GetStatusAsync(token)).Revision);

            var uncertain = await AdvanceAsync();
            Assert.Contains("write is unconfirmed", uncertain.Message);
            Assert.True(uncertain.WritesBlocked);
            Assert.Equal(DeviceSubsystem.Performance, Assert.Single(uncertain.UnconfirmedWrites));
            AssertSelection(uncertain, null, initial.Profiles.PluggedIn.Performance,
                SettingValueSource.PowerProfile, PerformancePreset.Performance);
            await AssertIdleAsync(uncertain, 7, null, initial.Profiles.PluggedIn.Performance,
                PerformancePreset.Performance);
            Assert.Equal(ControlOperationStatus.Stored, (await ReportAsync(8, rule.ExecutablePath)).Status);
            var fenced = await AdvanceAsync();
            Assert.Contains("awaits read-only reconciliation", fenced.Message);
            Assert.Equal(DeviceSubsystem.Performance, Assert.Single(fenced.UnconfirmedWrites));
            AssertSelection(fenced, rule.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Performance);
            var settled = await AdvanceAsync();
            Assert.Empty(settled.UnconfirmedWrites);
            AssertSelection(settled, rule.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Balanced);
            Assert.Equal(ControlOperationStatus.Stored, (await ReportAsync(9, null)).Status);

            var off = new ManualOverride<LightingSettings>(LightingSettingsFactory.Off(), OverrideLifetime.Persistent);
            Task<ControlOperationResult> applyingOff = client.SetLightingOverrideAsync(off, token);
            await WaitAsync(status => status.Message == "Frame drained. Renderer port still owned.");
            Assert.Equal(ControlOperationStatus.Stored, (await ReportAsync(10, rule.ExecutablePath)).Status);
            var draining = await client.GetStatusAsync(token);
            AssertSelection(draining, rule.Id, PerformancePreset.Performance,
                SettingValueSource.ApplicationProfile, PerformancePreset.Balanced);
            Assert.Equal(LightingRendererOwnershipState.Spectrum, draining.LightingRendererOwnership);
            Assert.Null(draining.ConfirmedState?.Lighting);
            Assert.True(draining.WritesBlocked);
            Assert.False(applyingOff.IsCompleted);
            AssertPrimaryBytes(root, saved);

            await host.RequestStopAsync(token);
            await host.ExpectLineAsync("STOP_PENDING", token);
            await Assert.ThrowsAnyAsync<IOException>(async () => await applyingOff);
            await host.ReleaseRendererAsync(token);
            await host.ExpectLineAsync("STOPPED", token);
            await host.StopAsync(token);
            AssertPrimaryBytes(root, saved);

            Task<ControlOperationResult> ReportAsync(long sequence, string? path) =>
                client.ReportApplicationContextAsync(
                    new ReportApplicationContextRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), instance, sequence, path),
                    token);

            async Task AssertIdleAsync(
                ServiceStatusSnapshot before,
                long sequence,
                string? path,
                PerformancePreset desired,
                PerformancePreset observed)
            {
                var idle = await ReportAsync(sequence, path);
                Assert.Equal(ControlOperationStatus.Stored, idle.Status);
                Assert.Equal("No application profile is active.", idle.Message);
                var stale = await ReportAsync(sequence, rule.ExecutablePath);
                Assert.Equal(ControlOperationStatus.Stored, stale.Status);
                Assert.Contains("stale", stale.Message);

                var after = await client.GetStatusAsync(token);
                Assert.Equal(before.Revision, after.Revision);
                Assert.Equal(before.Message, after.Message);
                Assert.Equal(before.WritesBlocked, after.WritesBlocked);
                Assert.Equal(before.UnconfirmedWrites.ToArray(), after.UnconfirmedWrites.ToArray());
                AssertSelection(after, null, desired, SettingValueSource.PowerProfile, observed);
            }

            async Task<ServiceStatusSnapshot> AdvanceAsync()
            {
                await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
                var previous = await ReadStatusAsync(watch, subscriptionOpened: true);
                await host.AdvanceAsync(token);
                var current = await ReadStatusAsync(watch, subscriptionOpened: false);
                Assert.True(current.Revision > previous.Revision);
                return current;
            }

            async Task<ServiceStatusSnapshot> WaitAsync(Func<ServiceStatusSnapshot, bool> predicate)
            {
                await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
                var status = await ReadStatusAsync(watch, subscriptionOpened: true);
                while (!predicate(status))
                {
                    status = await ReadStatusAsync(watch, subscriptionOpened: false);
                }

                return status;
            }
        }
        finally
        {
            await host.DisposeAsync();
            foreach (string file in Directory.EnumerateFiles(root))
            {
                File.Delete(file);
            }

            pin.Dispose();
            Directory.Delete(root);
        }
    }

    private static void AssertSelection(
        ServiceStatusSnapshot status,
        Guid? application,
        PerformancePreset desired,
        SettingValueSource source,
        PerformancePreset observed)
    {
        Assert.Equal(application, status.ActiveApplicationProfileId);
        Assert.Equal(desired, status.DesiredState.Performance.Value);
        Assert.Equal(source, status.DesiredState.Performance.Source);
        Assert.Equal(observed, status.ConfirmedState?.Performance);
    }
}
