using System.Text.Json;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class RazerKeyboardWindowsUpdateSourceTests
{
    private static readonly RazerKeyboardDriverRecipe Recipe = RazerKeyboardDriverRecipes.Pid02E0;
    private const string KeyboardId = @"HID\VID_1532&PID_02E0&MI_01&COL01";
    private static readonly RazerKeyboardWindowsUpdateIdentity Identity = new("00112233-4455-6677-8899-aabbccddeeff", 4);
    private static RazerKeyboardWindowsUpdateOffer Offer(string hardwareId = KeyboardId) => new(Identity, [new(hardwareId, "Razer Inc.")]);
    private static RazerKeyboardWindowsUpdateTarget Target(string hardwareId = KeyboardId) => new(hardwareId + @"\test", [hardwareId]);
    private static RazerKeyboardWindowsUpdateSource Source(FakeSession session) => new(() => session,
        recipe => WindowsRazerDriverInstallPlatform.ReviewedWindowsUpdateHardwareIds(recipe));

    [Fact]
    public async Task NoOffersSearchesAutomaticAndOptionalBeforeAllowingFallback()
    {
        var session = new FakeSession();
        var result = await Source(session).TryInstallAsync(Recipe, [Target()], _ => throw new Exception(), default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.NoOffers, result.Status);
        Assert.Equal([RazerKeyboardWindowsUpdateSource.AutomaticCriteria, RazerKeyboardWindowsUpdateSource.OptionalCriteria], session.Criteria);
        Assert.False(result.InstallationAttempted);
        Assert.False(session.DownloadCalled);
    }

    [Fact]
    public async Task AllSevenRecipesUseTheirOwnReviewedHardwareIds()
    {
        Assert.Equal(7, RazerKeyboardDriverRecipes.All.Count);
        foreach (var recipe in RazerKeyboardDriverRecipes.All)
        {
            string id = $@"HID\VID_1532&PID_{recipe.ProductId}&MI_01&COL01";
            var session = new FakeSession { Optional = [Offer(id)] };
            bool admitted = false;
            var result = await Source(session).TryInstallAsync(recipe, [Target(id)], selection =>
            {
                Assert.Equal(recipe.Id, selection.RecipeId);
                Assert.Equal(Target(id).DeviceInstanceId, Assert.Single(selection.Targets).DeviceInstanceId);
                admitted = true;
            }, default);
            Assert.Equal(RazerKeyboardWindowsUpdateStatus.Installed, result.Status);
            Assert.True(admitted);
            Assert.True(result.InstallationAttempted);
        }
    }

    [Fact]
    public async Task DuplicateAutomaticAndOptionalIdentityIsInstalledOnce()
    {
        var session = new FakeSession { Automatic = [Offer()], Optional = [Offer()] };
        var result = await Source(session).TryInstallAsync(Recipe, [Target()], _ => { }, default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.Installed, result.Status);
        Assert.Single(result.Selection!.Offers);
    }

    [Theory]
    [InlineData("provider", "ProviderMismatch")]
    [InlineData("software", "NonDriverOffer")]
    [InlineData("hidden", "HiddenOffer")]
    [InlineData("installed", "AlreadyInstalledOffer")]
    [InlineData("bundle", "BundledUpdatesRefused")]
    [InlineData("prompt", "InteractiveUpdateRefused")]
    [InlineData("mixedHardware", "UnreviewedHardwareId")]
    [InlineData("badIdentity", "UpdateIdentityInvalid")]
    [InlineData("license", "LicenseNotAccepted")]
    [InlineData("missingCurrent", "CurrentTargetMissing")]
    public async Task UnsafeMatchingOfferBlocksFallbackAndSubmission(string fault, string expectedReason)
    {
        var offer = fault switch
        {
            "provider" => Offer() with { Entries = [new(KeyboardId, "Someone claiming Razer")] },
            "software" => Offer() with { IsDriver = false },
            "hidden" => Offer() with { IsHidden = true },
            "installed" => Offer() with { IsInstalled = true },
            "bundle" => Offer() with { HasBundledUpdates = true },
            "prompt" => Offer() with { RequiresUserInput = true },
            "license" => Offer() with { EulaAccepted = false },
            "missingCurrent" => Offer() with { Entries = [new(KeyboardId, "Razer Inc."), new(@"RAZER\DeviceContol", "Razer Inc.")] },
            "mixedHardware" => Offer() with { Entries = [new(KeyboardId, "Razer Inc."), new(@"HID\unrelated", "Razer Inc.")] },
            _ => Offer() with { Identity = new("not-a-guid", 1) }
        };
        var session = new FakeSession { Optional = [offer] };
        var result = await Source(session).TryInstallAsync(Recipe, [Target()], _ => throw new Exception(), default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.Failed, result.Status);
        Assert.False(session.DownloadCalled);
        Assert.False(result.InstallationAttempted);
        Assert.Null(result.Selection);
        Assert.Equal(expectedReason, result.FailureDetail!.Reason);
        Assert.NotNull(result.FailureDetail.HResult);
        Assert.NotNull(result.FailureDetail.Cause);
        using var detail = JsonDocument.Parse(result.FailureDetail.Context!);
        var observed = detail.RootElement.GetProperty("Offer");
        Assert.Equal(offer.Identity.UpdateId, observed.GetProperty("Identity").GetProperty("UpdateId").GetString());
        Assert.Equal(offer.EulaAccepted, observed.GetProperty("EulaAccepted").GetBoolean());
        Assert.Equal(offer.HasBundledUpdates, observed.GetProperty("HasBundledUpdates").GetBoolean());
        Assert.Equal(offer.Entries[0].Provider, observed.GetProperty("Entries")[0].GetProperty("Provider").GetString());
    }

    [Fact]
    public async Task WrongModelIsNotSelectedEvenWhenProviderIsRazer()
    {
        var session = new FakeSession { Optional = [Offer(@"HID\VID_1532&PID_02C6&MI_01&COL01")] };
        var result = await Source(session).TryInstallAsync(Recipe, [Target()], _ => throw new Exception(), default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.NoOffers, result.Status);
    }

    [Fact]
    public async Task UnrelatedGenericCompatibleIdIsIgnored()
    {
        var session = new FakeSession { Optional = [Offer(@"HID\KEYBOARD")] };
        var result = await Source(session).TryInstallAsync(Recipe,
            [Target() with { HardwareIds = [KeyboardId, @"HID\KEYBOARD"] }], _ => throw new Exception(), default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.NoOffers, result.Status);
    }

    [Fact]
    public async Task TwoOffersForSameHardwareIdAreAmbiguous()
    {
        var session = new FakeSession { Optional = [Offer(), Offer() with { Identity = Identity with { RevisionNumber = 5 } }] };
        var result = await Source(session).TryInstallAsync(Recipe, [Target()], _ => throw new Exception(), default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.Failed, result.Status);
        Assert.Equal("CompetingOffers", result.FailureDetail!.Reason);
        Assert.Contains("Offers", result.FailureDetail.Context);
    }

    [Fact]
    public async Task BroadCommonOfferBindsEveryCurrentTargetForFreshParentAdmission()
    {
        const string common = @"RAZER\DeviceContol";
        var session = new FakeSession { Optional = [Offer(common)] };
        var targets = new[] { new RazerKeyboardWindowsUpdateTarget(@"RZCONTROL\model\one", [common]),
            new RazerKeyboardWindowsUpdateTarget(@"RZCONTROL\model\two", [common]) };
        var result = await Source(session).TryInstallAsync(Recipe, targets, selection =>
        {
            Assert.Equal(2, selection.Targets.Count);
            throw new InvalidOperationException("Parent rejected unrelated common target ancestry.");
        }, default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.Failed, result.Status);
        Assert.False(result.InstallationAttempted);
        Assert.False(session.Submitted);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("download")]
    [InlineData("busy")]
    public async Task FailureBeforeSubmissionNeverAllowsPinnedFallback(string stage)
    {
        var session = new FakeSession { Optional = [Offer()], Fault = stage };
        var result = await Source(session).TryInstallAsync(Recipe, [Target()], _ => { }, default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.Failed, result.Status);
        Assert.False(result.InstallationAttempted);
        Assert.Contains(result.Operations, operation => operation.HResult == unchecked((int)0x80240009));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("partial")]
    [InlineData("missingResult")]
    [InlineData("wrongIdentity")]
    public async Task AmbiguityAfterSubmissionRequiresRecoveryAndPreservesEvidence(string fault)
    {
        var session = new FakeSession { Optional = [Offer()], Fault = fault };
        var result = await Source(session).TryInstallAsync(Recipe, [Target()], _ => { }, default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.RecoveryRequired, result.Status);
        Assert.True(result.InstallationAttempted);
        Assert.Equal(Identity, Assert.Single(result.Selection!.Offers).Identity);
        Assert.Equal(1, session.SubmissionCount);
        if (fault == "partial")
        {
            Assert.Contains(result.Operations, operation => operation.ResultCode == 3);
        }
    }

    [Fact]
    public async Task RestartStopsFurtherWorkAndKeepsNativePerUpdateResults()
    {
        var session = new FakeSession { Optional = [Offer()], Fault = "reboot" };
        var result = await Source(session).TryInstallAsync(Recipe, [Target()], _ => { }, default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.RestartRequired, result.Status);
        Assert.True(result.Operations.Last().Updates.Single().RebootRequired);
        Assert.Equal(1, session.SubmissionCount);
    }

    [Fact]
    public async Task MissingChildRequiresFreshTargetRescanRatherThanAssumingCompleteStack()
    {
        string child = @"RZVIRTUAL\VID_1532&PID_02E0&MI_00&COL03";
        var session = new FakeSession { Optional = [Offer(child)] };
        var first = await Source(session).TryInstallAsync(Recipe, [Target()], _ => throw new Exception(), default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.NoOffers, first.Status);
        var second = await Source(new FakeSession { Optional = [Offer(child)] })
            .TryInstallAsync(Recipe, [Target(), Target(child)], _ => { }, default);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.Installed, second.Status);
        // Installed means these selected offers succeeded; the parent independently checks complete API bindings.
    }

    [Fact]
    public async Task CancellationBeforeSearchDoesNotCreateNativeSession()
    {
        var source = new RazerKeyboardWindowsUpdateSource(() => throw new Exception("Must not create session"), _ => [KeyboardId]);
        var result = await source.TryInstallAsync(Recipe, [Target()], _ => { }, new CancellationToken(true));
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.Failed, result.Status);
        Assert.Contains("OperationCanceledException", result.DiagnosticFailure);
    }

    [Fact]
    public async Task CancellationAfterAdmissionCallbackStillProvesNoNativeSubmission()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FakeSession { Optional = [Offer()] };
        var result = await Source(session).TryInstallAsync(Recipe, [Target()], _ => cancellation.Cancel(), cancellation.Token);
        Assert.Equal(RazerKeyboardWindowsUpdateStatus.Failed, result.Status);
        Assert.False(result.InstallationAttempted);
        Assert.False(session.Submitted);
        Assert.Equal("OperationCanceled", result.FailureDetail!.Reason);
    }

    [Fact]
    public async Task MissingTargetAdmissionHasConcreteBoundedReasonWithoutOpeningSession()
    {
        var source = new RazerKeyboardWindowsUpdateSource(() => throw new Exception("No session expected"), _ => [KeyboardId]);
        var result = await source.TryInstallAsync(Recipe, [], _ => { }, default);
        Assert.Equal("CurrentTargetsMissing", result.FailureDetail!.Reason);
        Assert.Equal("Admission", result.FailureDetail.Stage);
        using var detail = JsonDocument.Parse(result.FailureDetail.Context!);
        Assert.Equal(0, detail.RootElement.GetProperty("TargetCount").GetInt32());
    }

    private sealed class FakeSession : IRazerKeyboardWindowsUpdateSession
    {
        public RazerKeyboardWindowsUpdateOffer[] Automatic { get; init; } = [];
        public RazerKeyboardWindowsUpdateOffer[] Optional { get; init; } = [];
        public string? Fault { get; init; }
        public List<string> Criteria { get; } = [];
        public bool DownloadCalled { get; private set; }
        public bool Submitted { get; private set; }
        public int SubmissionCount { get; private set; }
        public Task<RazerKeyboardWindowsUpdateSearchResult> SearchAsync(string criteria, CancellationToken cancellationToken)
        {
            Criteria.Add(criteria);
            if (Fault == "search")
            {
                throw new RazerKeyboardWindowsUpdateException("Native search failure", unchecked((int)0x80240009));
            }

            return Task.FromResult(new RazerKeyboardWindowsUpdateSearchResult(
                criteria == RazerKeyboardWindowsUpdateSource.AutomaticCriteria ? Automatic : Optional, new("Search", 2, 0, [])));
        }
        public Task<RazerKeyboardWindowsUpdateOperation> DownloadAsync(RazerKeyboardWindowsUpdateSelection selection, CancellationToken cancellationToken)
        {
            DownloadCalled = true;
            if (Fault == "download")
            {
                throw new RazerKeyboardWindowsUpdateException("Native download failure", unchecked((int)0x80240009));
            }

            return Task.FromResult(Success("Download", selection));
        }
        public Task<RazerKeyboardWindowsUpdateOperation> InstallAsync(RazerKeyboardWindowsUpdateSelection selection, Action beforeSubmission, CancellationToken cancellationToken)
        {
            if (Fault == "busy")
            {
                throw new RazerKeyboardWindowsUpdateException("Native busy failure", unchecked((int)0x80240009));
            }

            beforeSubmission();
            Submitted = true;
            SubmissionCount++;
            if (Fault == "timeout")
            {
                throw new TimeoutException();
            }

            if (Fault == "cancel")
            {
                throw new OperationCanceledException();
            }

            var result = Success("Install", selection);
            if (Fault == "partial")
            {
                result = result with { ResultCode = 3, HResult = unchecked((int)0x80004005) };
            }

            if (Fault == "missingResult")
            {
                result = result with { Updates = [] };
            }
            if (Fault == "wrongIdentity")
            {
                result = result with { Updates = [result.Updates[0] with { Identity = Identity with { RevisionNumber = 999 } }] };
            }

            if (Fault == "reboot")
            {
                result = result with { RebootRequired = true, Updates = [result.Updates[0] with { RebootRequired = true }] };
            }

            return Task.FromResult(result);
        }
        private static RazerKeyboardWindowsUpdateOperation Success(string stage, RazerKeyboardWindowsUpdateSelection selection) =>
            new(stage, 2, 0, selection.Offers.Select(offer => new RazerKeyboardWindowsUpdateItemResult(offer.Identity, 2, 0)).ToArray());
        public void Dispose() { }
    }
}
