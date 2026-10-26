using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class RazerKeyboardDriverWindowsUpdateInstallerTests
{
    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public async Task WindowsUpdateIsFirstAndOnlySourceWhenVerifiedStackCompletes(string productId)
    {
        var platform = new Platform(productId);
        var updates = new Updates(platform, RazerKeyboardWindowsUpdateStatus.Installed) { CompleteStack = true };
        var outcome = await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(default);
        Assert.Equal(RazerDriverInstallOutcome.Installed, outcome);
        Assert.False(platform.DownloadedPinned);
        Assert.Equal(0, platform.PinnedMutations);
        Assert.Equal(1, updates.NativeInstalls);
        Assert.False(platform.Journal.RequiresRecovery);
        int intent = platform.Events.IndexOf("journal:wu-intent");
        Assert.Equal("capture:wu", platform.Events[intent + 1]);
        Assert.Equal("native:wu-install", platform.Events[intent + 2]);
        Assert.Equal(platform.Recipe.Id, updates.Selections.Single().RecipeId);
    }

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public async Task DefinitiveInitialNoOffersUsesUnchangedPinnedRecipe(string productId)
    {
        var platform = new Platform(productId);
        var updates = new Updates(platform, RazerKeyboardWindowsUpdateStatus.NoOffers);
        Assert.Equal(RazerDriverInstallOutcome.Installed,
            await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(default));
        Assert.True(platform.DownloadedPinned);
        Assert.Equal(10, platform.PinnedMutations);
        Assert.Equal(0, updates.NativeInstalls);
        Assert.True(platform.Events.IndexOf("wu:query") < platform.Events.IndexOf("pinned:download"));
        Assert.False(platform.Journal.RequiresRecovery);
    }

    [Theory]
    [InlineData((int)RazerKeyboardWindowsUpdateStatus.Failed, false, RazerDriverInstallOutcome.WindowsUpdateUnavailable)]
    [InlineData((int)RazerKeyboardWindowsUpdateStatus.RecoveryRequired, true, RazerDriverInstallOutcome.RecoveryRequired)]
    [InlineData((int)RazerKeyboardWindowsUpdateStatus.RestartRequired, true, RazerDriverInstallOutcome.RestartRequired)]
    public async Task FailedPartialOrRestartedWindowsUpdateNeverFallsBack(
        int statusValue, bool attempted, RazerDriverInstallOutcome expected)
    {
        var status = (RazerKeyboardWindowsUpdateStatus)statusValue;
        var platform = new Platform("02C6");
        var updates = new Updates(platform, status) { Attempt = attempted };
        Assert.Equal(expected, await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(default));
        Assert.False(platform.DownloadedPinned);
        Assert.Equal(attempted, platform.Journal.RequiresRecovery);
    }

    [Fact]
    public async Task MissingDependencyMayRescanOnlyAfterObservedNewChild()
    {
        var platform = new Platform("02C6");
        var updates = new Updates(platform, RazerKeyboardWindowsUpdateStatus.Installed,
            RazerKeyboardWindowsUpdateStatus.Installed)
        { AddChildFirst = true, CompleteStack = true };
        Assert.Equal(RazerDriverInstallOutcome.Installed,
            await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(default));
        Assert.Equal(2, updates.NativeInstalls);
        Assert.Equal(2, updates.Selections.Count);
        Assert.Equal(2, updates.Selections[1].Targets.Count);
        Assert.False(platform.DownloadedPinned);
        Assert.False(platform.Journal.RequiresRecovery);
    }

    [Fact]
    public async Task NoOffersAfterPartialInstallationStopsWithoutPinnedFallback()
    {
        var platform = new Platform("02C6");
        var updates = new Updates(platform, RazerKeyboardWindowsUpdateStatus.Installed,
            RazerKeyboardWindowsUpdateStatus.NoOffers)
        { AddChildFirst = true };
        Assert.Equal(RazerDriverInstallOutcome.RecoveryRequired,
            await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(default));
        Assert.Equal(1, updates.NativeInstalls);
        Assert.False(platform.DownloadedPinned);
        Assert.True(platform.Journal.RequiresRecovery);
    }

    [Fact]
    public async Task SuccessWithoutReadbackOrNewChildDoesNotRetry()
    {
        var platform = new Platform("02C6");
        var updates = new Updates(platform, RazerKeyboardWindowsUpdateStatus.Installed);
        Assert.Equal(RazerDriverInstallOutcome.RecoveryRequired,
            await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(default));
        Assert.Equal(1, updates.NativeInstalls);
        Assert.True(platform.Journal.RequiresRecovery);
        Assert.False(platform.DownloadedPinned);
    }

    [Fact]
    public async Task ChangedTargetSetAfterDownloadPreventsNativeSubmission()
    {
        var platform = new Platform("02C6");
        var updates = new Updates(platform, RazerKeyboardWindowsUpdateStatus.Installed) { ChangeTargetsBeforeIntent = true };
        Assert.Equal(RazerDriverInstallOutcome.RecoveryRequired,
            await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(default));
        Assert.Equal(0, updates.NativeInstalls);
        Assert.True(platform.Journal.RequiresRecovery);
        Assert.False(platform.DownloadedPinned);
    }

    [Fact]
    public async Task JournalFailureBeforeIntentNeverReachesWindowsUpdateInstallation()
    {
        var platform = new Platform("02C6");
        platform.Journal.ThrowIntent = true;
        var updates = new Updates(platform, RazerKeyboardWindowsUpdateStatus.Installed);
        Assert.Equal(RazerDriverInstallOutcome.WindowsUpdateUnavailable,
            await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(default));
        Assert.Equal(0, updates.NativeInstalls);
        Assert.False(platform.Journal.RequiresRecovery);
        Assert.False(platform.DownloadedPinned);
    }

    [Fact]
    public async Task PendingTransactionCannotBeClearedByHealthyInstalledStack()
    {
        var platform = new Platform("02C6") { Installed = true, Pending = true };
        var updates = new Updates(platform, RazerKeyboardWindowsUpdateStatus.Installed);
        Assert.Equal(RazerDriverInstallOutcome.RecoveryRequired,
            await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(default));
        Assert.Equal(0, updates.Queries);
        Assert.False(platform.DownloadedPinned);
    }

    [Fact]
    public async Task PinnedFallbackMustPassItsOwnCurrentImageAdmission()
    {
        var platform = new Platform("02C6") { PinnedAdmitted = false };
        var updates = new Updates(platform, RazerKeyboardWindowsUpdateStatus.NoOffers);
        Assert.Equal(RazerDriverInstallOutcome.Unavailable,
            await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(default));
        Assert.Equal(1, updates.Queries);
        Assert.False(platform.DownloadedPinned);
        Assert.Equal(0, platform.PinnedMutations);
    }

    [Fact]
    public async Task CancellationBeforeInstallationRemainsCanceledWithoutPinnedFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform("02C6");
        var updates = new Updates(platform, RazerKeyboardWindowsUpdateStatus.Failed) { OnQuery = cancellation.Cancel };
        Assert.Equal(RazerDriverInstallOutcome.Canceled,
            await new RazerKeyboardDriverInstaller(platform, updates).InstallAsync(cancellation.Token));
        Assert.Equal(0, updates.NativeInstalls);
        Assert.False(platform.DownloadedPinned);
        Assert.False(platform.Journal.RequiresRecovery);
    }

    private sealed class Platform : IRazerDriverInstallPlatform
    {
        internal RazerKeyboardDriverRecipe Recipe { get; }
        internal List<string> Events { get; } = [];
        internal Journal Journal { get; }
        internal bool Installed { get; set; }
        internal bool Pending { get; init; }
        internal bool PinnedAdmitted { get; init; } = true;
        internal bool DownloadedPinned { get; private set; }
        internal int PinnedMutations { get; private set; }
        internal List<RazerKeyboardWindowsUpdateTarget> Targets { get; } = [];
        internal Platform(string productId)
        {
            Recipe = RazerKeyboardDriverRecipes.All.Single(recipe => recipe.ProductId == productId);
            Targets.Add(new($@"USB\VID_1532&PID_{productId}&MI_01\test",
                [$@"USB\VID_1532&PID_{productId}&MI_01"]));
            Journal = new(Events);
        }
        public RazerDriverInstallSnapshot Capture()
        {
            Events.Add("capture:pinned");
            return Snapshot(PinnedAdmitted, PinnedMutations == 10);
        }
        public RazerDriverInstallSnapshot CaptureForWindowsUpdate()
        {
            Events.Add("capture:wu");
            return Snapshot(true, Installed);
        }
        private RazerDriverInstallSnapshot Snapshot(bool admitted, bool installed) => new(admitted, installed, [], "test")
        {
            RecipeId = Recipe.Id,
            WindowsUpdateTargets = Targets.Select(target => new RazerKeyboardWindowsUpdateTarget(
                target.DeviceInstanceId, target.HardwareIds.ToArray())).ToArray(),
            FailureDetail = admitted ? null : WindowsRazerDriverInstallPlatform.InspectionFailure(
                "ExistingImages", "ExistingImageDiffersFromPinnedPackage", Recipe),
        };
        public bool HasUnfinishedTransaction() => Pending;
        public IRazerDriverInstallJournal OpenJournal() => Journal;
        public Task<string> AcquireVerifiedAsync(string destination, RazerKeyboardDriverRecipe recipe, CancellationToken cancellationToken)
        {
            Assert.Same(Recipe, recipe);
            Events.Add("pinned:download");
            DownloadedPinned = true;
            return Task.FromResult("verified-package");
        }
        public Task<RazerDriverMutationResult> MutateAsync(string root, RazerDriverMutation operation,
            RazerKeyboardDriverRecipe recipe, CancellationToken cancellationToken)
        {
            Assert.Same(Recipe, recipe);
            PinnedMutations++;
            return Task.FromResult(new RazerDriverMutationResult(0, ""));
        }
    }

    private sealed class Updates(Platform platform, params RazerKeyboardWindowsUpdateStatus[] outcomes) : IRazerKeyboardWindowsUpdateSource
    {
        internal int Queries { get; private set; }
        internal int NativeInstalls { get; private set; }
        internal List<RazerKeyboardWindowsUpdateSelection> Selections { get; } = [];
        internal bool Attempt { get; init; }
        internal bool CompleteStack { get; init; }
        internal bool AddChildFirst { get; init; }
        internal bool ChangeTargetsBeforeIntent { get; init; }
        internal Action? OnQuery { get; init; }
        public Task<RazerKeyboardWindowsUpdateResult> TryInstallAsync(RazerKeyboardDriverRecipe recipe,
            IReadOnlyList<RazerKeyboardWindowsUpdateTarget> targets,
            Action<RazerKeyboardWindowsUpdateSelection> beforeInstall, CancellationToken cancellationToken)
        {
            platform.Events.Add("wu:query");
            OnQuery?.Invoke();
            var status = outcomes[Math.Min(Queries, outcomes.Length - 1)];
            Queries++;
            RazerKeyboardWindowsUpdateSelection? selection = null;
            bool attempted = false;
            try
            {
                if (status == RazerKeyboardWindowsUpdateStatus.Installed || Attempt)
                {
                    selection = new(recipe.Id,
                        [new(new(Queries == 1 ? "c2898874-6a4d-4491-99f1-1f34df0fb8f1" : "1696e3ab-de48-46a7-b292-17860f50ce5e", 1),
                            [new(targets[0].HardwareIds[0], "Razer Inc.")])], targets);
                    Selections.Add(selection);
                    if (ChangeTargetsBeforeIntent)
                    {
                        platform.Targets.Add(new("foreign", ["foreign"]));
                    }
                    beforeInstall(selection);
                    platform.Events.Add("native:wu-install");
                    NativeInstalls++;
                    attempted = true;
                    if (AddChildFirst && Queries == 1)
                    {
                        platform.Targets.Add(new($@"RZCONTROL\VID_1532&PID_{recipe.ProductId}&MI_00\child", [@"RAZER\DeviceContol"]));
                    }
                    else if (CompleteStack && status == RazerKeyboardWindowsUpdateStatus.Installed)
                    {
                        platform.Installed = true;
                    }
                }
            }
            catch (Exception exception)
            {
                return Task.FromResult(new RazerKeyboardWindowsUpdateResult(RazerKeyboardWindowsUpdateStatus.Failed,
                    "Install", exception.Message, attempted, selection, []));
            }
            return Task.FromResult(new RazerKeyboardWindowsUpdateResult(status, "Test", null, attempted, selection, []));
        }
    }

    private sealed class Journal(List<string> events) : IRazerDriverInstallJournal
    {
        public bool RequiresRecovery { get; private set; }
        public string PackageDestination => "protected-staging";
        internal bool ThrowIntent { get; set; }
        public void Begin(RazerDriverInstallSnapshot baseline) => events.Add("journal:baseline");
        public void Intent(RazerDriverMutation operation) { events.Add("journal:pinned-intent"); RequiresRecovery = true; }
        public void RecordResult(RazerDriverMutationResult result) { }
        public void Observe(RazerDriverMutationResult result, RazerDriverInstallSnapshot after) { }
        public void WindowsUpdateIntent(RazerKeyboardWindowsUpdateSelection selection)
        {
            if (ThrowIntent) { throw new IOException("journal persistence failed"); }
            events.Add("journal:wu-intent");
            RequiresRecovery = true;
        }
        public void WindowsUpdateObserve(RazerKeyboardWindowsUpdateResult result, RazerDriverInstallSnapshot after) => events.Add("journal:wu-observed");
        public void Complete() { events.Add("journal:complete"); RequiresRecovery = false; }
        public void Dispose() { }
    }
}
