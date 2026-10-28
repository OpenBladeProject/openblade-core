using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal sealed record RazerKeyboardWindowsUpdateTarget(string DeviceInstanceId, IReadOnlyList<string> HardwareIds);
internal sealed record RazerKeyboardWindowsUpdateIdentity(string UpdateId, int RevisionNumber);
internal sealed record RazerKeyboardWindowsUpdateEntry(string HardwareId, string Provider);
internal sealed record RazerKeyboardWindowsUpdateOffer(RazerKeyboardWindowsUpdateIdentity Identity,
    IReadOnlyList<RazerKeyboardWindowsUpdateEntry> Entries, bool IsDriver = true, bool IsHidden = false,
    bool IsInstalled = false, bool HasBundledUpdates = false, bool RequiresUserInput = false, bool EulaAccepted = true);
internal sealed record RazerKeyboardWindowsUpdateSelection(string RecipeId,
    IReadOnlyList<RazerKeyboardWindowsUpdateOffer> Offers, IReadOnlyList<RazerKeyboardWindowsUpdateTarget> Targets);
internal enum RazerKeyboardWindowsUpdateStatus { NoOffers, Installed, RestartRequired, Failed, RecoveryRequired }
internal sealed record RazerKeyboardWindowsUpdateOperation(string Stage, int ResultCode, int HResult,
    IReadOnlyList<RazerKeyboardWindowsUpdateItemResult> Updates, bool RebootRequired = false)
{
    public IReadOnlyList<int> NativeErrors { get; init; } = [];
}
internal sealed record RazerKeyboardWindowsUpdateItemResult(RazerKeyboardWindowsUpdateIdentity Identity,
    int ResultCode, int HResult, bool RebootRequired = false);
internal sealed record RazerKeyboardWindowsUpdateResult(RazerKeyboardWindowsUpdateStatus Status, string Stage,
    string? DiagnosticFailure, bool InstallationAttempted, RazerKeyboardWindowsUpdateSelection? Selection,
    IReadOnlyList<RazerKeyboardWindowsUpdateOperation> Operations)
{
    public OpenBlade.Contracts.DiagnosticFailure? FailureDetail { get; init; }
}
internal sealed record RazerKeyboardWindowsUpdateSearchResult(IReadOnlyList<RazerKeyboardWindowsUpdateOffer> Offers,
    RazerKeyboardWindowsUpdateOperation Operation);

internal interface IRazerKeyboardWindowsUpdateSource
{
    Task<RazerKeyboardWindowsUpdateResult> TryInstallAsync(RazerKeyboardDriverRecipe recipe,
        IReadOnlyList<RazerKeyboardWindowsUpdateTarget> targets,
        Action<RazerKeyboardWindowsUpdateSelection> beforeInstall, CancellationToken cancellationToken);
}

// This boundary owns a single WUA session and retains the actual searched COM updates until install.
// Tests implement it entirely in memory; constructing the source does not activate COM.
internal interface IRazerKeyboardWindowsUpdateSession : IDisposable
{
    Task<RazerKeyboardWindowsUpdateSearchResult> SearchAsync(string criteria, CancellationToken cancellationToken);
    Task<RazerKeyboardWindowsUpdateOperation> DownloadAsync(RazerKeyboardWindowsUpdateSelection selection,
        CancellationToken cancellationToken);
    Task<RazerKeyboardWindowsUpdateOperation> InstallAsync(RazerKeyboardWindowsUpdateSelection selection,
        Action beforeSubmission, CancellationToken cancellationToken);
}

