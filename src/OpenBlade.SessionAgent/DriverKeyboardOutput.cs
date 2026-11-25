using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent;

internal enum DriverOutputDecision
{
    Allowed,
    TargetChanged,
    StopRequested,
}

/// <summary>
/// The worker's only path to driver output. It records which keys Windows has been
/// sent down and not yet up, so a stop that refuses output can release them instead
/// of leaving a key stuck down.
/// </summary>
internal sealed class DriverKeyboardOutput(Func<DriverOutputDecision> decide, Action<SnapTapKeyStroke> submit)
{
    private readonly HashSet<ushort> delivered = [];

    internal int DeliveredCount => delivered.Count;
    internal bool ReleaseUncertain { get; private set; }

    /// <summary>A/D output was refused by a stop request before anything in its batch was submitted.</summary>
    internal bool SnapStopRefused { get; private set; }

    /// <summary>Function-row output was refused by a stop request before submission.</summary>
    internal bool FunctionStopRefused { get; private set; }

    /// <summary>A function-row press the stop refused; it never reached Windows.</summary>
    internal ushort? FunctionRefusedDown { get; private set; }

    internal bool IsDelivered(ushort scanCode) => delivered.Contains(scanCode);

    /// <summary>The A/D processor's sender: returns how many strokes were submitted.</summary>
    internal int SendBatch(SnapTapOutputBatch batch)
    {
        for (int index = 0; index < batch.Count; index++)
        {
            DriverOutputDecision decision = Send(index == 0 ? batch.First : batch.Second);
            if (decision != DriverOutputDecision.Allowed)
            {
                // Only a refusal before anything in this batch was submitted can be clean.
                SnapStopRefused |= index == 0 && decision == DriverOutputDecision.StopRequested;
                return index;
            }
        }
        return batch.Count;
    }

    /// <summary>
    /// The function-row processor's sender. A refusal throws, so that processor
    /// still treats the stroke as uncertain and faults; only this class knows it
    /// was refused before submission.
    /// </summary>
    internal void SendFunctionRow(SnapTapKeyStroke stroke)
    {
        DriverOutputDecision decision = Send(stroke);
        if (decision != DriverOutputDecision.Allowed)
        {
            FunctionStopRefused |= decision == DriverOutputDecision.StopRequested;
            if (decision == DriverOutputDecision.StopRequested && stroke.IsDown)
            {
                FunctionRefusedDown = stroke.ScanCode;
            }
            throw new KeyboardSubmissionDeferredException();
        }
    }

    /// <summary>
    /// Submits <paramref name="stroke"/> only when the decision allows it. A refusal
    /// is decided before submission, so nothing reached the driver.
    /// </summary>
    internal DriverOutputDecision Send(SnapTapKeyStroke stroke)
    {
        DriverOutputDecision decision = decide();
        if (decision != DriverOutputDecision.Allowed)
        {
            return decision;
        }
        try
        {
            submit(stroke);
        }
        catch (KeyboardSubmissionDeferredException)
        {
            // The transport's own final check refused before DeviceIoControl, so
            // nothing was submitted. Only a stop that still holds counts as one.
            DriverOutputDecision again = decide();
            return again == DriverOutputDecision.Allowed ? DriverOutputDecision.TargetChanged : again;
        }
        if (stroke.IsDown)
        {
            delivered.Add(stroke.ScanCode);
        }
        else
        {
            delivered.Remove(stroke.ScanCode);
        }
        return decision;
    }

    /// <summary>
    /// Sends an up for every key Windows may still hold down. Returns true only when
    /// every release was submitted; a failed submission may have reached the driver.
    /// </summary>
    internal bool TryReleaseDelivered()
    {
        try
        {
            foreach (ushort scanCode in delivered.Order().ToArray())
            {
                if (Send(new(scanCode, false)) != DriverOutputDecision.Allowed)
                {
                    return false;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReleaseUncertain = true;
            return false;
        }
        return delivered.Count == 0;
    }
}

// After the supervisor requests a stop, the input loop refuses new output before
// submission, which faults the processor. That stop is clean only when the refusal
// is the sole failure and every key this worker delivered was released again; then
// a stalled worker is rearmed like any other clean stall. A possible submission,
// any other failure or an unreleased key keeps RecoveryRequired.
internal readonly record struct StopRefusalExit(
    bool SnapStopRefused, bool FunctionStopRefused, bool PublishFailed, DiagnosticFailure? SessionFailure,
    bool SnapFaulted, DiagnosticFailure? SnapFailure, bool FunctionFaulted, DiagnosticFailure? FunctionFailure,
    DiagnosticFailure? LoopFailure, bool FunctionKeysSettled)
{
    // FunctionKeysSettled: every function-row key still physically held was either
    // delivered, and so is released, or is the press the stop refused. A held media
    // key, which an ordinary stop waits for, keeps the failure.
    internal bool MayRelease =>
        (SnapStopRefused || FunctionStopRefused) && !PublishFailed && SessionFailure is null && FunctionKeysSettled
        && (SnapFaulted
            ? SnapStopRefused && SnapFailure is
            {
                Operation: "SendOutput", Reason: "OutputCountMismatch", MayHaveSubmitted: false, Cause: null, Related: null,
            }
            : SnapFailure is null)
        && (FunctionFaulted ? FunctionStopRefused && FunctionFailure is not null : FunctionFailure is null)
        && (LoopFailure is null || ReferenceEquals(LoopFailure, SnapFailure) || ReferenceEquals(LoopFailure, FunctionFailure));

    internal bool IsClean(bool keysReleased, bool closed, bool functionStopped) =>
        MayRelease && keysReleased && closed && functionStopped;
}
