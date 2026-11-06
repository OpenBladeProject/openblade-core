using System.Text.Json;

namespace OpenBlade.Capture;

// This is the fixed continuation of one reviewed staging-only failure, not a
// direct-duty session or a writable recovery form of the prior journal.
internal static class So6120StagingRecovery
{
    internal const byte SubmittedB8 = 54;
    internal const byte SubmittedB9 = 49;
    private const byte CapturedCycle = 183;
    private static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(30);
    private static readonly So6120MailboxRegister[] SnapshotOrder =
    [
        So6120MailboxRegister.Selector, So6120MailboxRegister.Manual, So6120MailboxRegister.Override,
        So6120MailboxRegister.StagedDuty1, So6120MailboxRegister.StagedDuty2, So6120MailboxRegister.Cycle,
        So6120MailboxRegister.PolicyCommand, So6120MailboxRegister.ActiveState,
    ];

    internal static FileStream CreateClaim(string directory, Guid priorRun, Guid run, string approvalSha256)
    {
        directory = So6120DirectDutyJournal.ValidateRunRoot(directory);
        var file = new FileStream(Path.Combine(directory, $"restore-staging-{priorRun:N}.json"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        try
        {
            _ = So6120DirectDutyJournal.ValidateRunRoot(directory);
            JsonSerializer.Serialize(file, new { priorRun, run, approvalSha256 });
            file.Flush(flushToDisk: true);
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal static void Restore(ISo6120MailboxBytePort port, TimeProvider clock, Action admit,
        Func<IDisposable> claim, Action<object> record, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            admit();
            using var consumed = claim();
            record(new { phase = "claimConsumed" });
            var state = Snapshot("before", null);

            if (state.Staged1 != 0)
            {
                var expected = state with { Staged1 = 0 };
                _ = Transfer(So6120MailboxRegister.StagedDuty1, 0);
                state = Snapshot("afterB8", expected);
            }
            if (state.Staged2 != 0)
            {
                var expected = state with { Staged2 = 0 };
                _ = Transfer(So6120MailboxRegister.StagedDuty2, 0);
                state = Snapshot("afterB9", expected);
            }
            var final = Snapshot("final", state with { Staged1 = 0, Staged2 = 0 });
            record(new { phase = "stagingVerified", final });
        }
        catch (Exception error)
        {
            // No mailbox command, including A0, after any refused or
            // ambiguous transaction, checkpoint, cancellation or disk error.
            try { record(new { phase = "fenced", failure = error.GetType().Name, error.Message }); }
            catch { /* The persistent claim still prevents a retry. */ }
            throw;
        }

        byte Transfer(So6120MailboxRegister register, byte? target)
        {
            var boundary = new Boundary(register, target, admit, record);
            var transaction = new So6120HostMailboxTransaction(port, boundary, clock, TransactionTimeout);
            var result = target is byte value
                ? transaction.WriteAndConfirm(register, value, cancellationToken)
                : transaction.Read(register, cancellationToken);
            record(new { phase = "transferResult", register, target, result });
            if (!result.Success || result.Value is not byte confirmed || (target.HasValue && !result.WriteConfirmed))
            { throw new InvalidOperationException($"Mailbox {register} transaction stopped ({result.Failure})."); }
            return confirmed;
        }

        So6120DutyControls Snapshot(string checkpoint, So6120DutyControls? expected)
        {
            long started = clock.GetTimestamp();
            var values = new byte[SnapshotOrder.Length];
            for (int i = 0; i < values.Length; i++)
            {
                CheckSnapshotBudget();
                So6120MailboxRegister register = SnapshotOrder[i];
                byte value = Transfer(register, null);
                if (!Matches(register, value, expected))
                { throw new InvalidOperationException($"Unexpected {register} byte {value:X2} at checkpoint {checkpoint}."); }
                values[i] = value;
            }
            CheckSnapshotBudget();
            var controls = new So6120DutyControls(values[0], values[1], values[2], values[3], values[4], values[5], values[6]);
            record(new { phase = "snapshot", checkpoint, started, completed = clock.GetTimestamp(), controls, activeState = values[7] });
            // A durable flush may block. Do not start a new transaction using
            // a checkpoint that expired while it was being recorded.
            CheckSnapshotBudget();
            return controls;

            void CheckSnapshotBudget()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.GetElapsedTime(started) >= SnapshotTimeout)
                { throw new TimeoutException("The staging recovery snapshot expired."); }
            }
        }
    }

    private static bool Matches(So6120MailboxRegister register, byte value, So6120DutyControls? expected) => register switch
    {
        So6120MailboxRegister.Selector or So6120MailboxRegister.Manual or So6120MailboxRegister.Override
            or So6120MailboxRegister.PolicyCommand => value == 0,
        So6120MailboxRegister.Cycle => value == CapturedCycle,
        So6120MailboxRegister.ActiveState => (value & 1) != 0,
        So6120MailboxRegister.StagedDuty1 => expected is null ? value is 0 or SubmittedB8 : value == expected.Staged1,
        So6120MailboxRegister.StagedDuty2 => expected is null ? value is 0 or SubmittedB9 : value == expected.Staged2,
        _ => true,
    };

    private sealed class Boundary(So6120MailboxRegister register, byte? target, Action admit, Action<object> record)
        : ISo6120MailboxPrerequisites
    {
        private So6120MailboxRequest? pending;
        private int prepared;

        public bool Prepare(So6120MailboxRequest request)
        {
            byte? expected = prepared == 0 ? target : null;
            if (pending is not null || prepared >= (target.HasValue ? 2 : 1) || request.Register != register
                || request.WriteValue != expected || (expected.HasValue
                    && (expected != 0 || register is not (So6120MailboxRegister.StagedDuty1 or So6120MailboxRegister.StagedDuty2))))
            { return false; }
            admit();
            record(new { phase = "intent", request });
            prepared++;
            pending = request;
            return true;
        }

        public bool AdmitPublication(So6120MailboxRequest request)
        {
            if (pending != request) { return false; }
            record(new { phase = "publicationIntent", request });
            admit();
            pending = null;
            return true;
        }
    }
}
