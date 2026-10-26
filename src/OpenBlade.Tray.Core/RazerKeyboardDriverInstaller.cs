using System.ComponentModel;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public enum RazerDriverInstallOutcome
{
    Installed = 0, Failed = 1, Unavailable = 2, RecoveryRequired = 4, Canceled = 1602, RestartRequired = 3010, AcPowerRequired = 10, RazerSoftwareRunning = 11, WindowsUpdateUnavailable = 12,
}

internal sealed record RazerDriverInventoryEntry(string PublishedName, string Sha256);
internal sealed record RazerDriverInstallSnapshot(
    bool Admitted, bool Installed, IReadOnlyList<RazerDriverInventoryEntry> Inventory, string Evidence, RazerDriverInstallOutcome AdmissionFailure = RazerDriverInstallOutcome.Unavailable)
{
    internal string? RecipeId { get; init; }
    internal DiagnosticFailure? FailureDetail { get; init; }
    internal IReadOnlyList<RazerKeyboardWindowsUpdateTarget> WindowsUpdateTargets { get; init; } = [];
}
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
    void WindowsUpdateIntent(RazerKeyboardWindowsUpdateSelection selection) => throw new InvalidOperationException("The journal cannot record Windows Update intent.");
    void WindowsUpdateObserve(RazerKeyboardWindowsUpdateResult result, RazerDriverInstallSnapshot after) => throw new InvalidOperationException("The journal cannot record Windows Update results.");
    void Complete();
}

internal interface IRazerDriverInstallPlatform
{
    RazerDriverInstallSnapshot Capture();
    RazerDriverInstallSnapshot CaptureForWindowsUpdate() => Capture();
    bool HasUnfinishedTransaction();
    IRazerDriverInstallJournal OpenJournal();
    Task<string> AcquireVerifiedAsync(string destination, RazerKeyboardDriverRecipe recipe, CancellationToken cancellationToken);
    Task<RazerDriverMutationResult> MutateAsync(string root, RazerDriverMutation operation,
        RazerKeyboardDriverRecipe recipe, CancellationToken cancellationToken);
}

/// <summary>Fixed-operation elevated installer. No user-supplied package, path, or command is accepted.</summary>
public sealed partial class RazerKeyboardDriverInstaller
{
    private readonly IRazerDriverInstallPlatform platform;
    private readonly IRazerKeyboardWindowsUpdateSource? windowsUpdate;
    public RazerKeyboardDriverInstaller() : this(new WindowsRazerDriverInstallPlatform(),
        new RazerKeyboardWindowsUpdateSource(() => new RazerKeyboardWindowsUpdateNativeSession(),
            WindowsRazerDriverInstallPlatform.ReviewedWindowsUpdateHardwareIds))
    { }
    // The internal overload also keeps the pinned installer independently testable without WUA activation.
    internal RazerKeyboardDriverInstaller(IRazerDriverInstallPlatform platform, IRazerKeyboardWindowsUpdateSource? windowsUpdate = null)
    {
        this.platform = platform;
        this.windowsUpdate = windowsUpdate;
    }

    public static RazerDriverInstallationInspection Inspect()
    {
        try
        {
            var snapshot = WindowsRazerDriverInstallPlatform.Capture(requireElevation: false, windowsUpdate: true);
            return new(snapshot.Admitted, snapshot.Installed, snapshot.AdmissionFailure) { FailureDetail = snapshot.FailureDetail };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or InvalidOperationException
            or System.Security.Cryptography.CryptographicException)
        {
            DiagnosticTrace.TraceException(nameof(RazerKeyboardDriverInstaller), "Inspect", exception);
            return new(false, false, RazerDriverInstallOutcome.Unavailable)
            {
                FailureDetail = DiagnosticFailure.FromException("RazerDriverInstall", "Inspect", "Inspection", "InspectionFailed", exception),
            };
        }
    }

