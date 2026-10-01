namespace OpenBlade.Protocol;

internal sealed record CoolingPadCounterExchange(int Sequence, byte WireId, byte ClassId, byte CommandId,
    bool Acknowledged, HidSessionFailureKind? Failure, bool OutcomeAvailable);

// Fixed-capacity developer experiment. No production or ordinary allocator uses this policy.
internal sealed class CoolingPadDeveloperCounterPolicy
{
    internal const int NormalOperationBudget = 39;
    internal const int RestorationOperationBudget = 4;
    private readonly object sync = new();
    private readonly List<CoolingPadCounterExchange> exchanges = [];
    private int normalRemaining = NormalOperationBudget;
    private int restorationRemaining = RestorationOperationBudget;
    private int reserved;
    private byte next = 1;

    internal IReadOnlyList<CoolingPadCounterExchange> Snapshot()
    { lock (sync) { return exchanges.ToArray(); } }

    internal bool Reserve(int count, bool restoration)
    {
        lock (sync)
        {
            if (count < 1 || count > (restoration ? restorationRemaining : normalRemaining)) { return false; }
            if (restoration) { restorationRemaining -= count; } else { normalRemaining -= count; }
            reserved += count;
            return true;
        }
    }

    // Reservations consume capacity only. The serial actor claims the next wire ID.
    internal (int Sequence, TransactionIdAssignment Assignment) Assign(RazerCommandSpec command)
    {
        lock (sync)
        {
            if (reserved == 0) { throw new InvalidOperationException("CounterOperationBudgetExhausted"); }
            reserved--;
            byte id = next;
            next = id == 0x1E ? (byte)0 : (byte)(id + 1);
            int sequence = exchanges.Count + 1;
            exchanges.Add(new(sequence, id, command.ClassId, command.CommandId, false, null, false));
            return (sequence, new TransactionIdAssignment(id, null) { CoolingPadDeveloperCounter = true });
        }
    }

    internal void Complete(int sequence, HidSessionResult<ReadOnlyMemory<byte>> response)
    {
        lock (sync)
        {
            exchanges[sequence - 1] = exchanges[sequence - 1] with
            { Acknowledged = response.Failure is null, Failure = response.Failure, OutcomeAvailable = true };
        }
    }

    internal static bool Allows(RazerCommandSpec command)
    {
        var args = command.Arguments.Span;
        return command.PayloadValidator is not null && command.ExpectedPayloadLength == args.Length
            && (command.ClassId == 0 && (command.CommandId == 0x84 && args.SequenceEqual(new byte[] { 0, 0 })
                || command.CommandId == 4 && (args.SequenceEqual(new byte[] { 0, 0 }) || args.SequenceEqual(new byte[] { 3, 0 })))
            || command.ClassId == 0x0D && (command.CommandId == 0x10
                && (args.SequenceEqual(new byte[] { 0, 6, 0 }) || args.SequenceEqual(new byte[] { 1, 1, 0x2C }))
                || command.CommandId == 1 && (args.SequenceEqual(new byte[] { 1, 5, 20 }) || args.SequenceEqual(new byte[] { 1, 5, 44 }))));
    }
}
