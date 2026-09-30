namespace OpenBlade.Protocol;

internal readonly record struct BatteryValidationTransactionIds(
    byte BaselineQuery,
    byte CandidateSet,
    byte CandidateQuery,
    byte RestoreSet,
    byte RestoreQuery);

internal readonly record struct ProtectionOffValidationTransactionIds(
    byte BaselineProtectionQuery,
    byte BaselineTemporaryQuery,
    byte CandidateSet,
    byte CandidateProtectionQuery,
    byte CandidateTemporaryQuery,
    byte RestoreSet,
    byte RestorationProtectionQuery,
    byte RestorationTemporaryQuery)
{
    internal byte[] ToArray() =>
        [
            BaselineProtectionQuery,
            BaselineTemporaryQuery,
            CandidateSet,
            CandidateProtectionQuery,
            CandidateTemporaryQuery,
            RestoreSet,
            RestorationProtectionQuery,
            RestorationTemporaryQuery,
        ];
}

internal readonly record struct TemporaryFullChargeValidationTransactionIds(
    byte BaselineProtectionQuery,
    byte BaselineTemporaryQuery,
    byte EnableSet,
    byte CandidateProtectionQuery,
    byte CandidateTemporaryQuery,
    byte CancelSet,
    byte RestorationProtectionQuery,
    byte RestorationTemporaryQuery)
{
    internal byte[] ToArray() =>
        [
            BaselineProtectionQuery,
            BaselineTemporaryQuery,
            EnableSet,
            CandidateProtectionQuery,
            CandidateTemporaryQuery,
            CancelSet,
            RestorationProtectionQuery,
            RestorationTemporaryQuery,
        ];
}

internal sealed class ValidationRunContext
{
    private const int MaximumTransactionId = byte.MaxValue;
    private const int BatteryValidationTransactionCount = 5;
    private const int ProtectionOffValidationTransactionCount = 8;
    private const int TemporaryFullChargeValidationTransactionCount = 8;

    private readonly object sync = new();
    private int nextTransactionId = 1;
    private ActorLease? activeActorLease;

    internal CoolingPadDeveloperCounterPolicy CreateCoolingPadDeveloperCounterPolicy()
    {
        lock (sync)
        {
            if (nextTransactionId != 1 || activeActorLease is not null)
            { throw new InvalidOperationException("CounterPolicyRequiresFreshContext"); }
            nextTransactionId = MaximumTransactionId + 1;
            return new CoolingPadDeveloperCounterPolicy();
        }
    }

    internal int RemainingTransactions
    {
        get { lock (sync) { return MaximumTransactionId - nextTransactionId + 1; } }
    }

    public bool TryAllocateTransaction(out byte transactionId)
    {
        lock (sync)
        {
            if (nextTransactionId > MaximumTransactionId)
            {
                transactionId = default;
                return false;
            }

            transactionId = (byte)nextTransactionId;
            nextTransactionId++;
            return true;
        }
    }

    public bool TryReserveBatteryValidation(out BatteryValidationTransactionIds ids)
    {
        lock (sync)
        {
            if (nextTransactionId > MaximumTransactionId - BatteryValidationTransactionCount + 1)
            {
                ids = default;
                return false;
            }

            byte baselineQuery = (byte)nextTransactionId;
            ids = new BatteryValidationTransactionIds(
                baselineQuery,
                (byte)(baselineQuery + 1),
                (byte)(baselineQuery + 2),
                (byte)(baselineQuery + 3),
                (byte)(baselineQuery + 4));
            nextTransactionId += BatteryValidationTransactionCount;
            return true;
        }
    }

    public bool TryReserveBatteryQuerySequence(out byte[] transactionIds)
    {
        lock (sync)
        {
            if (nextTransactionId > MaximumTransactionId - BatteryValidationTransactionCount + 1)
            {
                transactionIds = [];
                return false;
            }

            transactionIds = new byte[BatteryValidationTransactionCount];
            for (int index = 0; index < transactionIds.Length; index++)
            {
                transactionIds[index] = (byte)(nextTransactionId + index);
            }

            nextTransactionId += BatteryValidationTransactionCount;
            return true;
        }
    }

    public bool TryReserveProtectionOffValidation(out ProtectionOffValidationTransactionIds ids)
    {
        lock (sync)
        {
            if (nextTransactionId > MaximumTransactionId - ProtectionOffValidationTransactionCount + 1)
            {
                ids = default;
                return false;
            }

            byte first = (byte)nextTransactionId;
            ids = new ProtectionOffValidationTransactionIds(
                first,
                (byte)(first + 1),
                (byte)(first + 2),
                (byte)(first + 3),
                (byte)(first + 4),
                (byte)(first + 5),
                (byte)(first + 6),
                (byte)(first + 7));
            nextTransactionId += ProtectionOffValidationTransactionCount;
            return true;
        }
    }

    public bool TryReserveTemporaryFullChargeValidation(
        out TemporaryFullChargeValidationTransactionIds ids)
    {
        lock (sync)
        {
            if (nextTransactionId > MaximumTransactionId
                - TemporaryFullChargeValidationTransactionCount + 1)
            {
                ids = default;
                return false;
            }

            byte first = (byte)nextTransactionId;
            ids = new TemporaryFullChargeValidationTransactionIds(
                first,
                (byte)(first + 1),
                (byte)(first + 2),
                (byte)(first + 3),
                (byte)(first + 4),
                (byte)(first + 5),
                (byte)(first + 6),
                (byte)(first + 7));
            nextTransactionId += TemporaryFullChargeValidationTransactionCount;
            return true;
        }
    }

    public bool TryAcquireActor(out ActorLease lease)
    {
        lock (sync)
        {
            if (activeActorLease is not null)
            {
                lease = null!;
                return false;
            }

            lease = new ActorLease(this);
            activeActorLease = lease;
            return true;
        }
    }

    private void ReleaseActor(ActorLease lease)
    {
        lock (sync)
        {
            if (ReferenceEquals(activeActorLease, lease))
            {
                activeActorLease = null;
            }
        }
    }

    internal sealed class ActorLease(ValidationRunContext owner) : IDisposable
    {
        public void Dispose() => owner.ReleaseActor(this);
    }
}