    public async Task<RazerDriverInstallOutcome> InstallAsync(CancellationToken cancellationToken)
    {
        bool possiblySubmitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RazerDriverInstallSnapshot baseline = windowsUpdate is null ? platform.Capture() : platform.CaptureForWindowsUpdate();
            if (!baseline.Admitted)
            {
                TraceBlocked(baseline);
                return baseline.AdmissionFailure;
            }
            RazerKeyboardDriverRecipe? recipe = RazerKeyboardDriverRecipes.FindById(baseline.RecipeId);
            if (recipe is null)
            {
                return RazerDriverInstallOutcome.Unavailable;
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

            if (windowsUpdate is not null)
            {
                RazerDriverInstallOutcome? updateOutcome = await InstallWindowsUpdateAsync(baseline, recipe, journal,
                    () => possiblySubmitted = true, cancellationToken).ConfigureAwait(false);
                if (updateOutcome is { } outcome) { return outcome; }
                // Only an initial, successful search with no matching offers reaches this fallback.
                // Fresh fixed-package admission still refuses incompatible existing files.
                baseline = platform.Capture();
                if (!MatchesRecipe(baseline, recipe))
                {
                    TraceBlocked(baseline);
                    return baseline.AdmissionFailure;
                }
            }

            string root = await platform.AcquireVerifiedAsync(journal.PackageDestination, recipe, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            baseline = platform.Capture();
            if (!MatchesRecipe(baseline, recipe))
            {
                TraceBlocked(baseline);
                return baseline.Admitted ? RazerDriverInstallOutcome.Unavailable : baseline.AdmissionFailure;
            }

            if (baseline.Installed)
            {
                return RazerDriverInstallOutcome.Installed;
            }

            journal.Begin(baseline);
            foreach (RazerDriverMutation operation in Operations(recipe))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RazerDriverInstallSnapshot before = platform.Capture();
                if (!MatchesRecipe(before, recipe))
                {
                    if (!possiblySubmitted)
                    {
                        journal.Complete();
                    }

                    return possiblySubmitted ? RazerDriverInstallOutcome.RecoveryRequired
                        : before.Admitted ? RazerDriverInstallOutcome.Unavailable : before.AdmissionFailure;
                }
                cancellationToken.ThrowIfCancellationRequested();
                journal.Intent(operation); // Stable-storage flush precedes native submission.
                possiblySubmitted = true;
                RazerDriverMutationResult result = await platform.MutateAsync(root, operation, recipe, cancellationToken)
                    .ConfigureAwait(false);
                journal.RecordResult(result);
                if (result.ExitCode == 3010)
                {
                    return RazerDriverInstallOutcome.RestartRequired;
                }

                RazerDriverInstallSnapshot after = platform.Capture();
                journal.Observe(result, after);

                if (result.ExitCode != 0 || !MatchesRecipe(after, recipe))
                {
                    return RazerDriverInstallOutcome.RecoveryRequired;
                }
            }
            RazerDriverInstallSnapshot final = platform.Capture();
            if (!MatchesRecipe(final, recipe) || !final.Installed)
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or Win32Exception or System.Security.Cryptography.CryptographicException
            or System.Text.Json.JsonException or TimeoutException or HttpRequestException)
        {
            DiagnosticTrace.TraceException(
                nameof(RazerKeyboardDriverInstaller),
                "InstallAsync",
                exception,
                possiblySubmitted ? "A driver installation may have been submitted." : "No driver installation was submitted.");
            return possiblySubmitted ? RazerDriverInstallOutcome.RecoveryRequired : RazerDriverInstallOutcome.Failed;
        }
    }

    private static bool MatchesRecipe(RazerDriverInstallSnapshot snapshot, RazerKeyboardDriverRecipe recipe) =>
        snapshot.Admitted && string.Equals(snapshot.RecipeId, recipe.Id, StringComparison.Ordinal);

    internal static IEnumerable<RazerDriverMutation> Operations() => Operations(RazerKeyboardDriverRecipes.Pid02E0);

    internal static IEnumerable<RazerDriverMutation> Operations(RazerKeyboardDriverRecipe recipe) =>
        recipe.StageOrder.Select(path => new RazerDriverMutation(path, false))
            .Concat(recipe.InstallOrder.Select(path => new RazerDriverMutation(path, true)));
}
