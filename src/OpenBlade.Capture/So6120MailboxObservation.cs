namespace OpenBlade.Capture;

internal sealed record So6120MailboxEnvelope(byte Bank, byte Index, byte Count, byte Command, byte Status, byte Payload);

internal sealed record So6120MailboxObservationResult(bool OpenAttempted, bool ScansMatched,
    So6120NativeCloseEvidence? CloseEvidence, string? Failure, IReadOnlyList<string> ReleaseFailures,
    IDisposable? RetainedPin, IDisposable? RetainedReservation)
{
    internal bool Success => ScansMatched && Failure is null && ReleaseFailures.Count == 0
        && CloseEvidence?.State == So6120NativeCloseState.Closed;
}

// The only port sequence is index selection on 300 and one byte read on 301.
// No setting, command, attempt-marker or prior evidence write is made here.
internal static class So6120MailboxObservation
{
    private static readonly byte[] ScanIndices = [0x93, 0x90, 0x91, 0x92, 0x94, 0xA0, 0x93];
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(2);

    internal static So6120MailboxObservationResult Observe(ISo6120WindowsDutyNative native, TimeProvider clock,
        Func<So6120SupervisedMetadata> admit, Func<So6120OwnedInpOutHandle, So6120InpOutBytePort> attach,
        Action<object> record, CancellationToken cancellationToken)
    {
        IDisposable? reservation = null, pin = null;
        So6120OwnedInpOutHandle? handle = null;
        So6120InpOutBytePort? port = null;
        bool openAttempted = false, scansMatched = false;
        string? failure = null;
        var releaseFailures = new List<string>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            reservation = native.ReserveRun();
            pin = native.PinDriverFile(admit().Driver);
            record(new { phase = "opening" });
            _ = admit();
            cancellationToken.ThrowIfCancellationRequested();
            openAttempted = true;
            handle = native.OpenDevice();
            port = attach(handle);

            record(new { phase = "scanStarted", scan = 1 });
            _ = admit();
            So6120MailboxEnvelope first = Scan(port, clock, record, 1, cancellationToken);
            record(new { phase = "scanStarted", scan = 2 });
            _ = admit();
            So6120MailboxEnvelope second = Scan(port, clock, record, 2, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            scansMatched = first == second;
            record(new { phase = "comparison", scansMatched, first, second });
            if (!scansMatched) { failure = "The two command-bracketed mailbox observations differ."; }
        }
        catch (Exception error)
        {
            failure = error is System.ComponentModel.Win32Exception nativeError
                ? $"{error.GetType().Name}: {error.Message} (Win32 {nativeError.NativeErrorCode})"
                : $"{error.GetType().Name}: {error.Message}";
        }
        finally
        {
            try
            {
                if (port is null) { handle?.Dispose(); }
                else { port.Dispose(); }
            }
            catch (Exception error) { releaseFailures.Add($"InpOut:{error.GetType().Name}"); }
            if (handle is null || handle.Evidence.State == So6120NativeCloseState.Closed)
            {
                try { pin?.Dispose(); }
                catch (Exception error) { releaseFailures.Add($"DriverPin:{error.GetType().Name}"); }
                try { reservation?.Dispose(); }
                catch (Exception error) { releaseFailures.Add($"Reservation:{error.GetType().Name}"); }
                pin = null;
                reservation = null;
            }
        }
        // Keep the pin and reservation alive through the command's terminal
        // report if native close failed or could not be observed.
        return new(openAttempted, scansMatched, handle?.Evidence, failure, releaseFailures, pin, reservation);
    }

    private sealed record ReadStep(byte Index, So6120PortResult? Selection, So6120PortResult? Read);

    private static So6120MailboxEnvelope Scan(So6120InpOutBytePort port, TimeProvider clock,
        Action<object> record, int scan, CancellationToken cancellationToken)
    {
        var steps = new List<ReadStep>(ScanIndices.Length);
        long started = clock.GetTimestamp();
        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= ScanTimeout)
            { throw new TimeoutException("The mailbox scan's two-second work budget expired."); }
        }
        try
        {
            foreach (byte index in ScanIndices)
            {
                Check();
                steps.Add(new(index, null, null));
                var selected = port.SelectIndex(index);
                steps[^1] = new(index, selected, null);
                Check();
                if (!selected.Succeeded || selected.Effect != So6120PortEffect.Performed)
                { throw new InvalidOperationException($"Mailbox index {index:X2} selection failed ({selected.NativeError})."); }
                var read = port.ReadData();
                steps[^1] = new(index, selected, read);
                Check();
                if (!read.Succeeded || read.Effect != So6120PortEffect.Performed || read.BytesReturned != 1)
                { throw new InvalidOperationException($"Mailbox index {index:X2} read failed ({read.NativeError}, {read.BytesReturned} bytes)."); }
            }
            byte command = steps[0].Read!.Value;
            if (command != steps[^1].Read!.Value)
            { throw new InvalidOperationException("Command byte changed during the mailbox scan."); }
            return new(steps[1].Read!.Value, steps[2].Read!.Value, steps[3].Read!.Value, command,
                steps[4].Read!.Value, steps[5].Read!.Value);
        }
        finally { record(new { phase = "scan", scan, steps }); }
    }
}
