using System.Security.Cryptography;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// Developer experiment only. It deliberately does not alter pad-required production admission.
internal sealed class Rz090528PadlessHyperBoostValidation(
    IValidationActorFactory actors, ICoolingPadDiscoverySource pads, Func<bool> isolated,
    Rz090528PadlessNoLoadValidation? noLoad = null)
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
    private int started;

    internal static Rz090528PadlessHyperBoostValidation CreateWindows(Func<bool> isolated,
        Rz090528PadlessNoLoadValidation? noLoad = null)
    {
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
            () => identity.ReadExactIdentity().Failure is null && isolated(), noLoad);
    }

    public async Task<PerformanceFanSetterValidationResult> RunAsync(
        PerformanceFanValidationTarget target, CancellationToken token)
    {
        if (target != PerformanceFanValidationTarget.HyperBoostFromPerformance
            || Interlocked.Exchange(ref started, 1) != 0)
        {
            return Rejected(target, PerformanceFanValidationFailure.WrongTarget);
        }
        bool innerRunStarted = false;
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
                new GuardedActors(actors, pads, isolated, pair => FailureReconciliation = pair), TimeProvider.System,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), padlessNoLoad: noLoad);
            innerRunStarted = true;
            return await runner.RunAsync(target, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Rejected(target, PerformanceFanValidationFailure.Canceled, possibleWrite: innerRunStarted);
        }
        catch
        {
            return Rejected(target, innerRunStarted ? PerformanceFanValidationFailure.ApplyWriteIndeterminate
                : PerformanceFanValidationFailure.OpenFailed, possibleWrite: innerRunStarted);
        }
    }

    private static PerformanceFanSetterValidationResult Rejected(PerformanceFanValidationTarget target,
        PerformanceFanValidationFailure failure, bool possibleWrite = false) => new(target, null, null, null, [], null,
            possibleWrite, false, false, false, false, false, possibleWrite, possibleWrite, failure);

    private sealed class GuardedActors(IValidationActorFactory inner, ICoolingPadDiscoverySource pads,
        Func<bool> isolated, Action<PerformanceFanThermalPair?> reconciliation) : IValidationActorFactory
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

            var session = new GuardedSession(actor.Session, context, pads, isolated, reconciliation);
            return new(new ValidationActor(session, actor.BatteryCommands, actor.TemporaryFullChargeCommands), null);
        }
    }

    internal sealed class GuardedSession(IRazerCommandSession inner, ValidationRunContext context,
        ICoolingPadDiscoverySource pads, Func<bool> isolated,
        Action<PerformanceFanThermalPair?>? reconciliation = null) : IRazerCommandSession, IRz090528NoLoadSnapshotSource
    {
        private int nextWrite;
        private volatile bool failed;
        private bool snapshotUncertain;
        private static readonly byte[][] Writes = [[1, 1, 7, 0], [1, 2, 7, 0], [1, 1, 2, 0], [1, 2, 2, 0]];
        public RazerCommandSessionDisposition Disposition => inner.Disposition;

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

                byte mode = baseline ? (byte)2 : (byte)7;
                for (byte channel = 1; channel <= 2; channel++)
                {
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
                if (nextWrite >= Writes.Length || !command.Arguments.Span.SequenceEqual(Writes[nextWrite]))
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
                    byte mode = nextWrite == 0 ? (byte)2 : (byte)7;
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

        internal void MarkSnapshotUncertain()
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
                if (!snapshotUncertain && (failed || nextWrite is > 0 and < 4)
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
            finally { await inner.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
