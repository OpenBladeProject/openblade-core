using System.Security.Cryptography;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// Developer experiment only. It deliberately does not alter pad-required production admission.
internal sealed class Rz090528PadlessHyperBoostValidation(
    IValidationActorFactory actors, ICoolingPadDiscoverySource pads, Func<bool> isolated,
    Rz090528PadlessNoLoadValidation? noLoad = null, Rz090528PadlessLoadedValidation? loaded = null,
    Rz090528PadlessBatteryBoundaryValidation? batteryBoundary = null, Func<bool>? batteryReadIsolation = null,
    ExactHyperBoostValidationObserver? observer = null)
    : IPerformanceFanSetterValidationRunner
{
    internal const string PlanSha256 = "53436C5DE5B19301FFDDA9EB3670DA62F6440B9CA5168A81878FE58386DB8CD1";
    internal static byte[] ReadPlan()
    {
        using Stream stream = typeof(Rz090528PadlessHyperBoostValidation).Assembly.GetManifestResourceStream(
            "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_PadlessHyperBoostImmediatePlan.json")
            ?? throw new InvalidOperationException("The reviewed plan is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        byte[] plan = bytes.ToArray();
        ValidatePlan(plan);
        return plan;
    }

    internal static void ValidatePlan(ReadOnlySpan<byte> plan)
    {
        if (Convert.ToHexString(SHA256.HashData(plan)) != PlanSha256)
        {
            throw new InvalidOperationException("The reviewed immediate plan hash does not match.");
        }
    }
    internal bool FirstAbsenceConfirmed { get; private set; }
    internal bool SecondAbsenceConfirmed { get; private set; }
    internal double AbsenceSampleElapsedMilliseconds { get; private set; }
    internal DateTimeOffset? FirstAbsenceObservedAtUtc { get; private set; }
    internal DateTimeOffset? SecondAbsenceObservedAtUtc { get; private set; }
    internal PerformanceFanThermalPair? FailureReconciliation { get; private set; }
    internal Rz090528NoLoadResult? NoLoadResult => noLoad?.Result;
    internal Rz090528LoadedResult? LoadedResult => loaded?.Result;
    internal Rz090528BatteryBoundaryResult? BatteryBoundaryResult => batteryBoundary?.Result;
    internal bool HardwareOpened { get; private set; }
    internal bool DisposalConfirmed { get; private set; }
    private int started;

    internal bool IsStockBaselineEligible() => isolated() && pads.Observe() == CoolingPadConnectionState.Absent;

    // Query-only baseline entry point. It never invokes the setter validation runner.
    internal async Task<StockBaselineQueries> OpenStockBaselineQueriesAsync()
    {
        if (!isolated() || pads.Observe() != CoolingPadConnectionState.Absent)
        {
            throw new InvalidOperationException("Baseline identity, isolation or pad absence not confirmed.");
        }
        var context = new ValidationRunContext();
        var opened = await actors.OpenAsync(context).ConfigureAwait(false);
        if (opened.Failure is not null || opened.Value is not { } actor)
        {
            if (opened.Value is not null)
            {
                await opened.Value.DisposeAsync().ConfigureAwait(false);
            }

            throw new InvalidOperationException("Baseline query actor could not open.");
        }
        return new(new GuardedSession(actor.Session, context, pads, isolated));
    }

    internal sealed class StockBaselineQueries(GuardedSession session) : IAsyncDisposable
    {
        internal Task<Rz090528NoLoadSnapshot> ReadAsync(CancellationToken token) => session.ReadAsync(true, token);
        public ValueTask DisposeAsync() => session.DisposeAsync();
    }

    internal static Rz090528PadlessHyperBoostValidation CreateWindows(Func<bool> isolated,
        Rz090528PadlessNoLoadValidation? noLoad = null, Rz090528PadlessLoadedValidation? loaded = null,
        Rz090528PadlessBatteryBoundaryValidation? batteryBoundary = null, Func<bool>? batteryReadIsolation = null,
        ExactHyperBoostValidationObserver? observer = null)
    {
        if (batteryBoundary is not null && batteryReadIsolation is null)
        {
            throw new ArgumentException("Battery boundary reads require an independent read-only isolation guard.");
        }
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var identity = new WindowsHostIdentitySource(registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            WindowsHostIdentitySource.Rz090528Identity with { FirmwareAdmission = FirmwareAdmissionScope.ExactVersions });
        var guard = new WindowsHardwareAccessGuard(new RunningProcessSource(), new WindowsServiceSource());
        var opener = new WindowsRazerHidSessionOpener(identity, guard, native,
            ControllerExclusivityPolicy.Strict, new HidHandleIdentity(0x1532, 0x02C6, 1, 2, 91));
        var factory = new ValidationActorFactory(opener, new OverlappedFeatureOperation(native), guard,
            TimeProvider.System, TimeSpan.FromSeconds(3), ControllerExclusivityPolicy.Strict,
            TimeSpan.FromMilliseconds(20));
        return new(factory, new WindowsHyperBoostEligibilitySource(native),
            () => identity.ReadExactIdentity().Failure is null && isolated(), noLoad, loaded, batteryBoundary,
            batteryReadIsolation is null ? null : () => identity.ReadExactIdentity().Failure is null && batteryReadIsolation(), observer);
    }

    public async Task<PerformanceFanSetterValidationResult> RunAsync(
        PerformanceFanValidationTarget target, CancellationToken token)
    {
        // The immediate round trip may start from any AC Automatic mode; the observation
        // variants remain bound to their reviewed Performance plans.
        byte? baseMode = target switch
        {
            PerformanceFanValidationTarget.HyperBoostFromPerformance => 2,
            PerformanceFanValidationTarget.HyperBoost when noLoad is null && loaded is null && batteryBoundary is null => 0,
            PerformanceFanValidationTarget.HyperBoostFromSilent when noLoad is null && loaded is null && batteryBoundary is null => 5,
            _ => null,
        };
        if (baseMode is null || Interlocked.Exchange(ref started, 1) != 0)
        {
            return Rejected(target, PerformanceFanValidationFailure.WrongTarget);
        }
        bool innerRunStarted = false;
        PerformanceFanSetterValidationResult Complete(PerformanceFanSetterValidationResult result)
        {
            batteryBoundary?.CompleteFromRunner(result, DisposalConfirmed);
            return result;
        }
        try
        {
            _ = ReadPlan();
            token.ThrowIfCancellationRequested();
            FirstAbsenceConfirmed = isolated() && pads.Observe() == CoolingPadConnectionState.Absent;
            FirstAbsenceObservedAtUtc = DateTimeOffset.UtcNow;
            if (!FirstAbsenceConfirmed)
            {
                return Rejected(target, PerformanceFanValidationFailure.WrongTarget);
            }

            long first = TimeProvider.System.GetTimestamp();
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1,
                    500 - AbsenceSampleElapsedMilliseconds)), token).ConfigureAwait(false);
                AbsenceSampleElapsedMilliseconds = TimeProvider.System.GetElapsedTime(first).TotalMilliseconds;
            } while (AbsenceSampleElapsedMilliseconds < 500);
            SecondAbsenceConfirmed = AbsenceSampleElapsedMilliseconds >= 500
                && isolated() && pads.Observe() == CoolingPadConnectionState.Absent;
            SecondAbsenceObservedAtUtc = DateTimeOffset.UtcNow;
            if (!SecondAbsenceConfirmed)
            {
                return Rejected(target, PerformanceFanValidationFailure.WrongTarget);
            }

            var runner = new PerformanceFanSetterValidationRunner(() => new ValidationRunContext(),
                new GuardedActors(actors, pads, isolated, pair => FailureReconciliation = pair,
                    () => HardwareOpened = true, () => DisposalConfirmed = true,
                    batteryBoundary is null ? null : batteryReadIsolation ?? (() => false), batteryBoundary is not null,
                    baseMode.Value), TimeProvider.System,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), padlessNoLoad: noLoad, padlessLoaded: loaded, padlessBatteryBoundary: batteryBoundary, exactObserver: observer);
            innerRunStarted = true;
            PerformanceFanSetterValidationResult result = await runner.RunAsync(target, token).ConfigureAwait(false);
            return Complete(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Complete(Rejected(target, PerformanceFanValidationFailure.Canceled, possibleWrite: innerRunStarted));
        }
        catch
        {
            return Complete(Rejected(target, innerRunStarted ? PerformanceFanValidationFailure.ApplyWriteIndeterminate
                : PerformanceFanValidationFailure.OpenFailed, possibleWrite: innerRunStarted));
        }
    }

    private static PerformanceFanSetterValidationResult Rejected(PerformanceFanValidationTarget target,
        PerformanceFanValidationFailure failure, bool possibleWrite = false) => new(target, null, null, null, [], null,
            possibleWrite, false, false, false, false, false, possibleWrite, possibleWrite, failure);

    private sealed class GuardedActors(IValidationActorFactory inner, ICoolingPadDiscoverySource pads,
        Func<bool> isolated, Action<PerformanceFanThermalPair?> reconciliation,
        Action opened, Action disposed, Func<bool>? boundaryReadIsolation = null,
        bool boundaryMode = false, byte baseMode = 2) : IValidationActorFactory
    {
        public async ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            if (!isolated() || pads.Observe() != CoolingPadConnectionState.Absent)
            {
                return new(null, HidSessionFailureKind.IdentityBlocked);
            }

            var result = await inner.OpenAsync(context).ConfigureAwait(false);
            if (result.Failure is not null || result.Value is not { } actor)
            {
                return result;
            }

            opened();
            var session = new GuardedSession(actor.Session, context, pads, isolated, reconciliation, disposed, boundaryReadIsolation, boundaryMode, baseMode);
            return new(new ValidationActor(session, actor.BatteryCommands, actor.TemporaryFullChargeCommands), null);
        }
    }

    internal sealed class GuardedSession(IRazerCommandSession inner, ValidationRunContext context,
        ICoolingPadDiscoverySource pads, Func<bool> isolated,
        Action<PerformanceFanThermalPair?>? reconciliation = null, Action? disposed = null,
        Func<bool>? boundaryReadIsolation = null, bool boundaryMode = false, byte baseMode = 2)
        : IRazerCommandSession, IRz090528NoLoadSnapshotSource, IRz090528BatteryBoundarySource, IExactExternalWriteGuard
    {
        private int nextWrite;
        private volatile bool failed;
        private bool snapshotUncertain;
        // Enable pair, then restoration of the exact starting AC mode (2 Performance, 0 Balanced, 5 Silent).
        private readonly byte[][] writes =
            [[1, 1, 7, 0], [1, 2, 7, 0], [1, 1, baseMode, 0], [1, 2, baseMode, 0]];
        public RazerCommandSessionDisposition Disposition => inner.Disposition;

        public void RetainQueryFailure(byte classId, byte commandId, FirmwareQueryOutcome outcome) =>
            inner.RetainQueryFailure(classId, commandId, outcome);

        public async Task<Rz090528BatteryBoundarySnapshot> ReadBatteryBoundaryAsync(CancellationToken token)
        {
            Rz090528BatteryBoundarySnapshot Refuse(string reason)
            {
                MarkSnapshotUncertain();
                return new(null, null, null, false, reason);
            }
            if (!boundaryMode || boundaryReadIsolation is null || failed)
            {
                return Refuse("BoundaryReadNotAdmitted");
            }
            // Four queries must leave eight transactions for the existing guarded restoration.
            if (context.RemainingTransactions < 12) { return Refuse("CleanupTransactionReserve"); }
            try
            {
                async Task<byte[]> Query(RazerCommandSpec command)
                {
                    token.ThrowIfCancellationRequested();
                    if (failed || !boundaryReadIsolation()) { throw new InvalidOperationException(); }
                    // Recheck after a potentially synchronous isolation probe, before enqueue.
                    token.ThrowIfCancellationRequested();
                    if (failed) { throw new InvalidOperationException(); }
                    var response = await ReadAsync(command, TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (failed || response.Failure is not null) { throw new InvalidOperationException(); }
                    return response.Value.ToArray();
                }
                var powerCommand = new RazerCommandSpec(7, 0x8C, new byte[] { 0, 0 }, 2);
                byte[] before = await Query(powerCommand).ConfigureAwait(false);
                byte[] first = await Query(new RazerCommandSpec(0x0D, 0x82, new byte[] { 1, 1, 0, 0 }, 4)).ConfigureAwait(false);
                byte[] second = await Query(new RazerCommandSpec(0x0D, 0x82, new byte[] { 1, 2, 0, 0 }, 4)).ConfigureAwait(false);
                byte[] after = await Query(powerCommand).ConfigureAwait(false);
                // 0011 battery, 0711 100 W USB-C, 1111 full AC; the boundary decides which its plan admits.
                bool Power(byte[] value) => value.Length == 2 && value[1] == 0x11 && value[0] is 0 or 0x07 or 0x11;
                bool Thermal(byte[] value, byte channel) => value.Length == 4 && value[0] == 1
                    && value[1] == channel && value[2] is 0 or 2 or 3 or 7 && value[3] == 0;
                // Malformed values poison. A well-formed bracket that changed during a physical
                // plug event is returned as observed; the boundary classifies it as transitional.
                if (!Power(before) || !Power(after) || !Thermal(first, 1) || !Thermal(second, 2))
                {
                    return Refuse("BoundaryPowerOrThermalAmbiguous");
                }
                bool absent = pads.Observe() == CoolingPadConnectionState.Absent;
                if (!boundaryReadIsolation()) { return Refuse("BoundaryIsolationLost"); }
                token.ThrowIfCancellationRequested();
                if (failed) { return Refuse("BoundaryReadUncertain"); }
                return new(Convert.ToHexString(before), new(Convert.ToHexString(first), Convert.ToHexString(second)),
                    Convert.ToHexString(after), absent, null);
            }
            catch { return Refuse("BoundaryReadUncertain"); }
        }

        public async Task<Rz090528NoLoadSnapshot> ReadAsync(bool baseline, CancellationToken token)
        {
            // Five read transactions plus eight reserved for independent restoration/readback.
            if (context.RemainingTransactions < 13)
            {
                return new(null, "CleanupTransactionReserve", true);
            }

            try
            {
                if (failed || !isolated())
                {
                    return new(null, "IsolationOrSessionLost", false);
                }

                // A timed-out loaded snapshot may have been blocked in the synchronous isolation prefix.
                token.ThrowIfCancellationRequested();
                if (failed) { return new(null, "SnapshotLaneClosed", false); }

                byte mode = baseline ? (byte)2 : (byte)7;
                for (byte channel = 1; channel <= 2; channel++)
                {
                    token.ThrowIfCancellationRequested();
                    if (failed) { return new(null, "SnapshotLaneClosed", false); }
                    var read = await ReadAsync(new RazerCommandSpec(0x0D, 0x82,
                        new byte[] { 1, channel, 0, 0 }, 4), TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    if (read.Failure is not null || !read.Value.Span.SequenceEqual(new byte[] { 1, channel, mode, 0 }))
                    {
                        failed = true;
                        return new(null, "ThermalReadOrPairAmbiguous", false);
                    }
                }
                if (!await FullPowerAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false))
                {
                    return new(null, "FullPowerNotConfirmed", false);
                }

                CoolingPadConnectionState pad = pads.Observe();
                if (pad != CoolingPadConnectionState.Absent)
                {
                    return new(null, "PadAbsenceLost", true);
                }

                var fans = new int[2];
                for (byte selector = 1; selector <= 2; selector++)
                {
                    if (!isolated())
                    {
                        return new(null, "IsolationLost", false);
                    }

                    token.ThrowIfCancellationRequested();
                    if (failed) { return new(null, "SnapshotLaneClosed", false); }
                    var read = await ReadAsync(new RazerCommandSpec(0x0D, 0x88,
                        new byte[] { 1, selector, 0 }, 3), TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    if (read.Failure is not null || !FanRpmProtocol.TryDecodeQuery(read.Value.Span,
                        (FanRpmSelector)selector, out fans[selector - 1]))
                    {
                        failed = true;
                        return new(null, "FanReadAmbiguous", false);
                    }
                }
                return isolated() ? new(new(fans[0], fans[1]), null, true)
                    : new(null, "IsolationLost", false);
            }
            catch
            {
                failed = true;
                return new(null, "SnapshotReadAmbiguous", false);
            }
        }

        public async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan timeout, CancellationToken token)
        {
            bool write = command.ClassId == 0x0D && command.CommandId == 2;
            bool query = command.ClassId == 0x0D && command.CommandId == 0x82
                && (command.Arguments.Span.SequenceEqual(new byte[] { 1, 1, 0, 0 })
                    || command.Arguments.Span.SequenceEqual(new byte[] { 1, 2, 0, 0 }));
            if (!isolated() || (write && failed) || (!query && !write))
            {
                return Block();
            }

            if (write)
            {
                token.ThrowIfCancellationRequested();
                if (nextWrite >= writes.Length || !command.Arguments.Span.SequenceEqual(writes[nextWrite]))
                {
                    return Block();
                }
                // Enable requires fresh robust absence. Known-state Performance escape does not.
                bool enabling = nextWrite < 2;
                if (enabling && pads.Observe() != CoolingPadConnectionState.Absent)
                {
                    return Block();
                }

                // This adds a second stable baseline and independently reconciles mode 7
                // before restoration, even when the caller canceled after candidate readback.
                if (nextWrite is 0 or 2)
                {
                    byte mode = nextWrite == 0 ? baseMode : (byte)7;
                    for (byte channel = 1; channel <= 2; channel++)
                    {
                        var observed = await ReadAsync(new RazerCommandSpec(0x0D, 0x82,
                            new byte[] { 1, channel, 0, 0 }, 4), timeout, token).ConfigureAwait(false);
                        if (observed.Failure is not null || !observed.Value.Span.SequenceEqual(new byte[] { 1, channel, mode, 0 }))
                        {
                            return Block();
                        }
                    }
                }
                if (!await FullPowerAsync(timeout, token).ConfigureAwait(false))
                {
                    return Block();
                }
                Func<bool>? previous = command.SubmissionAllowed;
                command = command with
                {
                    SubmissionAllowed = () => (previous?.Invoke() ?? true)
                    && isolated() && (!enabling || pads.Observe() == CoolingPadConnectionState.Absent)
                };
                if (command.SubmissionAllowed() != true)
                {
                    return Block();
                }

                nextWrite++;
            }
            var result = await inner.ExecuteAsync(command, assignTransactionId, timeout, token).ConfigureAwait(false);
            failed |= result.Failure is not null;
            return result;
        }

        // Re-admits an observer's GPU write: isolation, fresh pad absence and the full-power adapter.
        public async Task<bool> ValidateExternalWriteAsync(
            ValidationRunContext runContext, TimeSpan timeout, CancellationToken token)
        {
            if (failed || !isolated() || pads.Observe() != CoolingPadConnectionState.Absent)
            {
                return false;
            }
            bool allowed = await FullPowerAsync(timeout, token).ConfigureAwait(false)
                && isolated() && pads.Observe() == CoolingPadConnectionState.Absent;
            failed |= !allowed;
            return allowed;
        }

        private async Task<bool> FullPowerAsync(TimeSpan timeout, CancellationToken token)
        {
            var result = await ReadAsync(new RazerCommandSpec(7, 0x8C, new byte[] { 0, 0 }, 2), timeout, token).ConfigureAwait(false);
            return result.Failure is null && result.Value.Span.SequenceEqual(new byte[] { 0x11, 0x11 });
        }

        private Task<HidSessionResult<ReadOnlyMemory<byte>>> ReadAsync(RazerCommandSpec command,
            TimeSpan timeout, CancellationToken token) => context.TryAllocateTransaction(out byte transaction)
                ? inner.ExecuteAsync(command, () => TransactionIdAssignment.Valid(transaction), timeout, token)
                : Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Exhausted));

        private HidSessionResult<ReadOnlyMemory<byte>> Block()
        {
            failed = true;
            return new(default, HidSessionFailureKind.IdentityBlocked);
        }

        public void MarkSnapshotUncertain()
        {
            failed = true;
            snapshotUncertain = true;
        }

        public async ValueTask DisposeAsync()
        {
            // Never upgrade an ambiguous run to success. Preserve independent read-only
            // observations for recovery when the native session is still usable.
            try
            {
                if (!boundaryMode && !snapshotUncertain && (failed || nextWrite is > 0 and < 4)
                    && inner.Disposition == RazerCommandSessionDisposition.Healthy)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var observed = new List<string>();
                    for (byte channel = 1; channel <= 2; channel++)
                    {
                        if (!isolated())
                        {
                            break;
                        }

                        var result = await ReadAsync(new RazerCommandSpec(0x0D, 0x82,
                            new byte[] { 1, channel, 0, 0 }, 4), TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
                        if (result.Failure is not null || result.Value.Length != 4)
                        {
                            break;
                        }

                        observed.Add(Convert.ToHexString(result.Value.Span));
                    }
                    if (observed.Count == 2)
                    {
                        reconciliation?.Invoke(new(observed[0], observed[1]));
                    }
                }
            }
            catch { }
            finally
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                disposed?.Invoke();
            }
        }
    }
}
