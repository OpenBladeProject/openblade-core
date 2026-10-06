using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class CpuTuningRuntimeTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeGetterPowerConsensusIsBoundedAndNeverSharedWithCommands(bool mutation)
    {
        var live = new LiveFixture(curve: true, power: true);
        CpuTuningController? controller = null;
        var context = await CreateContextAsync(live: live, controllerFactory: source =>
            controller = new(source, curveBackend: live.Curve, curveStore: live.CurveStore,
                powerBackend: live.Power, powerStore: live.PowerStore));
        context.Power.Power = PowerSource.ExternalUnknown;
        int samples = 0, getters = 0;
        context.Device.ReadAdapterPower = _ =>
        { samples++; return Task.FromResult(AdapterPowerState.FullPower330W); };
        CpuLiveGetterAdmission? previous = null;
        live.Curve!.OnObserve = token =>
        {
            Assert.False(previous?.Allows() ?? false);
            using var getter = controller!.BeginLiveGetter(token);
            Assert.NotNull(getter);
            int before = samples;
            for (int i = 0; i < 820; i++) { Assert.True(getter.Allows()); }
            Assert.Equal(2, samples - before);
            // An actual command/restoration callback cannot use the getter proof.
            before = samples;
            Assert.True(controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.CurveOptimizer, token)());
            Assert.Equal(2, samples - before);
            previous = getter; getters++;
            return Task.FromResult(new CpuLiveCurveBackendObservation(live.Curve.Context,
                new(0xFF55, 0x005D000B, 0xD50, live.Curve.Values.ToArray())));
        };
        if (mutation)
        {
            Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.SetCpuTuningAsync(
                CpuTuningTarget.LiveNow, new(null, new(true, -2)), () => true, default)).Status);
            int before = samples;
            Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.RestoreCpuTuningAsync(
                CpuTuningTarget.LiveNow, () => true, default)).Status);
            Assert.True(samples > before);
            Assert.All(live.Curve.Values, core => Assert.Equal(-1, core.Offset));
        }
        else
        {
            for (int i = 0; i < 2; i++)
            { Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.ObserveCpuTuningAsync(true, () => true, default)).Status); }
        }
        Assert.True(getters >= 2); Assert.False(previous!.Allows());
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("device")]
    [InlineData("session")]
    [InlineData("battery")]
    public async Task GetterRechecksRuntimeGuardsAndCannotRecoverAfterDrift(string drift)
    {
        var live = new LiveFixture(curve: true, power: false);
        CpuTuningController? controller = null; bool controlsSession = true;
        var context = await CreateContextAsync(live: live, controllerFactory: source =>
            controller = new(source, curveBackend: live.Curve, curveStore: live.CurveStore));
        context.Power.Power = PowerSource.ExternalUnknown;
        context.Device.ReadAdapterPower = _ => Task.FromResult(AdapterPowerState.FullPower330W);
        live.Curve!.OnObserve = token =>
        {
            using var getter = controller!.BeginLiveGetter(token);
            Assert.NotNull(getter); Assert.True(getter.Allows());
            if (drift == "generation") { context.Gate.Enter(); }
            if (drift == "device") { context.Provider.Available = false; }
            if (drift == "session") { controlsSession = false; }
            if (drift == "battery") { context.Power.Power = PowerSource.Battery; }
            Assert.False(getter.Allows());
            context.Provider.Available = true; controlsSession = true; context.Power.Power = PowerSource.ExternalUnknown;
            Assert.False(getter.Allows());
            return Task.FromResult(new CpuLiveCurveBackendObservation(null, null));
        };
        await context.Runtime.ObserveCpuTuningAsync(true, () => controlsSession, default);
        Assert.Empty(live.Curve.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableLiveObservationRetainsTransactionReasonInTypedResponseWithoutExtraQueries(bool outerRejected)
    {
        var live = new LiveFixture(curve: true, power: true);
        live.Curve!.Values = live.Curve.Values[..^1];
        var context = await CreateContextAsync(live: live);
        int powerReads = 0;
        if (outerRejected)
        {
            context.Power.OnCapture = () =>
            {
                // The runtime checks before and after the getter, then at the outer boundary.
                if (++powerReads == 3) { context.Power.Power = PowerSource.Battery; }
            };
        }
        var before = context.Runtime.GetStatus();
        int publications = context.Hub.Published.Count;
        var result = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        if (outerRejected)
        {
            Assert.Equal(ControlOperationStatus.Blocked, result.Status);
            Assert.Contains("Inner live observation: curve transaction ObservationUnavailable.", result.Message);
            Assert.EndsWith("Admission failed at: runtime fresh power source (resolved Battery; required PluggedIn).", result.Message);
            Assert.Null(result.CpuTuning);
            Assert.Equal(before, context.Runtime.GetStatus());
            Assert.Equal(publications, context.Hub.Published.Count);
            Assert.Equal(3, powerReads);
        }
        else
        {
            Assert.Equal(ControlOperationStatus.Stored, result.Status);
            Assert.Contains("Live observation: curve transaction ObservationUnavailable.", result.Message);
            Assert.Null(result.CpuTuning!.LiveState); Assert.False(result.CpuTuning.Live.CanWrite);
        }
        Assert.Equal(1, live.Curve.Reads); Assert.Equal(0, live.Power!.Reads);
        Assert.Empty(live.Curve.Sent); Assert.Empty(live.Power.Sent); Assert.Equal(0, context.Source.Submissions);
    }
    [Fact]
    public async Task PartialLiveObservationRetainsPowerRejectionWithoutExtraQueriesOrSetters()
    {
        var live = new LiveFixture(curve: true, power: true);
        live.Power!.Unavailable = true;
        live.Power.ObservationFailure = "power native observation MetadataVersion/InvalidData";
        var context = await CreateContextAsync(live: live);
        var result = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Contains("Live observation: power transaction ObservationUnavailable.", result.Message);
        Assert.Contains("First rejection: power native observation MetadataVersion/InvalidData.", result.Message);
        Assert.Equal(12, result.CpuTuning!.LiveState!.CoreValues.Count);
        Assert.Null(result.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(CpuTuningRecoveryState.Blocked, result.CpuTuning.LiveRecovery!.State);
        Assert.Equal(CpuTuningFeatures.None, result.CpuTuning.LiveRecovery.Features);
        Assert.False(result.CpuTuning.Live.CanWrite);
        Assert.Equal(1, live.Curve!.Reads); Assert.Equal(1, live.Power.Reads);
        Assert.Empty(live.Curve.Sent); Assert.Empty(live.Power.Sent);
        Assert.Equal(0, context.Source.Submissions);
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.CpuTuning.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, AdapterPowerState.FullPower280W)]
    [InlineData(true, AdapterPowerState.FullPower330W)]
    public async Task ExternalPowerRequiresFreshBarrelConsensusForSavedAndLive(bool liveTarget, AdapterPowerState adapter)
    {
        var live = liveTarget ? new LiveFixture(curve: true, power: true) : null;
        var context = await CreateContextAsync(admittedSavedBackend: !liveTarget, live: live);
        context.Power.Power = PowerSource.ExternalUnknown;
        int samples = 0;
        context.Device.ReadAdapterPower = async token =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref samples);
            return adapter;
        };
        var result = await context.Runtime.SetCpuTuningAsync(liveTarget ? CpuTuningTarget.LiveNow : CpuTuningTarget.FirmwareNextBoot,
            new(null, new(true, -2)), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.True(samples >= 4); // Reclassify at subsequent admission boundaries.
        Assert.Equal(0, samples % 2);
        if (liveTarget) { Assert.NotEmpty(live!.Curve!.Sent); }
        else { Assert.Equal(1, context.Source.Submissions); }
    }

    [Theory]
    [InlineData("battery", false)]
    [InlineData("usb", false)]
    [InlineData("unknown", false)]
    [InlineData("failure", false)]
    [InlineData("usb", true)]
    public async Task CachedFullPowerCannotAdmitFreshBatteryUsbOrUnclassifiedPower(string failure, bool liveTarget)
    {
        var live = liveTarget ? new LiveFixture(curve: true, power: true) : null;
        var context = await CreateContextAsync(admittedSavedBackend: !liveTarget, live: live);
        Assert.Equal(PowerSource.PluggedIn, context.Runtime.GetStatus().ActivePowerSource);
        context.Power.Power = failure == "battery" ? PowerSource.Battery : PowerSource.ExternalUnknown;
        int samples = 0;
        context.Device.ReadAdapterPower = async _ =>
        {
            await Task.Yield();
            Interlocked.Increment(ref samples);
            if (failure == "failure") { throw new IOException("unavailable adapter sample"); }
            return failure == "usb" ? AdapterPowerState.UsbCPower100W : AdapterPowerState.Unknown;
        };
        var result = await context.Runtime.SetCpuTuningAsync(liveTarget ? CpuTuningTarget.LiveNow : CpuTuningTarget.FirmwareNextBoot,
            new(null, new(true, -2)), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Source.Submissions);
        if (!liveTarget)
        {
            string actual = failure == "battery" ? "Battery" : failure == "usb" ? "UsbC" : "ExternalUnknown";
            Assert.EndsWith($"Admission failed at: runtime fresh power source (resolved {actual}; required PluggedIn).", result.Message);
        }
        if (liveTarget) { Assert.Empty(live!.Curve!.Sent); }
        if (failure == "battery") { Assert.Equal(0, samples); }
        else { Assert.True(samples >= 2); }
    }

    [Theory]
    [InlineData("session")]
    [InlineData("generation")]
    [InlineData("device")]
    public async Task RevocationDuringAsyncAdapterReadPreventsSavedSubmission(string revoked)
    {
        var context = await CreateContextAsync(admittedSavedBackend: true);
        context.Power.Power = PowerSource.ExternalUnknown;
        bool active = true;
        context.Device.ReadAdapterPower = async _ =>
        {
            await Task.Yield();
            if (revoked == "session") { active = false; }
            else if (revoked == "generation") { context.Gate.Enter(); }
            else { context.Provider.Available = false; }
            return AdapterPowerState.FullPower330W;
        };
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(true, -2)), () => active, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Source.Submissions);
    }

    [Fact]
    public async Task ReverseAdmissionReclassifiesRatherThanReusingForwardPower()
    {
        var context = await CreateContextAsync(admittedSavedBackend: true);
        context.Power.Power = PowerSource.ExternalUnknown;
        int samples = 0;
        context.Device.ReadAdapterPower = async _ =>
        {
            await Task.Yield();
            return Interlocked.Increment(ref samples) <= 2 ? AdapterPowerState.FullPower330W : AdapterPowerState.UsbCPower100W;
        };
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(true, -2)), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.EndsWith("Admission failed at: runtime fresh power source (resolved UsbC; required PluggedIn).", result.Message);
        Assert.Equal(4, samples);
        Assert.Equal(0, context.Source.Submissions);
        Assert.False(result.CpuTuning!.RestorePending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledClassificationRetainsActualReadAndDeniesOverlappingAdmission(bool callerCancels)
    {
        var context = await CreateContextAsync(admittedSavedBackend: true);
        context.Power.Power = PowerSource.ExternalUnknown;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int samples = 0;
        context.Device.ReadAdapterPower = async _ =>
        {
            Interlocked.Increment(ref samples);
            entered.TrySetResult();
            await release.Task; // Deliberately model an actual read that ignores cancellation.
            return AdapterPowerState.FullPower330W;
        };
        using var cancellation = new CancellationTokenSource();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Task<ControlOperationResult> operation = Task.Run(() => context.Runtime.SetCpuTuningAsync(
            CpuTuningTarget.FirmwareNextBoot, new(null, new(true, -2)), () => true, cancellation.Token));
        Task? worker = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker = Assert.IsAssignableFrom<Task>(typeof(ServiceControlRuntime).GetField("cpuPowerClassification",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(context.Runtime));
            if (callerCancels)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            }
            else
            {
                var timedOut = await operation.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(ControlOperationStatus.Blocked, timedOut.Status);
                Assert.EndsWith("Admission failed at: runtime fresh power source (10-second classification deadline elapsed; caller not canceled).", timedOut.Message);
                Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(9));
                Assert.False(cancellation.IsCancellationRequested);
            }
            Assert.False(worker.IsCompleted);
            var next = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
                new(null, new(true, -2)), () => true, default);
            Assert.Equal(ControlOperationStatus.Blocked, next.Status);
            Assert.EndsWith("Admission failed at: runtime fresh power source (prior classification still running).", next.Message);
            Assert.Equal(1, samples);
            Assert.Equal(0, context.Source.Submissions);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try { await operation; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            worker ??= typeof(ServiceControlRuntime).GetField("cpuPowerClassification",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(context.Runtime) as Task;
            if (worker is not null)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
            }
        }
    }

    [Fact]
    public async Task IndependentlyCancelledClassificationIsNotReportedAsDeadlineOrCallerCancellation()
    {
        var context = await CreateContextAsync(admittedSavedBackend: true);
        int reads = 0;
        context.Power.OnCapture = () =>
        {
            reads++;
            throw new OperationCanceledException("Independent classifier cancellation.");
        };
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(true, -2)), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.EndsWith("Admission failed at: runtime fresh power source (classification canceled independently; caller and deadline not canceled).", result.Message);
        Assert.Equal(1, reads);
        Assert.Equal(0, context.Source.Submissions);
    }

    [Fact]
    public async Task CurveObservationDeadlineCancelsFreshPowerButRetainsAnUnfinishedRead()
    {
        var live = new LiveFixture(curve: true, power: false);
        CpuTuningController? controller = null;
        var context = await CreateContextAsync(live: live, controllerFactory: source =>
            controller = new(source, curveBackend: live.Curve, curveStore: live.CurveStore));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken classifierToken = default;
        context.Device.ReadAdapterPower = async token =>
        {
            classifierToken = token; entered.TrySetResult();
            await release.Task; // The actual read deliberately ignores cancellation.
            return AdapterPowerState.FullPower330W;
        };
        live.Curve!.OnObserve = token => Task.Run(() =>
        {
            context.Power.Power = PowerSource.ExternalUnknown;
            Assert.False(controller!.CaptureLiveRuntimeAdmission(CpuTuningFeatures.None, token)());
            return new CpuLiveCurveBackendObservation(null, null);
        });
        using var outer = new CancellationTokenSource();
        Task<ControlOperationResult> observation = context.Runtime.ObserveCpuTuningAsync(true, () => true, outer.Token);
        Task? worker = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker = Assert.IsAssignableFrom<Task>(typeof(ServiceControlRuntime).GetField("cpuPowerClassification",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(context.Runtime));
            ControlOperationResult result = await observation.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(classifierToken.IsCancellationRequested);
            Assert.False(outer.IsCancellationRequested);
            Assert.False(worker.IsCompleted);
            Assert.Equal(ControlOperationStatus.Blocked, result.Status);
            Assert.Null(result.CpuTuning);
            Assert.Contains("prior classification still running", result.Message);
            Assert.Contains("power adapter read sample 1 of 5 (open/exchange/dispose); elapsed ", result.Message);
            Assert.Empty(live.Curve.Sent); Assert.Equal(0, live.CurveStore!.Writes);
        }
        finally
        {
            release.TrySetResult();
            await observation;
            if (worker is not null) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker); }
        }
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("low-power")]
    [InlineData("unverified")]
    [InlineData("unsupported")]
    [InlineData("no-recent-device")]
    public async Task ObservationAdmissionFailureSkipsCpuSourceAndPublication(string failure)
    {
        TestContext context = await CreateContextAsync(supported: failure != "unsupported",
            verified: failure != "unverified");
        if (failure == "low-power") { context.Gate.Enter(); }
        if (failure == "no-recent-device") { context.Provider.Available = false; }
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        int publications = context.Hub.Published.Count;

        ControlOperationResult result = await context.Runtime.ObserveCpuTuningAsync(true,
            () => failure != "inactive", CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Source.Calls);
        string stage = failure switch
        {
            "inactive" => "runtime controlling session",
            "low-power" => "runtime active power lifecycle",
            "unverified" => "runtime verified device status",
            "unsupported" => "runtime supported CPU capability",
            _ => "runtime recently verified device",
        };
        Assert.EndsWith($"Admission failed at: {stage}.", result.Message);
        Assert.Null(result.CpuTuning);
        Assert.Equal(before, context.Runtime.GetStatus());
        Assert.Equal(publications, context.Hub.Published.Count);
        Assert.Empty(context.Device.BatterySetterCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionOrLowPowerChangeAfterObservationPreventsPublication(bool suspend)
    {
        TestContext context = await CreateContextAsync();
        bool active = true;
        context.Source.AfterInspection = () =>
        {
            if (suspend) { context.Gate.Enter(); }
            else { active = false; }
        };
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        int publications = context.Hub.Published.Count;

        ControlOperationResult result = await context.Runtime.ObserveCpuTuningAsync(true,
            () => active, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(3, context.Source.Calls);
        Assert.EndsWith($"Admission failed at: runtime {(suspend ? "active power lifecycle" : "controlling session")}.", result.Message);
        Assert.Null(result.CpuTuning);
        Assert.Equal(before, context.Runtime.GetStatus());
        Assert.Equal(publications, context.Hub.Published.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalLiveObservationAdmissionNamesFirstDenialWithoutAdditionalPowerReads(bool changeGeneration)
    {
        var live = new LiveFixture(curve: true, power: false);
        var context = await CreateContextAsync(live: live);
        int freshPowerReads = 0;
        context.Source.AfterInspection = () =>
        {
            if (changeGeneration) { context.Gate.Enter(); context.Gate.Exit(); }
            else { context.Power.Power = PowerSource.Battery; }
        };
        context.Power.OnCapture = () => freshPowerReads++;
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        int publications = context.Hub.Published.Count;

        var result = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        string failure = changeGeneration ? "runtime power generation before probes"
            : "runtime fresh power source (resolved Battery; required PluggedIn)";
        Assert.EndsWith($"Admission failed at: {failure}.", result.Message);
        Assert.Contains($"Inner first rejection: {failure}.", result.Message);
        // The existing live observation and final runtime checks each classify;
        // a generation mismatch still short-circuits both before power reads.
        Assert.Equal(changeGeneration ? 0 : 2, freshPowerReads);
        Assert.Null(result.CpuTuning);
        Assert.Equal(before, context.Runtime.GetStatus());
        Assert.Equal(publications, context.Hub.Published.Count);
        Assert.Empty(live.Curve!.Sent);
    }

    [Fact]
    public async Task OptionalReadOnlyCapabilityDoesNotMaskFinalObservationRejection()
    {
        var live = new LiveFixture(curve: true, power: false);
        var context = await CreateContextAsync(live: live, configureCapabilities: capabilities => capabilities with
        { CpuVoltageOptimizer = capabilities.CpuVoltageOptimizer! with { Access = DeviceFeatureAccess.ObservedOnly } });
        int reads = 0;
        context.Power.OnCapture = () =>
        {
            if (++reads == 3) { context.Power.Power = PowerSource.Battery; }
        };
        var before = context.Runtime.GetStatus();

        var result = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.EndsWith("Admission failed at: runtime fresh power source (resolved Battery; required PluggedIn).", result.Message);
        Assert.Contains("Inner first rejection: runtime curve write capability.", result.Message);
        Assert.Equal(3, reads);
        Assert.Null(result.CpuTuning);
        Assert.Equal(before, context.Runtime.GetStatus());
        Assert.Empty(live.Curve!.Sent);
    }

    [Fact]
    public async Task PassiveObservationPublishesStatusWithoutAdmittingWritesOrRestoration()
    {
        TestContext context = await CreateContextAsync();

        ControlOperationResult observation = await context.Runtime.ObserveCpuTuningAsync(true,
            () => true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, observation.Status);
        Assert.Equal(observation.CpuTuning, context.Runtime.GetStatus().CpuTuning);
        CpuTuningStatus status = Assert.IsType<CpuTuningStatus>(observation.CpuTuning);
        Assert.Null(status.LiveState);
        Assert.Equal(CpuTuningPendingBoot.Unknown, status.PendingBoot);
        Assert.False(status.RestorePending);
        Assert.False(status.Firmware.CanWrite);
        Assert.False(status.Live.CanWrite);
        int calls = context.Source.Calls;

        foreach (CpuTuningTarget target in Enum.GetValues<CpuTuningTarget>())
        {
            Assert.Equal(ControlOperationStatus.Blocked,
                (await context.Runtime.SetCpuTuningAsync(target, new(65, new(false, -17)),
                    () => true, CancellationToken.None)).Status);
            Assert.Equal(ControlOperationStatus.Blocked,
                (await context.Runtime.RestoreCpuTuningAsync(target,
                    () => true, CancellationToken.None)).Status);
        }
        Assert.Equal(calls, context.Source.Calls);
        Assert.Empty(context.Device.BatterySetterCalls);
    }

    [Theory]
    [InlineData(CpuTuningTarget.FirmwareNextBoot, true)]
    [InlineData(CpuTuningTarget.FirmwareNextBoot, false)]
    [InlineData(CpuTuningTarget.LiveNow, true)]
    [InlineData(CpuTuningTarget.LiveNow, false)]
    public async Task SetAndRestoreRemainBlockedWithoutCallingCpuSource(CpuTuningTarget target, bool active)
    {
        TestContext context = await CreateContextAsync();

        ControlOperationResult set = await context.Runtime.SetCpuTuningAsync(target,
            new(65, new(true, -1)), () => active, CancellationToken.None);
        ControlOperationResult restore = await context.Runtime.RestoreCpuTuningAsync(target,
            () => active, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, set.Status);
        Assert.Equal(ControlOperationStatus.Blocked, restore.Status);
        Assert.Equal(0, context.Source.Calls);
        Assert.Empty(context.Device.BatterySetterCalls);
        Assert.Null(context.Runtime.GetStatus().CpuTuning);
    }

    [Fact]
    public async Task AdmittedFakeSavedWorkflowPublishesOwnershipAndRestoresOriginalBytes()
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true);
        byte[] baseline = (byte[])context.Source.Bytes!.Clone();
        ControlOperationResult saved = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -10)), () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, saved.Status);
        Assert.True(context.Runtime.GetStatus().CpuTuning!.RestorePending);
        Assert.Equal(CpuTuningPendingBoot.Requested, saved.CpuTuning!.PendingBoot);
        Assert.Null(saved.CpuTuning.LiveState);
        ControlOperationResult restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.False(context.Runtime.GetStatus().CpuTuning!.RestorePending);
        Assert.Equal(baseline, context.Source.Bytes);
        Assert.Equal(2, context.Source.Submissions);
    }

    [Fact]
    public async Task CurveOnlyRecoveryIgnoresUnownedObservedOnlyPowerLimitAndCannotRestoreLive()
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true, powerAdmitted: false);
        byte[] baseline = (byte[])context.Source.Bytes!.Clone();
        ControlOperationResult applied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(true, -12)), () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer), applied.CpuTuning!.FirmwareRecovery);
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.RestoreCpuTuningAsync(
            CpuTuningTarget.LiveNow, () => true, CancellationToken.None)).Status);
        Assert.Equal(1, context.Source.Submissions);
        ControlOperationResult restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(2, context.Source.Submissions);
        Assert.Equal(baseline, context.Source.Bytes);
    }

    [Fact]
    public async Task ObservedSavedFeaturesApplyAndRestoreWithoutAdmittingLiveOrConstrainingOriginals()
    {
        var live = new LiveFixture(curve: true, power: false);
        TestContext context = await CreateContextAsync(admittedSavedBackend: true, live: live,
            savedAccess: DeviceFeatureAccess.ObservedOnly);
        byte[] baseline = (byte[])context.Source.Bytes!.Clone();
        var applied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(45, new(false, -10)), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.Equal(2, context.Source.Submissions);
        Assert.True(applied.CpuTuning!.Firmware.CanWrite);
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.SetCpuTuningAsync(
            CpuTuningTarget.LiveNow, new(null, new(true, -2)), () => true, default)).Status);
        Assert.Equal(0, live.Curve!.Reads);
        Assert.Empty(live.Curve.Sent);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            CpuPowerLimit = new(new(60, 90, 10), DeviceFeatureAccess.ObservedOnly),
            CpuVoltageOptimizer = new(new(-30, -2, 2), DeviceFeatureAccess.ObservedOnly),
        };
        await context.Runtime.ObserveAsync(default);
        // Restore -1 and exact default PPT Reset0, neither in these editor ranges.
        var restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(4, context.Source.Submissions);
        Assert.Equal(baseline, context.Source.Bytes);
        Assert.False(restored.CpuTuning!.RestorePending);
    }

    [Theory]
    [InlineData("session", "runtime controlling session", 0)]
    [InlineData("generation", "runtime power generation before probes", 0)]
    [InlineData("device", "runtime fresh verified device", 1)]
    public async Task DefaultSavedFactoryRevocationAfterPairedReadReportsFirstFailureBeforeNativeAdmission(
        string failure, string stage, int expectedVerifications)
    {
        var journal = new FailingSavedStore { Fail = false };
        TestContext context = await CreateContextAsync(admittedSavedBackend: true, savedJournal: journal,
            savedAccess: DeviceFeatureAccess.ObservedOnly, defaultSavedBackend: true);
        int readsBefore = context.Source.SavedReads;
        byte[] baseline = (byte[])context.Source.Bytes!.Clone();
        bool active = true;
        int verificationCalls = 0;
        context.Provider.BeforeVerify = () => verificationCalls++;
        // Arm only after setup. The real default backend returns this fake pair,
        // then its runtime callback refuses before any native USB/controller or
        // WMI call. This fence is independent of the source-admission decision.
        context.Source.AfterSavedRead = () =>
        {
            if (failure == "session") { active = false; }
            else if (failure == "generation") { context.Gate.Enter(); }
            else { context.Provider.Available = false; }
        };
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -10)), () => active, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.True(context.Source.SavedReads > readsBefore);
        Assert.Equal(failure != "session", active);
        Assert.Equal(0, journal.Writes);
        Assert.Null(journal.Journal);
        Assert.Equal(0, context.Source.Submissions);
        Assert.Equal(baseline, context.Source.Bytes);
        Assert.True(result.CpuTuning!.Firmware.CanWrite);
        Assert.Equal($"CPU commands require attended service write and restoration admission. Admission failed at: {stage}.", result.Message);
        Assert.Equal(expectedVerifications, verificationCalls);
    }

    [Theory]
    [InlineData("power-range")]
    [InlineData("power-step")]
    [InlineData("unknown-step")]
    [InlineData("curve-range")]
    [InlineData("curve-step")]
    [InlineData("disable")]
    [InlineData("readonly")]
    [InlineData("unknown-access")]
    [InlineData("missing")]
    [InlineData("invalid-range")]
    public async Task SavedForwardIntentRequiresEligibleFeatureAndSupportedValue(string denial)
    {
        bool curve = denial.StartsWith("curve", StringComparison.Ordinal) || denial == "disable";
        TestContext context = await CreateContextAsync(admittedSavedBackend: true,
            savedAccess: DeviceFeatureAccess.ObservedOnly, configureCapabilities: capabilities => denial switch
            {
                "power-range" => capabilities with { CpuPowerLimit = new(new(70, 95, 1), DeviceFeatureAccess.ObservedOnly) },
                "power-step" => capabilities with { CpuPowerLimit = new(new(45, 95, 10), DeviceFeatureAccess.ObservedOnly) },
                "unknown-step" => capabilities with { CpuPowerLimit = new(new(45, 95, null), DeviceFeatureAccess.ObservedOnly) },
                "curve-range" => capabilities with { CpuVoltageOptimizer = new(new(-8, -1, 1), DeviceFeatureAccess.ObservedOnly) },
                "curve-step" => capabilities with { CpuVoltageOptimizer = new(new(-30, -2, 2), DeviceFeatureAccess.ObservedOnly) },
                "disable" => capabilities with { CpuVoltageOptimizer = new(new(-30, -1, 1), DeviceFeatureAccess.ObservedOnly) },
                "readonly" => capabilities with { CpuPowerLimit = new(new(45, 95, 1), DeviceFeatureAccess.ReadOnly) },
                "unknown-access" => capabilities with { CpuPowerLimit = new(new(45, 95, 1), (DeviceFeatureAccess)99) },
                "missing" => capabilities with { CpuPowerLimit = null },
                _ => capabilities with { CpuPowerLimit = new(new(95, 45, 1), DeviceFeatureAccess.ObservedOnly) },
            });
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            curve ? new(null, new(denial != "disable", -9)) : new(60, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Source.Calls);
        Assert.Equal(0, context.Source.Submissions);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("disable")]
    [InlineData("access")]
    [InlineData("missing")]
    public async Task PublishedCapabilityLossDuringFinalDeviceProbeRevokesSavedAdmission(string loss)
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true, savedAccess: DeviceFeatureAccess.ObservedOnly);
        byte[] baseline = (byte[])context.Source.Bytes!.Clone();
        var previousCapabilities = context.Runtime.GetStatus().Capabilities;
        context.Source.BeforeSavedSend = () => context.Provider.BeforeVerify = () =>
        {
            context.Provider.BeforeVerify = null;
            DeviceCapabilities revoked = loss switch
            {
                "range" => previousCapabilities! with { CpuPowerLimit = new(new(70, 95, 1), DeviceFeatureAccess.ObservedOnly) },
                "disable" => previousCapabilities! with { CpuVoltageOptimizer = new(new(-30, -1, 1), DeviceFeatureAccess.ObservedOnly) },
                "missing" => previousCapabilities! with { CpuPowerLimit = null },
                _ => previousCapabilities! with { CpuPowerLimit = new(new(45, 95, 1), DeviceFeatureAccess.ReadOnly) },
            };
            // Simulate capability publication during the existing synchronous
            // device probe; do not introduce a product admission/status override.
            Func<ServiceStatusSnapshot, ServiceStatusSnapshot> update = status => status with { Capabilities = revoked };
            typeof(ServiceControlRuntime).GetMethod("PublishStatusUpdate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(context.Runtime, [update]);
        };
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            loss == "disable" ? new(null, new(false, -10)) : new(65, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.NotSame(previousCapabilities, context.Runtime.GetStatus().Capabilities);
        Assert.Equal(0, context.Source.Submissions);
        Assert.Equal(baseline, context.Source.Bytes);
        Assert.True(result.CpuTuning!.RestorePending);
    }

    [Fact]
    public async Task ZeroCurveOriginalRequiresDisableSupportBeforeApplyAndAgainBeforeRestore()
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true, savedAccess: DeviceFeatureAccess.ObservedOnly);
        context.Source.Bytes = CpuSavedTuningPlan.Simulate(context.Source.Bytes!, new(CpuSavedTuningCommandKind.CurveOptimizer, 0));
        byte[] baseline = (byte[])context.Source.Bytes!.Clone();
        var supported = context.Device.Capabilities;
        context.Device.Capabilities = supported with
        {
            CpuVoltageOptimizer = supported.CpuVoltageOptimizer! with { SupportsEnableDisable = false },
        };
        await context.Runtime.ObserveAsync(default);
        var denied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(true, -2)), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, denied.Status);
        Assert.Equal(0, context.Source.Submissions);
        context.Device.Capabilities = supported;
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.SetCpuTuningAsync(
            CpuTuningTarget.FirmwareNextBoot, new(null, new(true, -2)), () => true, default)).Status);
        context.Device.Capabilities = supported with { CpuVoltageOptimizer = supported.CpuVoltageOptimizer! with { SupportsEnableDisable = false } };
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.RestoreCpuTuningAsync(
            CpuTuningTarget.FirmwareNextBoot, () => true, default)).Status);
        Assert.Equal(1, context.Source.Submissions);
        context.Device.Capabilities = supported;
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.RestoreCpuTuningAsync(
            CpuTuningTarget.FirmwareNextBoot, () => true, default)).Status);
        Assert.Equal(2, context.Source.Submissions);
        Assert.Equal(baseline, context.Source.Bytes);
    }

    [Fact]
    public async Task SessionLossAfterInitialGateStopsFakeSavedSubmission()
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true);
        bool active = true;
        context.Source.BeforeIdentity = () => active = false;
        ControlOperationResult result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -1)), () => active, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Source.Submissions);
        Assert.False(result.CpuTuning!.RestorePending);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CompletedLowPowerBoundaryRevokesPendingSavedOperation(bool restoring, bool beforeSend)
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true);
        if (restoring)
        {
            Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.SetCpuTuningAsync(
                CpuTuningTarget.FirmwareNextBoot, new(null, new(false, -10)), () => true, default)).Status);
        }
        byte[] before = (byte[])context.Source.Bytes!.Clone();
        int submissions = context.Source.Submissions;
        Action boundary = () =>
        {
            context.Source.BeforeIdentity = null;
            context.Source.BeforeSavedSend = null;
            Assert.True(context.Gate.Enter());
            Assert.True(context.Gate.Exit());
        };
        if (beforeSend) { context.Source.BeforeSavedSend = boundary; }
        else { context.Source.BeforeIdentity = boundary; }

        ControlOperationResult result = restoring
            ? await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot, () => true, default)
            : await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
                new(null, new(false, -10)), () => true, default);

        Assert.False(context.Gate.IsActive);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(submissions, context.Source.Submissions);
        Assert.Equal(before, context.Source.Bytes);
        // Final admission denial retains an already prepared restoration journal.
        Assert.Equal(restoring || beforeSend, result.CpuTuning!.RestorePending);

        if (beforeSend && !restoring)
        {
            // Retire the prepared, never-submitted journal before a new apply.
            Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.RestoreCpuTuningAsync(
                CpuTuningTarget.FirmwareNextBoot, () => true, default)).Status);
            Assert.Equal(submissions, context.Source.Submissions);
            Assert.Equal(before, context.Source.Bytes);
        }
        // A fresh operation may admit the new generation; the interrupted one cannot.
        ControlOperationResult next = restoring
            ? await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot, () => true, default)
            : await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
                new(null, new(false, -10)), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, next.Status);
        Assert.Equal(submissions + 1, context.Source.Submissions);
    }

    [Fact]
    public async Task LiveCurveUsesRetainedOwnershipAndRestoresWithoutPowerAuthority()
    {
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(powerAdmitted: false, live: live);
        var first = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(null, new(true, -2)), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, first.Status);
        Assert.Equal(-2, first.CpuTuning!.LiveState!.UniformCurveOptimizerValue);
        Assert.Equal(new(null, live.Power!.Pair.Fast.Milliwatts, live.Power.Pair.Slow.Milliwatts), first.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer), first.CpuTuning.LiveRecovery);
        var original = live.CurveStore!.Journal!.OriginalBaseline;
        await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(null, new(true, -3)), () => true, default);
        Assert.Equal(original, live.CurveStore.Journal!.OriginalBaseline);
        var restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(-1, restored.CpuTuning!.LiveState!.UniformCurveOptimizerValue);
        Assert.Equal(CpuTuningRecoveryState.None, restored.CpuTuning.LiveRecovery!.State);
        Assert.False(restored.CpuTuning.RestorePending);
        Assert.True(restored.CpuTuning.Live.CanWrite);
        Assert.Empty(live.Power.Admissions); Assert.Empty(live.Power.Sent); Assert.Null(live.PowerStore!.Journal);
        Assert.Equal(0, context.Source.Calls);
    }

    [Fact]
    public async Task LivePowerRestoresBothExactFractionalOriginalsWithoutInventingCurveState()
    {
        var live = new LiveFixture(curve: false, power: true);
        var original = live.Power!.Pair;
        var context = await CreateContextAsync(live: live);
        var observed = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, observed.Status);
        Assert.True(observed.CpuTuning!.IsWellFormed);
        Assert.Empty(observed.CpuTuning.LiveState!.CoreValues);
        Assert.Equal(new(null, original.Fast.Milliwatts, original.Slow.Milliwatts), observed.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(observed.CpuTuning, context.Runtime.GetStatus().CpuTuning);
        Assert.False(observed.CpuTuning.Live.CanWrite);
        var applied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(45, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.Equal(45000u, live.Power.Pair.Fast.Milliwatts);
        Assert.Equal(45000u, live.Power.Pair.Slow.Milliwatts);
        Assert.Equal(original.StapmRawBits, live.Power.Pair.StapmRawBits);
        Assert.True(applied.CpuTuning!.IsWellFormed);
        Assert.Empty(applied.CpuTuning.LiveState!.CoreValues);
        Assert.Equal(new(null, 45000, 45000), applied.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(applied.CpuTuning, context.Runtime.GetStatus().CpuTuning);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.PowerLimit), applied.CpuTuning.LiveRecovery);
        var restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(original, live.Power.Pair);
        Assert.Equal(4, live.Power.Sent.Count);
        Assert.Equal(CpuLivePowerOwnershipState.Restored, live.PowerStore!.Journal!.State);
        Assert.False(restored.CpuTuning!.RestorePending);
        Assert.True(restored.CpuTuning.IsWellFormed);
        Assert.Empty(restored.CpuTuning.LiveState!.CoreValues);
        Assert.Null(restored.CpuTuning.LiveState.UniformCurveOptimizerValue);
        Assert.Equal(new(null, original.Fast.Milliwatts, original.Slow.Milliwatts), restored.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(restored.CpuTuning, context.Runtime.GetStatus().CpuTuning);
    }

    [Fact]
    public async Task CombinedIntentAndLaterSingleFamilyEditRestoreBothOriginalsThroughRuntime()
    {
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(live: live);
        var original = live.Power!.Pair;
        var combined = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(45, new(true, -2)), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, combined.Status);
        Assert.True(combined.CpuTuning!.IsWellFormed);
        Assert.Equal(-2, combined.CpuTuning.LiveState!.UniformCurveOptimizerValue);
        Assert.Equal(new(null, 45000, 45000), combined.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(combined.CpuTuning, context.Runtime.GetStatus().CpuTuning);
        var second = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(60, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, second.Status);
        Assert.Equal(-2, second.CpuTuning!.LiveState!.UniformCurveOptimizerValue);
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default)).Status);
        Assert.Equal(original, live.Power.Pair);
        Assert.All(live.Curve!.Values, core => Assert.Equal(-1, core.Offset));
        Assert.Equal(0, context.Source.Submissions);
    }

    [Fact]
    public async Task ObservedOnlyCurveCapabilityKeepsFullLiveReadbackReadOnlyAndSubmitsNothing()
    {
        var production = BatteryValidationFactory.CreateWindowsRz090528ProductionBattery().Capabilities;
        Assert.Equal(DeviceFeatureAccess.ReadWrite, production.CpuVoltageOptimizer!.Access);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, production.CpuPowerLimit!.Access);
        // Production CO is promoted; an explicitly read-only capability must
        // still deny submission even when the installed backend can write.
        var observedOnlyCurve = production.CpuVoltageOptimizer with { Access = DeviceFeatureAccess.ObservedOnly };
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(live: live, configureCapabilities: capabilities => capabilities with
        { CpuVoltageOptimizer = observedOnlyCurve, CpuPowerLimit = production.CpuPowerLimit });
        var observed = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.Equal(-1, observed.CpuTuning!.LiveState!.UniformCurveOptimizerValue);
        Assert.NotNull(observed.CpuTuning.LiveState.PowerLimits);
        Assert.False(observed.CpuTuning.Live.CanWrite);
        var denied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(null, new(false, -1)), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, denied.Status);
        Assert.Empty(live.Curve!.Sent); Assert.Empty(live.Power!.Sent);
        Assert.Equal(0, context.Source.Submissions);
    }

    [Fact]
    public async Task LiveObservationRequiresExplicitInstalledChoiceAndFreshCurveWriteAdmission()
    {
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(live: live);
        await context.Runtime.ObserveCpuTuningAsync(false, () => true, default);
        Assert.Equal(0, live.Curve!.Reads); Assert.Equal(0, live.Power!.Reads);
        var observed = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, observed.Status);
        Assert.True(observed.CpuTuning!.IsWellFormed);
        Assert.Equal(-1, observed.CpuTuning.LiveState!.UniformCurveOptimizerValue);
        Assert.Equal(live.Power.Pair.Fast.Milliwatts, observed.CpuTuning.LiveState.PowerLimits!.Message32Milliwatts);
        Assert.Equal(live.Power.Pair.Slow.Milliwatts, observed.CpuTuning.LiveState.PowerLimits.Message33Milliwatts);
        Assert.Null(observed.CpuTuning.LiveState.PowerLimits.EffectiveWatts);
        Assert.True(observed.CpuTuning.Live.CanWrite);
        Assert.Equal(CpuTuningRecoveryState.None, observed.CpuTuning.LiveRecovery!.State);
        Assert.Empty(live.Curve.Sent); Assert.Empty(live.Power.Sent);
    }

    [Fact]
    public async Task SuspendAndResumeInsideAdmissionCannotReviveTheOriginalRuntimeLease()
    {
        var live = new LiveFixture(curve: false, power: true);
        var context = await CreateContextAsync(live: live);
        live.Power!.Admit = (_, _) => { context.Gate.Enter(); context.Gate.Exit(); return true; };
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(45, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(live.Power.Sent); Assert.Null(live.PowerStore!.Journal);
        Assert.False(result.CpuTuning!.RestorePending);
    }

    [Fact]
    public async Task CancelledPendingPowerSenderBlocksBothFamiliesAndLateCompletionCannotRestoreAuthority()
    {
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(live: live);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CpuLivePowerSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        live.Power!.OnSend = _ => { entered.SetResult(); return release.Task; };
        using var cancellation = new CancellationTokenSource();
        Task<ControlOperationResult> pending = context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(45, null), () => true, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        Assert.Equal(ControlOperationStatus.Failed, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(CpuLivePowerOwnershipState.PossibleSubmission, live.PowerStore!.Journal!.State);
        int reads = live.Power.Reads;
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(null, new(true, -2)), () => true, default)).Status);
        await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.Equal(0, live.Curve!.Reads); Assert.Equal(reads, live.Power.Reads);
        release.SetResult(CpuLivePowerSubmission.Acknowledged);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default)).Status);
        Assert.Equal(CpuLivePowerOwnershipState.PossibleSubmission, live.PowerStore.Journal.State);
        Assert.Single(live.Power.Sent); Assert.Empty(live.Curve.Sent);
    }

    [Fact]
    public async Task SavedAndLiveRecoveryRemainSeparateWhenEitherTargetChanges()
    {
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(admittedSavedBackend: true, live: live);
        await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot, new(null, new(false, -1)), () => true, default);
        var applied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(null, new(true, -2)), () => true, default);
        Assert.Equal(CpuTuningPendingBoot.Requested, applied.CpuTuning!.PendingBoot);
        Assert.Equal(CpuTuningRecoveryState.RestoreReady, applied.CpuTuning.FirmwareRecovery!.State);
        var savedRestore = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot, () => true, default);
        Assert.Equal(CpuTuningRecoveryState.None, savedRestore.CpuTuning!.FirmwareRecovery!.State);
        Assert.Equal(CpuTuningRecoveryState.RestoreReady, savedRestore.CpuTuning.LiveRecovery!.State);
        Assert.True(savedRestore.CpuTuning.RestorePending);
        Assert.Equal(-2, savedRestore.CpuTuning.LiveState!.UniformCurveOptimizerValue);
        var liveRestore = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, liveRestore.Status);
        Assert.False(liveRestore.CpuTuning!.RestorePending);
        Assert.Equal(2, context.Source.Submissions);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitialPrepareRetainsFamilyUncertaintyAcrossLiveOperationsButNotSaved(bool curveFailure)
    {
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(admittedSavedBackend: true, live: live);
        if (curveFailure) { live.CurveStore!.FailWrite = 1; }
        else { live.PowerStore!.FailWrite = 1; }
        var failed = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            curveFailure ? new(null, new(true, -2)) : new(45, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, failed.Status);
        Assert.Null(live.CurveStore!.Journal); Assert.Null(live.PowerStore!.Journal);
        var feature = curveFailure ? CpuTuningFeatures.CurveOptimizer : CpuTuningFeatures.PowerLimit;
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, feature), failed.CpuTuning!.LiveRecovery);
        Assert.True(failed.CpuTuning.RestorePending);
        Assert.True(failed.CpuTuning.IsWellFormed);
        int curveReads = live.Curve!.Reads, powerReads = live.Power!.Reads;
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(null, new(true, -2)), () => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(45, null), () => true, default)).Status);
        var observed = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, feature), observed.CpuTuning!.LiveRecovery);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default)).Status);
        Assert.Equal(curveReads, live.Curve.Reads); Assert.Equal(powerReads, live.Power.Reads);
        Assert.Empty(live.Curve.Sent); Assert.Empty(live.Power.Sent);
        var saved = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -1)), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, saved.Status);
        Assert.Equal(1, context.Source.Submissions);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, feature), saved.CpuTuning!.LiveRecovery);
    }
    [Fact]
    public async Task SavedFailedPrepareRemainsBlockedAcrossRecreatedTransactionsAndReadOnlyObservation()
    {
        var journal = new FailingSavedStore();
        var context = await CreateContextAsync(admittedSavedBackend: true, savedJournal: journal);
        var failed = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -1)), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, failed.Status);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.CurveOptimizer), failed.CpuTuning!.FirmwareRecovery);
        Assert.True(failed.CpuTuning.RestorePending); Assert.True(failed.CpuTuning.IsWellFormed);
        Assert.Null(journal.Journal); Assert.Equal(0, context.Source.Submissions);
        journal.Fail = false;
        var observed = await context.Runtime.ObserveCpuTuningAsync(false, () => true, default);
        Assert.NotNull(observed.CpuTuning!.SavedConfiguration);
        Assert.Equal(failed.CpuTuning.FirmwareRecovery, observed.CpuTuning.FirmwareRecovery);
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -1)), () => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot, () => true, default)).Status);
        Assert.Equal(1, journal.Writes); Assert.Equal(0, context.Source.Submissions);
    }

    private sealed class FailingSavedStore : ICpuTuningOwnershipJournalStore
    {
        internal bool Fail = true;
        internal int Writes;
        internal CpuTuningOwnershipJournal? Journal;
        private string? hash;
        public Task<CpuTuningOwnershipJournalLoadResult> LoadAsync(CancellationToken token) =>
            Task.FromResult(new CpuTuningOwnershipJournalLoadResult(Journal, false, hash));
        public Task<string?> TryWriteAsync(string? expectedSha256, CpuTuningOwnershipJournal journal, CancellationToken token)
        {
            Writes++;
            if (Fail || expectedSha256 != hash) { return Task.FromResult<string?>(null); }
            Assert.True(journal.IsWellFormed); Journal = journal; hash = Writes.ToString();
            return Task.FromResult<string?>(hash);
        }
    }

    private sealed class LiveFixture
    {
        internal readonly CpuLiveCurveTuningTransactionTests.MemoryStore? CurveStore;
        internal readonly CpuLiveCurveTuningTransactionTests.FakeBackend? Curve;
        internal readonly CpuLivePowerTuningTransactionTests.MemoryStore? PowerStore;
        internal readonly CpuLivePowerTuningTransactionTests.FakeBackend? Power;
        internal LiveFixture(bool curve, bool power)
        {
            if (curve) { CurveStore = new(); Curve = new(CurveStore); }
            if (power) { PowerStore = new(); Power = new(PowerStore); }
            if (Curve is not null && Power is not null)
            {
                Curve.CheckPeer = peer => !Power.Unavailable && peer.Expected == Power.Pair;
                Power.CheckPeer = peer => peer.Expected.CoreOffsets.SequenceEqual(Curve.Values);
            }
        }
    }
    private async Task<TestContext> CreateContextAsync(bool supported = true, bool verified = true,
        bool admittedSavedBackend = false, bool powerAdmitted = true, LiveFixture? live = null,
        ICpuTuningOwnershipJournalStore? savedJournal = null, DeviceFeatureAccess savedAccess = DeviceFeatureAccess.ReadWrite,
        bool defaultSavedBackend = false, Func<DeviceCapabilities, DeviceCapabilities>? configureCapabilities = null,
        Func<ICpuTuningObservationSource, CpuTuningController>? controllerFactory = null)
    {
        var store = new BatteryConfigurationStore(Path.Combine(directory, "profiles.json"),
            Path.Combine(directory, "preferences.json"), Path.Combine(directory, "commit.json"));
        BatteryConfiguration configuration = await store.LoadAsync(CancellationToken.None);
        bool writable = admittedSavedBackend || live is not null;
        var device = new BatteryTestDevice();
        device.Capabilities = device.Capabilities with
        {
            CpuPowerLimit = supported ? new(new(45, 95, 1), writable && powerAdmitted
                ? savedAccess : DeviceFeatureAccess.ObservedOnly) : null,
            CpuVoltageOptimizer = writable ? new(new(-30, -1, 1), savedAccess) { SupportsEnableDisable = true } : null,
        };
        if (configureCapabilities is not null) { device.Capabilities = configureCapabilities(device.Capabilities); }
        var provider = new FakeProvider(device) { Available = verified };
        var source = new FakeObservationSource();
        if (admittedSavedBackend) { source.Bytes = CpuSavedTuningTransactionTests.Baseline(); }
        var gate = new LowPowerGate();
        var hub = new FakeHub();
        var power = new FakePowerState { Power = writable ? PowerSource.PluggedIn : PowerSource.Battery };
        var runtime = new ServiceControlRuntime(store,
            new PersistentOverrideStore(Path.Combine(directory, "overrides.json")),
            power,
            hub, provider, new DiagnosticHistory(), gate,
            new FakePresenceMonitor(), new FakeFirmwareSource(),
            static (_, _) => Task.CompletedTask, ProfileSetValidator.CreateUnverifiedSafeDefaults(),
            cpuTuningController: controllerFactory?.Invoke(source) ?? new CpuTuningController(source,
                admittedSavedBackend ? savedJournal ?? new CpuTuningOwnershipJournalStore(Path.Combine(directory, "cpu.json")) : null,
                admittedSavedBackend && !defaultSavedBackend ? safety => new FakeSavedBackend(source, safety) : null,
                live?.Curve, live?.CurveStore, live?.Power, live?.PowerStore));
        runtime.Initialize(configuration, writable ? PowerSource.PluggedIn : PowerSource.Battery);
        await runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(verified, runtime.GetStatus().DeviceVerified);
        return new(runtime, device, provider, source, gate, hub, power);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }

    private sealed record TestContext(ServiceControlRuntime Runtime, BatteryTestDevice Device,
        FakeProvider Provider, FakeObservationSource Source, LowPowerGate Gate, FakeHub Hub, FakePowerState Power);

    private sealed class FakeObservationSource : ICpuTuningObservationSource
    {
        public int Calls { get; private set; }
        public Action? AfterInspection { get; set; }
        internal Action? BeforeIdentity;
        internal Action? BeforeSavedSend;
        internal Action? AfterSavedRead;
        internal byte[]? Bytes;
        internal int Submissions;
        internal int SavedReads;
        public DeviceIdentity? ReadIdentity()
        {
            Calls++;
            BeforeIdentity?.Invoke();
            return new("RZ09-0528", 0x1532, 0x02C6, "2.02");
        }
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity)
        {
            Calls++;
            SavedReads++;
            if (Bytes is not null)
            {
                var result = new SavedCpuFirmwareReadResult(SavedCpuFirmwareReadStatus.Observed, Rz090528CpuFirmwareCodec.DecodeSaved(Bytes),
                    Rz090528CpuFirmwareCodec.DecodeSaved(Bytes), 0, 0);
                AfterSavedRead?.Invoke();
                return result;
            }
            return new(SavedCpuFirmwareReadStatus.Unavailable, null, null, 5, 5);
        }
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend)
        {
            Calls++;
            AfterInspection?.Invoke();
            return new(DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available);
        }
    }

    private sealed class FakeProvider(IBladeDevice device) : IVerifiedBladeDeviceProvider
    {
        public bool Available { get; set; } = true;
        internal Action? BeforeVerify;
        public FirmwareCompatibilityStatus FirmwareCompatibility => FirmwareCompatibilityStatus.Tested;
        public bool RequiresUntestedFirmwareOverride => false;
        public void SetAllowUntestedFirmware(bool enabled) { }
        public bool TryGetVerifiedDevice(out IBladeDevice result)
        {
            BeforeVerify?.Invoke();
            result = device;
            return Available;
        }
    }

    private sealed class FakePowerState : IPowerStateProvider
    {
        internal PowerSource Power = PowerSource.Battery;
        internal Action? OnCapture;
        public PowerSource CapturePowerSource() { OnCapture?.Invoke(); return Power; }
        public int? CaptureBatteryPowerFlowMilliwatts() => null;
    }

    private sealed class FakeSavedBackend(FakeObservationSource source,
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> safety) : ICpuSavedTuningBackend
    {
        public Task<bool> CheckWriteAdmissionAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token) =>
            Task.FromResult(safety(identity, command));
        public Task<SavedCpuFirmwareReadResult> ReadAsync(DeviceIdentity identity, CancellationToken token) =>
            Task.FromResult(source.ReadSaved(identity));
        public Task<CpuSavedTuningSubmission> SendAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token)
        {
            source.BeforeSavedSend?.Invoke();
            if (!safety(identity, command)) { return Task.FromResult(CpuSavedTuningSubmission.NotSubmitted); }
            source.Bytes = CpuSavedTuningPlan.Simulate(source.Bytes!, command);
            source.Submissions++;
            return Task.FromResult(CpuSavedTuningSubmission.Acknowledged);
        }
    }

    private sealed class FakePresenceMonitor : IControllerPresenceMonitor
    {
        public ControllerPresenceObservation Observe() => new([], null);
    }

    private sealed class FakeFirmwareSource : IFirmwareVersionSource
    {
        public FirmwareVersions Capture() => FirmwareVersions.Unavailable();
    }

    private sealed class FakeHub : IStatusSubscriptionHub
    {
        public List<ServiceStatusSnapshot> Published { get; } = [];
        public void Publish(ServiceStatusSnapshot snapshot) => Published.Add(snapshot);
        public StatusSubscriptionLease Subscribe(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
