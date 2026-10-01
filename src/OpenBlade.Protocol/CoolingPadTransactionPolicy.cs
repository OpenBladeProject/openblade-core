namespace OpenBlade.Protocol;

// Exact 0528/0F43 production fan policy. Ordinary devices retain their allocator.
// One acknowledged exchange completes before another assignment; uncertainty is terminal.
internal sealed class CoolingPadTransactionPolicy
{
    private readonly object gate = new();
    private byte next = 1;
    private long sequence;
    private bool pending;
    private bool failed;
    internal long CompletedCount { get; private set; }

    internal TransactionIdAssignment Assign(RazerCommandSpec command)
    {
        lock (gate)
        {
            if (failed || pending || !Allows(command))
            { failed = true; return TransactionIdAssignment.Exhausted(); }
            try { sequence = checked(sequence + 1); }
            catch (OverflowException) { failed = true; return TransactionIdAssignment.Exhausted(); }
            byte id = next;
            next = id == 0x1E ? (byte)0 : (byte)(id + 1);
            pending = true;
            return new(id, null) { CoolingPadProduction = true };
        }
    }

    internal void Complete(HidSessionResult<ReadOnlyMemory<byte>> result, bool knownNotSubmitted = false)
    {
        lock (gate)
        {
            if (!pending || result.Failure is not null && !knownNotSubmitted) { failed = true; }
            else if (result.Failure is null) { CompletedCount = checked(CompletedCount + 1); }
            pending = false;
        }
    }

    internal static bool Allows(RazerCommandSpec command)
    {
        var args = command.Arguments.Span;
        if (command.PayloadValidator is null || command.ExpectedPayloadLength != args.Length) { return false; }
        if (command.ClassId == 0)
        {
            return command.CommandId == 0x84 && args.SequenceEqual(new byte[] { 0, 0 })
                || command.CommandId == 4 && (args.SequenceEqual(new byte[] { 0, 0 }) || args.SequenceEqual(new byte[] { 3, 0 }));
        }
        return command.ClassId == 0x0D && (command.CommandId == 0x10
            && (args.SequenceEqual(new byte[] { 0, 6, 0 }) || args.SequenceEqual(new byte[] { 1, 1, 0x2C }))
            || command.CommandId == 1 && args.Length == 3 && args[0] == 1 && args[1] == 5 && args[2] is >= 10 and <= 64);
    }
}
