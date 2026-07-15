using System.Collections.Concurrent;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ValidationRunContextTests
{
    [Fact]
    public void TryAllocateTransaction_AssignsOneThrough255ThenExhaustsWithoutReuse()
    {
        var context = new ValidationRunContext();
        var assigned = new List<byte>();

        while (context.TryAllocateTransaction(out byte transactionId))
        {
            assigned.Add(transactionId);
        }

        Assert.Equal(Enumerable.Range(1, 255).Select(value => (byte)value), assigned);
        Assert.False(context.TryAllocateTransaction(out _));
    }

    [Fact]
    public void TryReserveBatteryValidation_AtomicallyReservesFiveConsecutiveIds()
    {
        var context = new ValidationRunContext();

        Assert.True(context.TryReserveBatteryValidation(out BatteryValidationTransactionIds ids));

        Assert.Equal(new BatteryValidationTransactionIds(1, 2, 3, 4, 5), ids);
        Assert.True(context.TryAllocateTransaction(out byte next));
        Assert.Equal(6, next);
    }

    [Fact]
    public void TryReserveBatteryValidation_After250ConsumedIdsReservesUsableFinalFive()
    {
        var context = new ValidationRunContext();
        Consume(context, 250);

        Assert.True(context.TryReserveBatteryValidation(out BatteryValidationTransactionIds ids));
        Assert.Equal(new BatteryValidationTransactionIds(251, 252, 253, 254, 255), ids);
        Assert.All(
            new[] { ids.BaselineQuery, ids.CandidateSet, ids.CandidateQuery, ids.RestoreSet, ids.RestoreQuery },
            transactionId => Assert.InRange(transactionId, (byte)1, byte.MaxValue));
        Assert.False(context.TryAllocateTransaction(out _));
    }

    [Theory]
    [InlineData(251)]
    [InlineData(252)]
    [InlineData(255)]
    public void TryReserveBatteryValidation_WithFewerThanFiveIdsRemainingFailsWithoutConsumption(
        int consumedCount)
    {
        var context = new ValidationRunContext();
        Consume(context, consumedCount);

        Assert.False(context.TryReserveBatteryValidation(out BatteryValidationTransactionIds ids));
        Assert.Equal(default, ids);

        if (consumedCount < byte.MaxValue)
        {
            Assert.True(context.TryAllocateTransaction(out byte next));
            Assert.Equal(consumedCount + 1, next);
        }
        else
        {
            Assert.False(context.TryAllocateTransaction(out _));
        }
    }

    [Fact]
    public void TryReserveBatteryValidation_ConcurrentRequestsNeverOverlapOrPartiallyReserve()
    {
        var context = new ValidationRunContext();
        var reservations = new ConcurrentBag<BatteryValidationTransactionIds>();

        Parallel.For(
            0,
            64,
            _ =>
            {
                if (context.TryReserveBatteryValidation(out BatteryValidationTransactionIds ids))
                {
                    reservations.Add(ids);
                }
            });

        byte[] assigned = reservations
            .SelectMany(ids => new[]
            {
                ids.BaselineQuery,
                ids.CandidateSet,
                ids.CandidateQuery,
                ids.RestoreSet,
                ids.RestoreQuery,
            })
            .Order()
            .ToArray();

        Assert.Equal(51, reservations.Count);
        Assert.Equal(Enumerable.Range(1, 255).Select(value => (byte)value), assigned);
        Assert.False(context.TryAllocateTransaction(out _));
    }

    [Fact]
    public void TryAcquireActor_ExcludesOtherActorsUntilLeaseIsReleased()
    {
        var context = new ValidationRunContext();

        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease first));
        Assert.False(context.TryAcquireActor(out _));

        first.Dispose();

        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease second));
        second.Dispose();
    }

    [Fact]
    public void TryAcquireActor_ConcurrentAttemptsGrantExactlyOneLease()
    {
        var context = new ValidationRunContext();
        var leases = new ConcurrentBag<ValidationRunContext.ActorLease>();

        Parallel.For(
            0,
            64,
            _ =>
            {
                if (context.TryAcquireActor(out ValidationRunContext.ActorLease lease))
                {
                    leases.Add(lease);
                }
            });

        ValidationRunContext.ActorLease granted = Assert.Single(leases);
        granted.Dispose();
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease replacement));
        replacement.Dispose();
    }

    private static void Consume(ValidationRunContext context, int count)
    {
        for (int expected = 1; expected <= count; expected++)
        {
            Assert.True(context.TryAllocateTransaction(out byte actual));
            Assert.Equal(expected, actual);
        }
    }
}