internal sealed class RazerKeyboardWindowsUpdateSource(
    Func<IRazerKeyboardWindowsUpdateSession> createSession,
    Func<RazerKeyboardDriverRecipe, IReadOnlyList<string>> reviewedHardwareIds) : IRazerKeyboardWindowsUpdateSource
{
    internal const string AutomaticCriteria = "Type='Driver' and IsInstalled=0 and IsHidden=0 and BrowseOnly=0";
    internal const string OptionalCriteria = "Type='Driver' and IsInstalled=0 and IsHidden=0 and BrowseOnly=1";
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    public async Task<RazerKeyboardWindowsUpdateResult> TryInstallAsync(RazerKeyboardDriverRecipe recipe,
        IReadOnlyList<RazerKeyboardWindowsUpdateTarget> targets,
        Action<RazerKeyboardWindowsUpdateSelection> beforeInstall, CancellationToken cancellationToken)
    {
        string stage = "Admission";
        bool attempted = false;
        RazerKeyboardWindowsUpdateSelection? selection = null;
        List<RazerKeyboardWindowsUpdateOperation> operations = [];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(RazerKeyboardDriverRecipes.FindById(recipe.Id), recipe))
            {
                throw RazerKeyboardWindowsUpdateException.Refused("Admission", "RecipeNotAdmitted", new { recipe.Id, recipe.ProductId });
            }

            string[] reviewed = reviewedHardwareIds(recipe).ToArray();
            string? admissionReason = reviewed.Length == 0 ? "ReviewedHardwareIdsMissing"
                : targets.Count == 0 ? "CurrentTargetsMissing"
                : targets.Count > 32 ? "TargetCountExceeded"
                : targets.Any(t => string.IsNullOrWhiteSpace(t.DeviceInstanceId)) ? "TargetInstanceIdMissing"
                : targets.Any(t => t.HardwareIds.Count == 0) ? "TargetHardwareIdsMissing"
                : targets.Select(t => t.DeviceInstanceId).Distinct(IdComparer).Count() != targets.Count ? "DuplicateTargetInstanceId"
                : null;
            if (admissionReason is not null)
            {
                throw RazerKeyboardWindowsUpdateException.Refused("Admission", admissionReason,
                    new
                    {
                        recipe.Id,
                        ReviewedHardwareIds = reviewed,
                        TargetCount = targets.Count,
                        Targets = targets.Take(32).Select(t => new
                        {
                            HardwareIdentity = string.Join('\\', t.DeviceInstanceId.Split('\\').Take(2)),
                            t.HardwareIds,
                        }),
                    });
            }
            // Freeze journal provenance before passing control to asynchronous/native code.
            targets = targets.Select(t => new RazerKeyboardWindowsUpdateTarget(t.DeviceInstanceId, t.HardwareIds.ToArray())).ToArray();
            using IRazerKeyboardWindowsUpdateSession session = createSession();
            List<RazerKeyboardWindowsUpdateOffer> offers = [];
            foreach (string criteria in new[] { AutomaticCriteria, OptionalCriteria })
            {
                stage = "Search";
                RazerKeyboardWindowsUpdateSearchResult search = await session.SearchAsync(criteria, cancellationToken).ConfigureAwait(false);
                operations.Add(search.Operation);
                RequireSuccess(search.Operation, false);
                offers.AddRange(search.Offers);
            }
            stage = "Selection";
            var chosen = new Dictionary<RazerKeyboardWindowsUpdateIdentity, RazerKeyboardWindowsUpdateOffer>();
            foreach (RazerKeyboardWindowsUpdateOffer offer in offers)
            {
                if (offer.Entries.Count == 0)
                {
                    throw RazerKeyboardWindowsUpdateException.Refused("Selection", "DriverEntriesMissing", new { RecipeId = recipe.Id, Offer = offer });
                }
                bool applicable = offer.Entries.Any(e => reviewed.Contains(e.HardwareId, IdComparer)
                    && targets.Any(t => t.HardwareIds.Contains(e.HardwareId, IdComparer)));
                if (!applicable)
                {
                    continue; // Never use a title, manufacturer substring or broad Razer search as authority.
                }

                string? refusal = !Guid.TryParse(offer.Identity.UpdateId, out _) || offer.Identity.RevisionNumber <= 0 ? "UpdateIdentityInvalid"
                    : !offer.IsDriver ? "NonDriverOffer"
                    : offer.IsHidden ? "HiddenOffer"
                    : offer.IsInstalled ? "AlreadyInstalledOffer"
                    : offer.HasBundledUpdates ? "BundledUpdatesRefused"
                    : offer.RequiresUserInput ? "InteractiveUpdateRefused"
                    : !offer.EulaAccepted ? "LicenseNotAccepted"
                    : offer.Entries.Count == 0 ? "DriverEntriesMissing"
                    : offer.Entries.Count > 16 ? "DriverEntryCountExceeded"
                    : offer.Entries.Any(e => !reviewed.Contains(e.HardwareId, IdComparer)) ? "UnreviewedHardwareId"
                    : offer.Entries.Any(e => !targets.Any(t => t.HardwareIds.Contains(e.HardwareId, IdComparer))) ? "CurrentTargetMissing"
                    : offer.Entries.Any(e => !(string.Equals(e.Provider, "Razer Inc", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.Provider, "Razer Inc.", StringComparison.OrdinalIgnoreCase))) ? "ProviderMismatch"
                    : null;
                if (refusal is not null)
                {
                    throw RazerKeyboardWindowsUpdateException.Refused("Selection", refusal, new { RecipeId = recipe.Id, Offer = offer });
                }

                if (chosen.TryGetValue(offer.Identity, out var prior)
                    && !prior.Entries.SequenceEqual(offer.Entries))
                {
                    throw RazerKeyboardWindowsUpdateException.Refused("Selection", "InconsistentUpdateMetadata",
                        new { RecipeId = recipe.Id, PreviousOffer = prior, Offer = offer });
                }

                chosen[offer.Identity] = offer;
            }
            if (chosen.Count == 0)
            {
                return Result(RazerKeyboardWindowsUpdateStatus.NoOffers);
            }

            if (chosen.Count > 8)
            {
                throw RazerKeyboardWindowsUpdateException.Refused("Selection", "SelectedOfferCountExceeded",
                    new { RecipeId = recipe.Id, OfferCount = chosen.Count, Offers = chosen.Values.Take(9).ToArray() });
            }
            if (chosen.Values.SelectMany(o => o.Entries.Select(e => (o.Identity, e.HardwareId)))
                .GroupBy(x => x.HardwareId, IdComparer).Any(g => g.Select(x => x.Identity).Distinct().Count() > 1))
            {
                throw RazerKeyboardWindowsUpdateException.Refused("Selection", "CompetingOffers",
                    new { RecipeId = recipe.Id, Offers = chosen.Values.ToArray() });
            }

            selection = new(recipe.Id, chosen.Values.ToArray(), targets);
            stage = "Download";
            var downloaded = await session.DownloadAsync(selection, cancellationToken).ConfigureAwait(false);
            operations.Add(downloaded);
            RequireSuccess(downloaded, true);
            RequireSelectedResults(downloaded, selection);
            stage = "Install";
            var installed = await session.InstallAsync(selection, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                beforeInstall(selection); // Flush durable intent and perform fresh exact-host/all-target admission here.
                cancellationToken.ThrowIfCancellationRequested();
                attempted = true; // Immediately before the actual native BeginInstall; failure is conservatively uncertain.
            }, cancellationToken).ConfigureAwait(false);
            operations.Add(installed);
            RequireSuccess(installed, true);
            RequireSelectedResults(installed, selection);
            return Result(installed.RebootRequired || installed.Updates.Any(u => u.RebootRequired)
                ? RazerKeyboardWindowsUpdateStatus.RestartRequired : RazerKeyboardWindowsUpdateStatus.Installed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            operations.Add(new(stage, 4, ex.HResult, [], false));
            DiagnosticFailure cause = OpenBlade.Contracts.DiagnosticFailure.FromException("SnapTapWindowsUpdate", "Install", stage,
                ex is OperationCanceledException ? "OperationCanceled" : "NativeOperationFailed", ex, attempted);
            DiagnosticFailure detail = ex is RazerKeyboardWindowsUpdateException { FailureDetail: { } refusal }
                ? (refusal with { MayHaveSubmitted = attempted, HResult = ex.HResult, ExceptionType = ex.GetType().Name, Cause = cause }).LimitDetails()
                : (cause with { Context = JsonSerializer.Serialize(new { Operations = operations }) }).LimitDetails();
            return Result(ex is RazerKeyboardWindowsUpdateException { RestartRequired: true }
                ? RazerKeyboardWindowsUpdateStatus.RestartRequired
                : attempted ? RazerKeyboardWindowsUpdateStatus.RecoveryRequired : RazerKeyboardWindowsUpdateStatus.Failed,
                $"{stage}: {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})") with
            { FailureDetail = detail };
        }

        RazerKeyboardWindowsUpdateResult Result(RazerKeyboardWindowsUpdateStatus status, string? failure = null) =>
            new(status, stage, failure, attempted, selection, operations.ToArray());
    }

    private static void RequireSelectedResults(RazerKeyboardWindowsUpdateOperation operation, RazerKeyboardWindowsUpdateSelection selection)
    {
        if (operation.Updates.Count != selection.Offers.Count
            || !operation.Updates.Select(item => item.Identity).OrderBy(id => id.UpdateId).ThenBy(id => id.RevisionNumber)
                .SequenceEqual(selection.Offers.Select(offer => offer.Identity).OrderBy(id => id.UpdateId).ThenBy(id => id.RevisionNumber)))
        {
            throw new InvalidOperationException("Windows Update results did not cover the exact selected update identities.");
        }
    }

    private static void RequireSuccess(RazerKeyboardWindowsUpdateOperation operation, bool requireItems)
    {
        // orcSucceededWithErrors is not success: partially applied installs require recovery/readback.
        if (operation.ResultCode != 2 || operation.HResult < 0 || operation.NativeErrors.Any(code => code < 0) || requireItems && operation.Updates.Count == 0
            || operation.Updates.Any(item => item.ResultCode != 2 || item.HResult < 0))
        {
            throw new InvalidOperationException($"Windows Update {operation.Stage} did not fully succeed (result {operation.ResultCode}, HRESULT 0x{operation.HResult:X8}).");
        }
    }
}

internal sealed class RazerKeyboardWindowsUpdateException : Exception
{
    public RazerKeyboardWindowsUpdateException(string message, int hresult, bool restartRequired = false,
        DiagnosticFailure? failureDetail = null) : base(message)
    {
        HResult = hresult;
        RestartRequired = restartRequired;
        FailureDetail = failureDetail;
    }
    public bool RestartRequired { get; }
    public DiagnosticFailure? FailureDetail { get; }

    internal static RazerKeyboardWindowsUpdateException Refused(string stage, string reason, object observed) =>
        new($"Windows Update refused the operation: {reason}.", unchecked((int)0x80131509), failureDetail:
            new DiagnosticFailure("SnapTapWindowsUpdate", "Install", stage, reason)
            { Context = JsonSerializer.Serialize(observed) }.LimitDetails());
}
