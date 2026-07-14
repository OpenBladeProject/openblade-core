namespace OpenSynapse.Protocol;

internal readonly record struct BatteryValidationTransactionIds(
    byte BaselineQuery,
    byte CandidateSet,
    byte CandidateQuery,
    byte RestoreSet,
    byte RestoreQuery);

internal sealed class ValidationRunContext
{
    private const int MaximumTransactionId = byte.MaxValue;
    private const int BatteryValidationTransactionCount = 5;

    private readonly object _sync = new();
    private int _nextTransactionId = 1;
    private ActorLease? _activeActorLease;

    public bool TryAllocateTransaction(out byte transactionId)
    {
        lock (_sync)
        {
            if (_nextTransactionId > MaximumTransactionId)
            {
                transactionId = default;
                return false;
            }

            transactionId = (byte)_nextTransactionId;
            _nextTransactionId++;
            return true;
        }
    }

    public bool TryReserveBatteryValidation(out BatteryValidationTransactionIds ids)
    {
        lock (_sync)
        {
            if (_nextTransactionId > MaximumTransactionId - BatteryValidationTransactionCount + 1)
            {
                ids = default;
                return false;
            }

            byte baselineQuery = (byte)_nextTransactionId;
            ids = new BatteryValidationTransactionIds(
                baselineQuery,
                (byte)(baselineQuery + 1),
                (byte)(baselineQuery + 2),
                (byte)(baselineQuery + 3),
                (byte)(baselineQuery + 4));
            _nextTransactionId += BatteryValidationTransactionCount;
            return true;
        }
    }

    public bool TryAcquireActor(out ActorLease lease)
    {
        lock (_sync)
        {
            if (_activeActorLease is not null)
            {
                lease = null!;
                return false;
            }

            lease = new ActorLease(this);
            _activeActorLease = lease;
            return true;
        }
    }

    private void ReleaseActor(ActorLease lease)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activeActorLease, lease))
            {
                _activeActorLease = null;
            }
        }
    }

    internal sealed class ActorLease(ValidationRunContext owner) : IDisposable
    {
        public void Dispose() => owner.ReleaseActor(this);
    }
}
