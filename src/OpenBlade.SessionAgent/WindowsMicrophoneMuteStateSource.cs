namespace OpenBlade.SessionAgent;

public interface IMicrophoneMuteStateSource
{
    IAsyncEnumerable<bool?> WatchMutedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Observes the deduplicated default Windows capture endpoints through Core
/// Audio callbacks. The indicator is asserted only when at least one endpoint
/// exists and every default capture endpoint is muted.
/// </summary>
public sealed class WindowsMicrophoneMuteStateSource : IMicrophoneMuteStateSource
{
    public IAsyncEnumerable<bool?> WatchMutedAsync(
        CancellationToken cancellationToken) =>
        WindowsCoreAudioMuteStateSource.WatchMutedAsync(
            CoreAudioDataFlow.Capture,
            Enum.GetValues<CoreAudioRole>(),
            states => AggregateIndicatorState(states),
            cancellationToken);

    internal static bool AggregateIndicatorState(IReadOnlyCollection<bool> states) =>
        states.Count > 0 && states.All(static muted => muted);
}
