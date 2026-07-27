namespace OpenBlade.Testing;

internal static class AsyncTest
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private const string TimeoutMessage = "The asynchronous condition was not reached.";

    public static Task WaitUntilAsync(Func<bool> condition) =>
        WaitUntilAsync(condition, DefaultTimeout);

    public static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(condition);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await WaitUntilAsync(condition, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Assert.True(condition(), TimeoutMessage);
        }
    }

    public static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
