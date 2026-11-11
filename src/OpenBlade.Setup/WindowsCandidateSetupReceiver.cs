#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup;

internal static class WindowsCandidateSetupReceiver
{
    internal const int LaunchSecretLength = ScratchCandidateLaunch.SecretLength;

    // The locator selects a scratch tree. Authority comes from its protected
    // enrollment, current Broker launch binding, and independently verified package.
    internal static Task CommitScratchPayloadAsync(Guid scratchId, byte[] secret,
        Func<byte[], CancellationToken, Task> observeStartup, CancellationToken cancellationToken)
    {
        var storage = WindowsCandidateReceiptStorage.OpenScratch(scratchId);
        string root = storage.Root;
        var paths = SetupPaths.Create(Path.Combine(root, "program-files"), Path.Combine(root, "ProgramData"),
            Path.Combine(root, "LocalData"), Path.Combine(root, "Startup"));
        return new BrokerCandidateReceiptStage(storage).WithCurrentProcessReceiptAsync(secret, async (receipt, identity, requireLiveLaunch) =>
        {
            WindowsScratchCandidateReadiness? verified = null;
            var transaction = await SetupPayloadTransaction.StageCandidateAsync(paths,
                Path.Combine(root, "payload"), new WindowsSetupPayloadFileSystem(), receipt,
                receipt.InstallationId, receipt.RequestId, () => { }, cancellationToken, requireLiveLaunch,
                verifyReadinessAtPublication: () =>
                {
                    requireLiveLaunch();
                    (verified ?? throw new InvalidDataException("Scratch readiness has not completed readback."))
                        .RequireLive(cancellationToken);
                });
            try
            {
                requireLiveLaunch();
                await transaction.ActivateAsync(cancellationToken);
                // Mirrors the command runner's activate -> start/health -> commit
                // order. This rendezvous is offline fixture readiness only.
                byte[] commitment = [.. receipt.StartupCommitment(identity), .. identity];
                await observeStartup(commitment, cancellationToken);
                requireLiveLaunch();
                var candidate = await WindowsScratchCandidateReadiness.ConnectAsync(storage, receipt.RequestId,
                    commitment, secret, cancellationToken);
                Exception? readinessFailure = null;
                try
                {
                    await candidate.VerifyAsync(1, cancellationToken);
                    await candidate.VerifyAsync(2, cancellationToken);
                    verified = candidate;
                    await transaction.CommitAsync(cancellationToken);
                }
                catch (Exception error) { readinessFailure = error; }
                candidate.Dispose(readinessFailure);
            }
            catch (Exception failure)
            {
                Exception? rollbackFailure = null;
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception error) { rollbackFailure = error; }
                Exception? disposeFailure = null;
                try { await transaction.DisposeAsync(); }
                catch (Exception error) { disposeFailure = error; }
                ScratchFixtureFailure.Cleanup(failure,
                    () => { if (rollbackFailure is not null) { throw rollbackFailure; } },
                    () => { if (disposeFailure is not null) { throw disposeFailure; } });
                throw;
            }
            await transaction.DisposeAsync();
        }, cancellationToken);
    }
}
#endif
