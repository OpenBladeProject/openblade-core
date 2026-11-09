using OpenBlade.Contracts;
using OpenBlade.FanStop.Guardian;

namespace OpenBlade.Service.Tests;

public sealed class FanStopPowerReadAdmissionTests
{
    [Theory]
    [InlineData("Competing controller detected")]
    [InlineData("Original ownership delegation unavailable")]
    [InlineData("Metadata expired before the adapter query")]
    public async Task AdmissionRefusalEndsStopWithoutStartingOrRetryingTheQuery(string refusal)
    {
        int queries = 0;
        var result = await So6120ServicePowerReadPolicy.ReadAdmittedAsync(
            () => throw new InvalidOperationException(refusal),
            () => { queries++; return Task.FromResult(PowerSource.UsbC); });

        Assert.True(result.QueryNotAttempted);
        Assert.Equal(PowerSource.ExternalUnknown, result.Source);
        Assert.Contains("before native submission", result.AdmissionRefusal);
        Assert.Equal(0, queries);
        Assert.Equal(FanStopPowerTransition.Automatic,
            FanStopPowerTransitionPolicy.Evaluate(PowerSource.UsbC, result.Source));
        // This requests the normal same-owner cleanup path, never overrides
        // the fresh controller/identity/ownership checks on its EC commands.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnteredQueryFailureRemainsUnresolvedAndIsNeverRetried(bool canceled)
    {
        int queries = 0;
        Exception cause = canceled ? new OperationCanceledException() : new TimeoutException();
        var failure = await Assert.ThrowsAsync<So6120ServicePowerReadException>(() =>
            So6120ServicePowerReadPolicy.ReadAdmittedAsync(static () => { }, () =>
            {
                queries++;
                return So6120ServicePowerReadPolicy.ReadAsync(
                    _ => Task.FromException<AdapterPowerState>(cause), default);
            }));

        Assert.True(failure.NativeOperationUnresolved);
        Assert.Same(cause, failure.InnerException);
        Assert.Equal(1, queries);
    }

    [Theory]
    [InlineData(PowerSource.UsbC)]
    [InlineData(PowerSource.ExternalUnknown)]
    public async Task CompletedQueryIsDistinguishedFromACommandFreeRefusal(PowerSource source)
    {
        int admissions = 0, queries = 0;
        var result = await So6120ServicePowerReadPolicy.ReadAdmittedAsync(
            () => admissions++,
            () => { queries++; return Task.FromResult(source); });

        Assert.False(result.QueryNotAttempted);
        Assert.Null(result.AdmissionRefusal);
        Assert.Equal(source, result.Source);
        Assert.Equal(1, admissions);
        Assert.Equal(1, queries);
    }
}
