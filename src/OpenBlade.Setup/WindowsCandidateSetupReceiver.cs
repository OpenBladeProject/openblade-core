#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup;

internal static class WindowsCandidateSetupReceiver
{
    // The locator selects a scratch tree. Authority comes from its protected
    // enrollment, current Broker launch binding, and independently verified package.
    internal static Task CommitScratchPayloadAsync(Guid scratchId, CancellationToken cancellationToken)
    {
        var storage = WindowsCandidateReceiptStorage.OpenScratch(scratchId);
        string root = storage.Root;
        var paths = SetupPaths.Create(Path.Combine(root, "program-files"), Path.Combine(root, "ProgramData"),
            Path.Combine(root, "LocalData"), Path.Combine(root, "Startup"));
        return new BrokerCandidateReceiptStage(storage).WithCurrentProcessReceiptAsync(async receipt =>
        {
            await using var transaction = await SetupPayloadTransaction.StageCandidateAsync(paths,
                Path.Combine(root, "payload"), new WindowsSetupPayloadFileSystem(), receipt,
                receipt.InstallationId, receipt.RequestId, () => { }, cancellationToken);
            try
            {
                await transaction.ActivateAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception failure)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(failure, rollbackFailure);
                }
                throw;
            }
        }, cancellationToken);
    }
}
#endif
