using System.Security.Cryptography;
using System.Text;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Exact 0F43 accessory admission, independent of the laptop's identity.</summary>
public sealed class WindowsCoolingPadDeviceProvider : ICoolingPadDeviceProvider
{
    private int active;
    public CoolingPadConnectionState ObserveConnection() => CoolingPadDiscoveryFactory.CreateWindows().Observe();

    public async Task<ICoolingPadDevice?> OpenAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || Interlocked.CompareExchange(ref active, 1, 0) != 0) { return null; }
        Task<ICoolingPadDevice?> opening = Task.Run(OpenCore, CancellationToken.None);
        try
        {
            return await opening.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Late native opening is disposed; it can never turn into a detached write.
            _ = opening.ContinueWith(async task =>
            {
                if (task.IsCompletedSuccessfully && task.Result is { } device)
                { await device.DisposeAsync().ConfigureAwait(false); }
                _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            throw;
        }
    }

    private async Task<ICoolingPadDevice?> OpenCore()
    {
        CoolingPadControllerLease? ownership = null;
        IDisposable? reference = null;
        ValidationActor? actor = null;
        bool transferred = false;
        try
        {
            ownership = new CoolingPadControllerLease();
            reference = ownership.Acquire();
            if (reference is null) { return null; }
            var windowsNative = new WindowsHidNative();
            var opener = new WindowsCoolingPadHidSessionOpener(windowsNative);
            var guard = new CoolingPadAccessGuard(new RunningProcessSource(), new WindowsServiceSource(),
                opener.StillExact);
            var actors = new ValidationActorFactory(() => opener.Open(guard),
                (handle, lease) => new WindowsRazerHidSession(handle,
                    new OverlappedFeatureOperation(windowsNative), guard, TimeProvider.System, lease,
                    ControllerExclusivityPolicy.Strict, TimeSpan.FromMilliseconds(30), featureReportId: 0,
                    queryDiagnostics: FirmwareQueryDiagnostics.Process), TimeSpan.FromSeconds(3));
            var opened = await actors.OpenAsync(new ValidationRunContext()).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            { throw new CoolingPadOperationException($"Cooling-pad control is unavailable: {opened.Failure}.", false); }
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(opener.SelectedPath!.ToUpperInvariant())));
            var ownedActor = actor;
            var ownedReference = reference;
            var ownedLease = ownership;
            var device = new SourceBackedCoolingPadDevice(ownedActor, identity, async () =>
            {
                // Release cooperative ownership only after the native handle has actually retired.
                if (ownedActor.Session.Disposition != RazerCommandSessionDisposition.FullyDisposed) { return; }
                ownedReference.Dispose();
                ownedLease.Dispose();
                Interlocked.Exchange(ref active, 0);
                await Task.CompletedTask.ConfigureAwait(false);
            });
            actor = null;
            reference = null;
            ownership = null;
            transferred = true;
            return device;
        }
        finally
        {
            if (actor is not null) { await actor.DisposeAsync().ConfigureAwait(false); }
            if (reference is not null) { reference.Dispose(); }
            ownership?.Dispose();
            // Success has transferred ownership to the returned device.
            if (!transferred) { Interlocked.Exchange(ref active, 0); }
        }
    }

    internal static readonly HidHandleIdentity ExactIdentity = new(0x1532, 0x0F43, 0x000C, 1, 91, 0x0200);

    internal sealed class CoolingPadAccessGuard(IRunningProcessSource processes, IWindowsServiceSource services,
        Func<bool> identityAllowed) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check()
        {
            try
            {
                var ordinary = new WindowsHardwareAccessGuard(processes, services).Check();
                var conflicts = processes.GetProcessNames().Where(name =>
                    // The reviewed Windows pad plugin runs inside this host; this exclusion is accessory-specific.
                    name.Equals("FanControl", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("OpenBlade.Capture", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("USBPcapCMD", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("RazerAppEngine", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("SynapseAppEngine", StringComparison.OrdinalIgnoreCase));
                var vendor = services.GetServices().Where(service => service.State != WindowsServiceState.Stopped
                    && (service.ServiceName.Contains("Razer", StringComparison.OrdinalIgnoreCase)
                        || service.DisplayName.Contains("Razer", StringComparison.OrdinalIgnoreCase))).Select(service => service.ServiceName);
                if (!identityAllowed())
                { return new([], "Exact cooling-pad identity is unavailable."); }
                return new([.. ordinary.Conflicts, .. conflicts, .. vendor], ordinary.DetectionFailure);
            }
            catch { return new([], "Cooling-pad identity or controller detection failed."); }
        }
    }
}
