using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class RazerKeyboardDriverInstaller
{
    private async Task<RazerDriverInstallOutcome?> InstallWindowsUpdateAsync(RazerDriverInstallSnapshot baseline,
        RazerKeyboardDriverRecipe recipe, IRazerDriverInstallJournal journal, Action<bool> markSubmission,
        CancellationToken cancellationToken)
    {
        journal.Begin(baseline);
        bool submitted = false;
        HashSet<RazerKeyboardWindowsUpdateIdentity> installedUpdates = [];
        // The reviewed recipe has five INF roles. New children may expose a role only after
        // their parent filter is installed, so permit at most five fully observed passes.
        for (int pass = 0; pass < 5; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RazerDriverInstallSnapshot before = platform.CaptureForWindowsUpdate();
            if (!MatchesRecipe(before, recipe))
            {
                TraceBlocked(before);
                if (!submitted) { journal.Complete(); }
                return submitted ? RazerDriverInstallOutcome.RecoveryRequired : before.AdmissionFailure;
            }
            if (before.Installed)
            {
                journal.Complete();
                return RazerDriverInstallOutcome.Installed;
            }
            RazerKeyboardWindowsUpdateResult result = await windowsUpdate!.TryInstallAsync(recipe,
                before.WindowsUpdateTargets, selection =>
                {
                    if (!string.Equals(selection.RecipeId, recipe.Id, StringComparison.Ordinal)
                        || selection.Offers.Any(offer => installedUpdates.Contains(offer.Identity)))
                    {
                        throw new InvalidOperationException("Windows Update attempted to replay an observed update or change the recipe.");
                    }
                    // A durable selected-update intent precedes fresh admission and native submission.
                    journal.WindowsUpdateIntent(selection);
                    // An unexpected escape remains uncertain until the source explicitly reports
                    // whether BeginInstall was reached. Intent alone is not submission evidence.
                    markSubmission(true);
                    RazerDriverInstallSnapshot fresh = platform.CaptureForWindowsUpdate();
                    if (!MatchesRecipe(fresh, recipe) || fresh.Installed
                        || !SameWindowsUpdateTargets(selection.Targets, fresh.WindowsUpdateTargets))
                    {
                        TraceBlocked(fresh);
                        throw RazerKeyboardWindowsUpdateException.Refused("Admission", "SetupAdmissionChanged",
                            new { fresh.Admitted, fresh.Installed, fresh.RecipeId, fresh.AdmissionFailure, fresh.FailureDetail });
                    }
                }, cancellationToken).ConfigureAwait(false);
            submitted |= result.InstallationAttempted;
            markSubmission(submitted);
            if (result.Status == RazerKeyboardWindowsUpdateStatus.Failed && !submitted)
            {
                // The source completed with proof that native installation was never entered.
                // Retain its exact failure and close intent before any fallible readback; a
                // refused/canceled operation still stops here without pinned-package fallback.
                TraceWindowsUpdate(result, null);
                journal.Complete();
                return cancellationToken.IsCancellationRequested ? RazerDriverInstallOutcome.Canceled
                    : result.FailureDetail?.Stage is "Admission" or "Selection" or "NativeAdmission" or "SearchMetadata"
                        ? RazerDriverInstallOutcome.WindowsUpdateRefused : RazerDriverInstallOutcome.WindowsUpdateUnavailable;
            }
            RazerDriverInstallSnapshot after = platform.CaptureForWindowsUpdate();
            if (result.Status == RazerKeyboardWindowsUpdateStatus.Installed && result.InstallationAttempted)
            {
                // WUA completion is not a barrier for derived PnP child enumeration. Retry
                // only independent reads, at most 25 times, without resubmitting an update.
                for (int readback = 0; readback < 25
                    && !(MatchesRecipe(after, recipe) && (after.Installed
                        || HasNewWindowsUpdateChild(before.WindowsUpdateTargets, after.WindowsUpdateTargets, recipe))); readback++)
                {
                    await waitForWindowsUpdateReadback(cancellationToken).ConfigureAwait(false);
                    after = platform.CaptureForWindowsUpdate();
                }
            }
            journal.WindowsUpdateObserve(result, after);
            TraceWindowsUpdate(result, after.FailureDetail);
            switch (result.Status)
            {
                case RazerKeyboardWindowsUpdateStatus.NoOffers when !submitted && pass == 0:
                    journal.Complete();
                    return null;
                case RazerKeyboardWindowsUpdateStatus.RestartRequired:
                    return RazerDriverInstallOutcome.RestartRequired;
                case RazerKeyboardWindowsUpdateStatus.Installed when result.InstallationAttempted
                    && MatchesRecipe(after, recipe):
                    if (after.Installed)
                    {
                        journal.Complete();
                        return RazerDriverInstallOutcome.Installed;
                    }
                    if (result.Selection is null || !HasNewWindowsUpdateChild(before.WindowsUpdateTargets, after.WindowsUpdateTargets, recipe))
                    {
                        // No newly exposed child can justify another installation pass. Avoid replay
                        // when WUA success is not accompanied by independent installation progress.
                        TraceBlocked(after, "Readback", "WindowsUpdateProgressUnconfirmed");
                        return RazerDriverInstallOutcome.RecoveryRequired;
                    }
                    foreach (var offer in result.Selection.Offers) { installedUpdates.Add(offer.Identity); }
                    break;
                default:
                    TraceBlocked(after, "Readback", "WindowsUpdateInstallationUnconfirmed");
                    return RazerDriverInstallOutcome.RecoveryRequired;
            }
        }
        TraceBlocked(baseline, "Readback", "WindowsUpdateDependencyPassLimit");
        return RazerDriverInstallOutcome.RecoveryRequired;
    }

    internal static bool SameWindowsUpdateTargets(IReadOnlyList<RazerKeyboardWindowsUpdateTarget> expected,
        IReadOnlyList<RazerKeyboardWindowsUpdateTarget> current) => expected.Count == current.Count
        && expected.All(target => current.Count(candidate =>
            string.Equals(candidate.DeviceInstanceId, target.DeviceInstanceId, StringComparison.OrdinalIgnoreCase)
            && candidate.HardwareIds.Count == target.HardwareIds.Count
            && candidate.HardwareIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(target.HardwareIds)) == 1);

    internal static bool HasNewWindowsUpdateChild(IReadOnlyList<RazerKeyboardWindowsUpdateTarget> before,
        IReadOnlyList<RazerKeyboardWindowsUpdateTarget> after, RazerKeyboardDriverRecipe recipe)
    {
        var prior = after.Where(target => before.Any(old => string.Equals(old.DeviceInstanceId, target.DeviceInstanceId,
            StringComparison.OrdinalIgnoreCase))).ToArray();
        if (!SameWindowsUpdateTargets(before, prior) || after.Count <= before.Count) { return false; }
        string[] reviewed = WindowsRazerDriverInstallPlatform.ReviewedWindowsUpdateHardwareIds(recipe).ToArray();
        return after.Where(target => !before.Any(old => string.Equals(old.DeviceInstanceId, target.DeviceInstanceId,
            StringComparison.OrdinalIgnoreCase))).All(target => target.HardwareIds.Contains(reviewed[3], StringComparer.OrdinalIgnoreCase)
                || target.HardwareIds.Contains(reviewed[4], StringComparer.OrdinalIgnoreCase));
    }

    private static void TraceBlocked(RazerDriverInstallSnapshot snapshot, string stage = "Admission", string reason = "SetupAdmissionChanged") =>
        DiagnosticTrace.TraceFailure(snapshot.FailureDetail
            ?? new DiagnosticFailure("RazerDriverInstall", "Install", stage, reason)
            { Context = JsonSerializer.Serialize(new { snapshot.Admitted, snapshot.Installed, snapshot.RecipeId, snapshot.AdmissionFailure }) });

    private static void TraceWindowsUpdate(RazerKeyboardWindowsUpdateResult result, DiagnosticFailure? readbackFailure)
    {
        var detail = new DiagnosticFailure("RazerDriverInstall", "WindowsUpdate", result.Stage,
            result.FailureDetail?.Reason ?? result.Status.ToString(), result.InstallationAttempted)
        {
            Message = result.DiagnosticFailure,
            Cause = result.FailureDetail ?? readbackFailure,
            Related = result.FailureDetail is not null && readbackFailure is not null ? [readbackFailure] : null,
            Context = JsonSerializer.Serialize(new
            {
                result.Status,
                result.InstallationAttempted,
                result.Selection?.RecipeId,
                Offers = result.Selection?.Offers,
                Targets = result.Selection?.Targets.Select(target => new
                {
                    HardwareIdentity = string.Join('\\', target.DeviceInstanceId.Split('\\').Take(2)),
                    target.HardwareIds,
                }),
                result.Operations,
            }),
        };
        if (result.Status is RazerKeyboardWindowsUpdateStatus.NoOffers or RazerKeyboardWindowsUpdateStatus.Installed)
        {
            System.Diagnostics.Trace.TraceInformation("Razer driver Windows Update observation: {0}", JsonSerializer.Serialize(detail.LimitDetails()));
        }
        else
        {
            DiagnosticTrace.TraceFailure(detail);
        }
    }
}
