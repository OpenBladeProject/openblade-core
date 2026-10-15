using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class TemporaryFullChargeValidationRunFactoryTests
{
    [Fact]
    public void TryReserveTemporaryFullChargeValidation_ReturnsAtomicTenIds()
    {
        var context = new ValidationRunContext();

        Assert.True(context.TryReserveTemporaryFullChargeValidation(
            out TemporaryFullChargeValidationTransactionIds ids));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, ids.ToArray());
    }

    [Fact]
    public async Task OpenAsync_WithOnlySevenIdsRemainingFailsBeforeDeviceOpen()
    {
        var context = new ValidationRunContext();
        for (int index = 0; index < 248; index++)
        {
            Assert.True(context.TryAllocateTransaction(out _));
        }

        var actors = new NeverOpenActorFactory();
        var factory = new TemporaryFullChargeValidationRunFactory(
            () => context,
            actors,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            new PendingDisposalRegistry());

        HidSessionResult<ITemporaryFullChargeValidationPort> result =
            await factory.OpenAsync(default);

        Assert.Equal(HidSessionFailureKind.Exhausted, result.Failure);
        Assert.Equal(0, actors.OpenCount);
        Assert.True(context.TryAllocateTransaction(out byte unchanged));
        Assert.Equal(249, unchanged);
    }

    private sealed class NeverOpenActorFactory : IValidationActorFactory
    {
        internal int OpenCount { get; private set; }

        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context)
        {
            OpenCount++;
            throw new InvalidOperationException();
        }
    }
}
