#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Buffers.Binary;
using System.Diagnostics;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup;

internal static class WindowsCandidateSetupReceiver
{
    internal const int LaunchSecretLength = ScratchCandidateLaunch.SecretLength;

    // The locator selects a scratch tree. Authority comes from its protected
    // enrollment, current Broker launch binding, and independently verified package.
    internal static Task CommitScratchPayloadAsync(Guid scratchId, byte[] secret,
        Func<byte[], CancellationToken, Task> verifyStartup, CancellationToken cancellationToken)
    {
        var storage = WindowsCandidateReceiptStorage.OpenScratch(scratchId);
        string root = storage.Root;
        var paths = SetupPaths.Create(Path.Combine(root, "program-files"), Path.Combine(root, "ProgramData"),
            Path.Combine(root, "LocalData"), Path.Combine(root, "Startup"));
        return new BrokerCandidateReceiptStage(storage).WithCurrentProcessReceiptAsync(secret, async (receipt, requireLiveLaunch) =>
        {
            await using var transaction = await SetupPayloadTransaction.StageCandidateAsync(paths,
                Path.Combine(root, "payload"), new WindowsSetupPayloadFileSystem(), receipt,
                receipt.InstallationId, receipt.RequestId, () => { }, cancellationToken, requireLiveLaunch);
            try
            {
                requireLiveLaunch();
                await transaction.ActivateAsync(cancellationToken);
                // Mirrors the command runner's activate -> start/health -> commit
                // order. This rendezvous is offline fixture readiness only.
                using var setup = Process.GetCurrentProcess();
                byte[] identity = new byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(identity, setup.Id);
                BinaryPrimitives.WriteInt64LittleEndian(identity.AsSpan(4), setup.StartTime.ToFileTimeUtc());
                await verifyStartup([.. receipt.StartupCommitment(identity), .. identity], cancellationToken);
                requireLiveLaunch();
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
