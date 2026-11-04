namespace OpenBlade.Protocol;

// A retained renderer outlives individual control requests. Carry recovery
// admission into each native queue without changing its low-power cleanup policy.
internal sealed class LightingRecoveryWriteGate
{
    private Func<bool>? admission;

    internal bool IsAllowed => Volatile.Read(ref admission)?.Invoke() != false;

    internal void Set(Func<bool> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref admission, value);
    }

    internal SubmissionGuard CreateSubmissionGuard(Func<bool>? existing = null) => new(this, existing);

    internal void ThrowIfBlocked()
    {
        if (!IsAllowed)
        {
            throw Refused(WriteSubmissionState.KnownNotApplied);
        }
    }

    internal static bool IsRejection(Exception exception) => exception is BladeWriteException
    {
        FailurePhase: BladeWriteFailurePhase.ControlRecoveryAdmission,
    };

    internal static bool IsDeferral(Exception exception) => IsRejection(exception)
        && ((BladeWriteException)exception).SubmissionState == WriteSubmissionState.KnownNotApplied;

    private static BladeWriteException Refused(WriteSubmissionState state) => new(state,
        BladeWriteFailurePhase.ControlRecoveryAdmission,
        "Lighting submission is paused until the pending laptop control is reconciled.");

    internal sealed class SubmissionGuard(LightingRecoveryWriteGate owner, Func<bool>? existing)
    {
        private int refused;

        internal bool Allow()
        {
            if (!owner.IsAllowed)
            {
                Volatile.Write(ref refused, 1);
                return false;
            }
            return existing?.Invoke() != false;
        }

        internal void ThrowIfRefused(HidSessionFailureDiagnostic? diagnostic = null)
        {
            if (Volatile.Read(ref refused) != 0)
            {
                // SET batches mark partial submission explicitly. Never turn a
                // refused later row into a claim that the whole frame was unsent.
                throw Refused(diagnostic?.Phase == HidSessionFailurePhase.SetCompletion
                    ? WriteSubmissionState.Indeterminate : WriteSubmissionState.KnownNotApplied);
            }
        }
    }
}
