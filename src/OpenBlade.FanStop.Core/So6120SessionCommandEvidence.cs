namespace OpenBlade.FanStop;

internal sealed record So6120SessionCommandEvidence(int CommandsPossiblySubmitted, int CommandsCompleted,
    int MutationsPossiblySubmitted, bool NativeAmbiguity, bool TransferInFlight)
{
    internal bool CleanNoMutation => !TransferInFlight && !NativeAmbiguity
        && MutationsPossiblySubmitted == 0 && CommandsPossiblySubmitted == CommandsCompleted;
}
