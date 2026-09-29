using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Developer-only composition for the captured 02C6 Performance/Automatic
/// to HyperBoost pair and its exact restoration, separate from production admission.
/// </summary>
internal static class Rz090528HyperBoostValidation
{
    internal static IPerformanceFanSetterValidationRunner CreateWindows(Func<bool> isolated,
        ExactHyperBoostValidationObserver? observer = null)
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var identity = new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            WindowsHostIdentitySource.Rz090528Identity with
            {
                FirmwareAdmission = FirmwareAdmissionScope.ExactVersions,
            });
        var guard = new WindowsHardwareAccessGuard(
            new RunningProcessSource(), new WindowsServiceSource());
        var opener = new WindowsRazerHidSessionOpener(
            identity, guard, native, ControllerExclusivityPolicy.Strict,
            new HidHandleIdentity(0x1532, 0x02C6, 0x0001, 0x0002, 91));
        TimeSpan timeout = TimeSpan.FromSeconds(3);
        var factory = new ValidationActorFactory(
            opener, new OverlappedFeatureOperation(native), guard,
            TimeProvider.System, timeout, ControllerExclusivityPolicy.Strict,
            TimeSpan.FromMilliseconds(20));
        return Create(factory, new WindowsHyperBoostEligibilitySource(native),
            () => identity.ReadExactIdentity().Failure is null && isolated(), timeout, observer);
    }

    internal static IPerformanceFanSetterValidationRunner Create(
        IValidationActorFactory actors,
        ICoolingPadDiscoverySource pads,
        Func<bool> isolated,
        TimeSpan timeout, ExactHyperBoostValidationObserver? observer = null) => new ExactTargetRunner(new PerformanceFanSetterValidationRunner(
            static () => new ValidationRunContext(),
            new GuardedActorFactory(actors, pads, isolated),
            TimeProvider.System, timeout, TimeSpan.FromSeconds(5), exactObserver: observer));

    private sealed class ExactTargetRunner(IPerformanceFanSetterValidationRunner inner)
        : IPerformanceFanSetterValidationRunner
    {
        public Task<PerformanceFanSetterValidationResult> RunAsync(
            PerformanceFanValidationTarget target, CancellationToken cancellationToken)
        {
            if (target != PerformanceFanValidationTarget.HyperBoostFromPerformance)
            {
                throw new ArgumentOutOfRangeException(nameof(target));
            }
            return inner.RunAsync(target, cancellationToken);
        }
    }

    private sealed class GuardedActorFactory(
        IValidationActorFactory inner, ICoolingPadDiscoverySource pads, Func<bool> isolated)
        : IValidationActorFactory
    {
        public async ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            if (!isolated() || pads.Observe() != CoolingPadConnectionState.PresentExact)
            {
                return new(null, HidSessionFailureKind.IdentityBlocked);
            }
            HidSessionResult<ValidationActor> opened = await inner.OpenAsync(context).ConfigureAwait(false);
            if (opened.Failure is not null || opened.Value is not { } actor)
            {
                return opened;
            }
            var session = new GuardedSession(actor.Session, pads, isolated);
            return new(new ValidationActor(session, actor.BatteryCommands, actor.TemporaryFullChargeCommands), null);
        }
    }

    internal sealed class GuardedSession(
        IRazerCommandSession inner, ICoolingPadDiscoverySource pads, Func<bool> isolated)
        : IRazerCommandSession, IExactExternalWriteGuard
    {
        private int nextWrite;
        private bool failed;
        private static readonly byte[][] Writes =
        [
            [1, 1, 7, 0], [1, 2, 7, 0], [1, 1, 2, 0], [1, 2, 2, 0],
        ];

        public RazerCommandSessionDisposition Disposition => inner.Disposition;

        public async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command, Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            bool write = command.ClassId == 0x0D && command.CommandId == 0x02;
            bool exactQuery = command.ClassId == 0x0D && command.CommandId == 0x82
                && (command.Arguments.Span.SequenceEqual(new byte[] { 1, 1, 0, 0 })
                    || command.Arguments.Span.SequenceEqual(new byte[] { 1, 2, 0, 0 }));
            if ((write && failed) || !isolated()
                || command.ClassId != 0x0D || command.CommandId is not (0x82 or 0x02))
            {
                failed = true;
                return new(default, HidSessionFailureKind.ConflictBlocked);
            }
            if (!write && !exactQuery)
            {
                failed = true;
                return new(default, HidSessionFailureKind.IdentityBlocked);
            }
            if (write)
            {
                if (nextWrite >= Writes.Length || !command.Arguments.Span.SequenceEqual(Writes[nextWrite])
                    || pads.Observe() != CoolingPadConnectionState.PresentExact)
                {
                    failed = true;
                    return new(default, HidSessionFailureKind.IdentityBlocked);
                }
                var powerCommand = new RazerCommandSpec(0x07, 0x8C, new byte[] { 0, 0 }, 2,
                    payload => payload.Span.SequenceEqual(new byte[] { 0x11, 0x11 }));
                HidSessionResult<ReadOnlyMemory<byte>> power = await inner.ExecuteAsync(
                    powerCommand, assignTransactionId, exchangeTimeout, cancellationToken).ConfigureAwait(false);
                if (power.Failure is not null || !power.Value.Span.SequenceEqual(new byte[] { 0x11, 0x11 })
                    || !isolated() || pads.Observe() != CoolingPadConnectionState.PresentExact)
                {
                    failed = true;
                    return new(default, power.Failure ?? HidSessionFailureKind.IdentityBlocked);
                }
                Func<bool>? priorSubmissionAllowed = command.SubmissionAllowed;
                command = command with
                {
                    SubmissionAllowed = () => (priorSubmissionAllowed?.Invoke() ?? true)
                        && isolated()
                        && pads.Observe() == CoolingPadConnectionState.PresentExact,
                };
                nextWrite++;
            }
            HidSessionResult<ReadOnlyMemory<byte>> result = await inner.ExecuteAsync(
                command, assignTransactionId, exchangeTimeout, cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                failed = true;
            }
            return result;
        }

        public async Task<bool> ValidateExternalWriteAsync(
            ValidationRunContext context, TimeSpan timeout, CancellationToken token)
        {
            if (failed || !isolated() || pads.Observe() != CoolingPadConnectionState.PresentExact
                || !context.TryAllocateTransaction(out byte transaction))
            {
                return false;
            }

            var power = await inner.ExecuteAsync(
                new RazerCommandSpec(0x07, 0x8C, new byte[] { 0, 0 }, 2,
                    value => value.Span.SequenceEqual(new byte[] { 0x11, 0x11 })),
                () => TransactionIdAssignment.Valid(transaction), timeout, token).ConfigureAwait(false);
            bool allowed = power.Failure is null
                && power.Value.Span.SequenceEqual(new byte[] { 0x11, 0x11 })
                && isolated() && pads.Observe() == CoolingPadConnectionState.PresentExact;
            failed |= !allowed;
            return allowed;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
