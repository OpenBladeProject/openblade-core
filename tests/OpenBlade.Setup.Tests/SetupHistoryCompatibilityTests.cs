#if OPENBLADE_NATIVE_PACKAGE_TESTS
using OpenBlade.Deployment;
using OpenBlade.Deployment.Tests;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed partial class SetupCommandRunnerTests
{
    [Fact]
    public async Task ManagedFailureRestoresHealthyPinnedReaderAfterNewHistoryAdmissionAndPayloadRollback()
    {
        using var files = ManagedMailboxCorrectionTests.Handoff();
        using var epochs = ManagedMailboxCorrectionTests.Bootstrap();
        using var original = await ManagedMailboxCorrectionTests.Packages();
        var run = new ManagedMailboxRun(new string('1', 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
        using var transfer = ManagedMailboxCorrectionTests.SignedTransfer(files, original, run, '6');
        File.Delete(ManagedMailboxHistory.RunPath(files, run.Run));
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        {
            PinnedPredecessorMailboxRecovery.Resolve(files, native, _ =>
            {
                using var cached = WindowsManagedMailboxPackage.OpenCachedHistorical(files, original.Managed.PackageDigest, original.Managed.VerificationProjection);
                Assert.Equal(original.Managed.EnrollmentDigest, cached.EnrollmentDigest);
            });
        }
        ManagedMailboxCorrectionTests.ReadPrivatePredecessor(files, epochs);
        bool admitted = false, restored = false;
        var platform = new RecordingSetupPlatform
        {
            ServiceExists = true,
            FailurePoint = "service-health",
            PredecessorKind = SetupServicePredecessorKind.Managed,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
            ObserveOperation = operation =>
            {
                if (operation == "service-health")
                {
                    ManagedMailboxCorrectionTests.ResolvePrivateHistory(files, epochs,
                        () => NativeLocalDevEnrollment.FromProtectedProjection(original.Managed.VerificationProjection));
                    admitted = true;
                }
                if (operation == "service-restore")
                {
                    Assert.True(admitted);
                    ManagedMailboxCorrectionTests.ReadPrivatePredecessor(files, epochs); restored = true;
                }
            },
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new SetupCommandRunner(platform).RunAsync(["install"], default));
        Assert.Equal("service-health failed", error.Message); Assert.True(admitted); Assert.True(restored);
        Assert.True(platform.Operations.IndexOf("payload-rollback") < platform.Operations.IndexOf("service-restore"));
        Assert.DoesNotContain("payload-commit", platform.Operations);
    }
}
#endif
