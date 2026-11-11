using OpenBlade.Deployment;

namespace OpenBlade.Setup;

internal interface ISetupOriginalManagedProcess : IDisposable
{
    SetupCandidateLaunch Launch { get; }
    Task WaitForExitAsync(TimeSpan budget, CancellationToken token);
}

internal static class SetupManagedMailboxAdmission
{
    internal static async Task ConfirmAsync(ISetupOriginalManagedProcess original, IManagedMailboxReceipt receipt, TimeSpan budget, CancellationToken token)
    {
        // Retained original process evidence and protected transport evidence
        // are both required. SCM Stopped never substitutes for either.
        await original.WaitForExitAsync(budget, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        receipt.ConfirmOriginalExited();
    }
}
