using OpenBlade.Deployment;

namespace OpenBlade.Setup;

internal interface ISetupOriginalManagedProcess : IDisposable
{
    SetupCandidateLaunch Launch { get; }
    void RequireExited();
}

internal static class SetupManagedMailboxAdmission
{
    internal static void Confirm(ISetupOriginalManagedProcess original, IManagedMailboxReceipt receipt)
    {
        // Retained original process evidence and protected transport evidence
        // are both required. SCM Stopped never substitutes for either.
        original.RequireExited();
        receipt.ConfirmOriginalExited();
    }
}
