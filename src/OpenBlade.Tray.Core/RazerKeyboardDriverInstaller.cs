using System.ComponentModel;

namespace OpenBlade.Tray;

public enum RazerDriverInstallOutcome
{
    Installed = 0, Failed = 1, Unavailable = 2, RecoveryRequired = 4, Canceled = 1602, RestartRequired = 3010, AcPowerRequired = 10, RazerSoftwareRunning = 11,
}

internal sealed record RazerDriverInventoryEntry(string PublishedName, string Sha256);
internal sealed record RazerDriverInstallSnapshot(
    bool Admitted, bool Installed, IReadOnlyList<RazerDriverInventoryEntry> Inventory, string Evidence, RazerDriverInstallOutcome AdmissionFailure = RazerDriverInstallOutcome.Unavailable);
internal sealed record RazerDriverMutation(string RelativeInf, bool Bind);
internal sealed record RazerDriverMutationResult(int ExitCode, string Output);

internal interface IRazerDriverInstallJournal : IDisposable
{
    bool RequiresRecovery { get; }
    string PackageDestination { get; }
    void Begin(RazerDriverInstallSnapshot baseline);
    void Intent(RazerDriverMutation operation);
    void RecordResult(RazerDriverMutationResult result);
    void Observe(RazerDriverMutationResult result, RazerDriverInstallSnapshot after);
    void Complete();
}

internal interface IRazerDriverInstallPlatform
{
    RazerDriverInstallSnapshot Capture();
    bool HasUnfinishedTransaction();
    IRazerDriverInstallJournal OpenJournal();
    Task<string> AcquireVerifiedAsync(string destination, CancellationToken cancellationToken);
    Task<RazerDriverMutationResult> MutateAsync(string root, RazerDriverMutation operation,
        CancellationToken cancellationToken);
}

/// <summary>Fixed-operation elevated installer. No user-supplied package, path, or command is accepted.</summary>
public sealed class RazerKeyboardDriverInstaller
{
    private readonly IRazerDriverInstallPlatform platform;
    public RazerKeyboardDriverInstaller() : this(new WindowsRazerDriverInstallPlatform()) { }
    internal RazerKeyboardDriverInstaller(IRazerDriverInstallPlatform platform) => this.platform = platform;

    public static RazerDriverInstallationInspection Inspect()
    {
        try
        {
            var snapshot = WindowsRazerDriverInstallPlatform.Capture(requireElevation: false);
            return new(snapshot.Admitted, snapshot.Installed, snapshot.AdmissionFailure);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return new(false, false, RazerDriverInstallOutcome.Unavailable);
        }
    }

    public async Task<RazerDriverInstallOutcome> InstallAsync(CancellationToken cancellationToken)
    {
        bool possiblySubmitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RazerDriverInstallSnapshot baseline = platform.Capture();
            if (!baseline.Admitted)
            {
                return baseline.AdmissionFailure;
            }

            if (platform.HasUnfinishedTransaction())
            {
                return RazerDriverInstallOutcome.RecoveryRequired;
            }

            if (baseline.Installed)
            {
                return RazerDriverInstallOutcome.Installed;
            }

            using IRazerDriverInstallJournal journal = platform.OpenJournal();
            // An unfinished transaction can have a pending restart or unobserved PnPUtil process.
            // A healthy snapshot alone cannot authorize replay or removal.
            if (journal.RequiresRecovery)
            {
                return RazerDriverInstallOutcome.RecoveryRequired;
            }

            string root = await platform.AcquireVerifiedAsync(journal.PackageDestination, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            baseline = platform.Capture();
            if (!baseline.Admitted)
            {
                return baseline.AdmissionFailure;
            }

            if (baseline.Installed)
            {
                return RazerDriverInstallOutcome.Installed;
            }

            journal.Begin(baseline);
            foreach (RazerDriverMutation operation in Operations())
            {
                cancellationToken.ThrowIfCancellationRequested();
                RazerDriverInstallSnapshot before = platform.Capture();
                if (!before.Admitted)
                {
                    if (!possiblySubmitted)
                    {
                        journal.Complete();
                    }

                    return possiblySubmitted ? RazerDriverInstallOutcome.RecoveryRequired : before.AdmissionFailure;
                }
                cancellationToken.ThrowIfCancellationRequested();
                journal.Intent(operation); // Stable-storage flush precedes native submission.
                possiblySubmitted = true;
                RazerDriverMutationResult result = await platform.MutateAsync(root, operation, cancellationToken)
                    .ConfigureAwait(false);
                journal.RecordResult(result);
                if (result.ExitCode == 3010)
                {
                    return RazerDriverInstallOutcome.RestartRequired;
                }

                RazerDriverInstallSnapshot after = platform.Capture();
                journal.Observe(result, after);

                if (result.ExitCode != 0 || !after.Admitted)
                {
                    return RazerDriverInstallOutcome.RecoveryRequired;
                }
            }
            RazerDriverInstallSnapshot final = platform.Capture();
            if (!final.Admitted || !final.Installed)
            {
                return RazerDriverInstallOutcome.RecoveryRequired;
            }

            journal.Complete();
            return RazerDriverInstallOutcome.Installed;
        }
        catch (OperationCanceledException)
        {
            return possiblySubmitted ? RazerDriverInstallOutcome.RecoveryRequired : RazerDriverInstallOutcome.Canceled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or Win32Exception or System.Security.Cryptography.CryptographicException
            or System.Text.Json.JsonException or TimeoutException or HttpRequestException)
        {
            return possiblySubmitted ? RazerDriverInstallOutcome.RecoveryRequired : RazerDriverInstallOutcome.Failed;
        }
    }

    internal static IEnumerable<RazerDriverMutation> Operations() =>
        RazerKeyboardDriverPackage.StageOrder.Select(path => new RazerDriverMutation(path, false))
            .Concat(RazerKeyboardDriverPackage.InstallOrder.Select(path => new RazerDriverMutation(path, true)));
}
