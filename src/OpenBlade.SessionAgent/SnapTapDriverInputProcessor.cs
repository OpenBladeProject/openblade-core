namespace OpenBlade.SessionAgent;

/// <summary>Processes the A/D stream already intercepted by the device driver.</summary>
internal sealed class SnapTapDriverInputProcessor
{
    private readonly Func<SnapTapOutputBatch, int> send;
    private readonly SnapTapPriorityCore core;
    internal OpenBlade.Contracts.DiagnosticFailure? Failure { get; private set; }

    internal SnapTapDriverInputProcessor(
        Func<SnapTapOutputBatch, int> send,
        TimeProvider? timeProvider = null,
        TimeSpan? repeatInterval = null)
    {
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        core = new SnapTapPriorityCore(Send, timeProvider, repeatInterval);
    }

    internal bool Enabled => core.Enabled;
    internal bool Faulted => core.Faulted;
    internal bool ArePhysicallyReleased => core.ArePhysicallyReleased;
    internal ushort Winner => core.Winner;

    internal bool Enable() => core.Enable();

    internal bool Handle(SnapTapKeyStroke key)
    {
        if (key.ScanCode is not (0x1E or 0x20))
        {
            // The driver contract admits only this pair. Do not turn an
            // unexpected intercepted packet into an arbitrary keyboard writer.
            core.Poison();
            return false;
        }
        if (core.Faulted)
        {
            return false;
        }

        SnapTapInputResult result = core.Handle(key);
        if (core.Faulted)
        {
            return false;
        }
        if (!result.Suppress)
        {
            // The Windows hook backend forwards the original physical event.
            // A driver hook has already consumed it, so forward it exactly once
            // through the same typed driver output path as priority changes.
            try
            {
                if (Send(new(key, default, 1)) != 1)
                {
                    core.Poison();
                }
            }
            catch
            {
                core.Poison();
            }
        }

        return !core.Faulted;
    }

    internal void RepeatRestoredWinner() => core.RepeatRestoredWinner();

    internal void Disable() => core.Disable();

    internal void Poison() => core.Poison();

    private int Send(SnapTapOutputBatch batch)
    {
        try
        {
            int accepted = send(batch);
            if (accepted != batch.Count)
            {
                Failure ??= SnapTapDiagnosticFailure.Create(
                    SnapTapDiagnosticFailure.Worker,
                    "SendOutput",
                    "InputLoop",
                    "OutputCountMismatch",
                    accepted > 0);
            }
            return accepted;
        }
        catch (Exception exception)
        {
            // The native callback may have submitted before reporting failure.
            // Capture it before the priority core poisons and suppresses the exception.
            Failure ??= SnapTapDiagnosticFailure.FromException(
                SnapTapDiagnosticFailure.Worker,
                "SendOutput",
                "InputLoop",
                "OutputSubmissionFailed",
                exception,
                mayHaveSubmitted: true);
            throw;
        }
    }
}
