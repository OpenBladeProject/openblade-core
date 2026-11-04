
namespace OpenBlade.Tray.Tests;

public sealed class RazerKeyboardDriverInstallerTests
{
    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public async Task StagesAllPackagesBeforeBindingAndConfirmsReadback(string productId)
    {
        var recipe = RazerKeyboardDriverRecipes.All.Single(value => value.ProductId == productId);
        var platform = new FakePlatform { Recipe = recipe };
        Assert.Equal(RazerDriverInstallOutcome.Installed, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Equal(recipe.StageOrder, platform.Mutations.Take(5).Select(operation => operation.RelativeInf));
        Assert.All(platform.Mutations.Take(5), operation => Assert.False(operation.Bind));
        Assert.Equal(recipe.InstallOrder, platform.Mutations.Skip(5).Select(operation => operation.RelativeInf));
        Assert.All(platform.Mutations.Skip(5), operation => Assert.True(operation.Bind));
        Assert.True(platform.Journal.Completed);
        Assert.True(platform.Captures >= 23);
        Assert.Equal(10, platform.Journal.IntentCount);
        Assert.Equal(10, platform.Journal.ObservationCount);
        Assert.Equal(recipe.Id, platform.AcquiredRecipeId);
        Assert.Equal(recipe.Id, platform.Journal.Baseline!.RecipeId);
    }

    [Fact]
    public async Task ExistingVerifiedStackDoesNotDownloadOrCreateJournal()
    {
        var platform = new FakePlatform { AlreadyInstalled = true };
        Assert.Equal(RazerDriverInstallOutcome.Installed, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.False(platform.OpenedJournal);
        Assert.False(platform.Downloaded);
        Assert.Empty(platform.Mutations);
    }

    [Theory]
    [InlineData(RazerDriverInstallOutcome.Unavailable)]
    [InlineData(RazerDriverInstallOutcome.AcPowerRequired)]
    [InlineData(RazerDriverInstallOutcome.RazerSoftwareRunning)]
    public async Task FailedInitialAdmissionDoesNotPrepareOrMutate(RazerDriverInstallOutcome reason)
    {
        var platform = new FakePlatform { RefuseCapture = 1, Refusal = reason };
        Assert.Equal(reason, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.False(platform.OpenedJournal);
        Assert.Empty(platform.Mutations);
    }

    [Fact]
    public async Task AdmissionLossAfterDownloadDoesNotStage()
    {
        var platform = new FakePlatform { RefuseCapture = 2 };
        Assert.Equal(RazerDriverInstallOutcome.Unavailable, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Empty(platform.Mutations);
        Assert.Equal(0, platform.Journal.IntentCount);
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(13, 5)]
    public async Task FreshAdmissionLossStopsBeforeNextMutation(int capture, int mutations)
    {
        var platform = new FakePlatform { RefuseCapture = capture };
        Assert.Equal(RazerDriverInstallOutcome.RecoveryRequired, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Equal(mutations, platform.Mutations.Count);
        Assert.False(platform.Journal.Completed);
    }

    [Fact]
    public async Task BaselinePreservesPreexistingPublishedPackageIdentity()
    {
        var platform = new FakePlatform();
        Assert.Equal(RazerDriverInstallOutcome.Installed, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Equal(new RazerDriverInventoryEntry("oem101.inf", "already-present"), Assert.Single(platform.Journal.Baseline!.Inventory));
    }

    [Fact]
    public async Task CancellationBeforeStartDoesNothing()
    {
        var platform = new FakePlatform();
        Assert.Equal(RazerDriverInstallOutcome.Canceled,
            await new RazerKeyboardDriverInstaller(platform).InstallAsync(new CancellationToken(true)));
        Assert.Equal(0, platform.Captures);
    }

    [Fact]
    public async Task CancellationDuringAcquisitionDoesNotClaimSubmission()
    {
        var platform = new FakePlatform { DownloadCanceled = true };
        Assert.Equal(RazerDriverInstallOutcome.Canceled, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Empty(platform.Mutations);
        Assert.Equal(0, platform.Journal.IntentCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task AmbiguousSubmissionStopsAndRetainsJournalAtEveryPhase(int failAt)
    {
        var platform = new FakePlatform { AmbiguousAt = failAt };
        Assert.Equal(RazerDriverInstallOutcome.RecoveryRequired, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Equal(failAt + 1, platform.Mutations.Count);
        Assert.False(platform.Journal.Completed);
        Assert.True(platform.Journal.RequiresRecovery);
    }

    [Theory]
    [InlineData(3010, RazerDriverInstallOutcome.RestartRequired)]
    [InlineData(5, RazerDriverInstallOutcome.RecoveryRequired)]
    [InlineData(1602, RazerDriverInstallOutcome.RecoveryRequired)]
    public async Task NonzeroExitStopsWithoutRebootOrRollback(int code, RazerDriverInstallOutcome expected)
    {
        var platform = new FakePlatform { ExitCode = code };
        Assert.Equal(expected, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Single(platform.Mutations);
        Assert.False(platform.Journal.Completed);
    }

    [Fact]
    public async Task ZeroExitWithoutBoundStackRequiresRecovery()
    {
        var platform = new FakePlatform { ReadbackFails = true };
        Assert.Equal(RazerDriverInstallOutcome.RecoveryRequired, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Equal(10, platform.Mutations.Count);
        Assert.False(platform.Journal.Completed);
    }

    [Fact]
    public async Task UnfinishedTransactionNeverReplaysEvenIfStackLooksInstalled()
    {
        var platform = new FakePlatform { Pending = true, AlreadyInstalled = true };
        Assert.Equal(RazerDriverInstallOutcome.RecoveryRequired, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Empty(platform.Mutations);
        Assert.False(platform.Downloaded);
    }

    [Fact]
    public async Task IntentPersistenceFailureNeverStartsNativeProcess()
    {
        var platform = new FakePlatform();
        platform.Journal.FailIntent = true;
        Assert.Equal(RazerDriverInstallOutcome.Failed, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Empty(platform.Mutations);
    }

    [Theory]
    [InlineData("{\"kind\":\"baseline\"}\n", false)]
    [InlineData("{\"kind\":\"intent\"}\n", true)]
    [InlineData("{\"kind\":\"intent\"}\n{\"kind\":\"native-result\"}\n", true)]
    [InlineData("{\"kind\":\"intent\"}\n{\"kind\":\"complete\"}\n", false)]
    [InlineData("{\"kind\":\"complete\"}\n{\"kind\":\"intent\"}\n", true)]
    public void JournalOnlyClearsIntentAfterConfirmedCompletion(string json, bool pending)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(pending, RazerDriverInstallJournal.HasUnfinishedTransaction(stream));
    }

    [Fact]
    public void TruncatedJournalCannotBeReadAsCompleted()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"kind\":\"intent\"}\n{\"kind\":\"comp"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => RazerDriverInstallJournal.HasUnfinishedTransaction(stream));
    }
    [Theory]
    [InlineData(2, 0, RazerDriverInstallOutcome.Unavailable)]
    [InlineData(3, 0, RazerDriverInstallOutcome.Unavailable)]
    [InlineData(4, 1, RazerDriverInstallOutcome.RecoveryRequired)]
    [InlineData(5, 1, RazerDriverInstallOutcome.RecoveryRequired)]
    [InlineData(23, 10, RazerDriverInstallOutcome.RecoveryRequired)]
    public async Task ChangingRecipeNeverMixesModelPackages(int changeAt, int mutations, RazerDriverInstallOutcome expected)
    {
        var platform = new FakePlatform { Recipe = RazerKeyboardDriverRecipes.Pid02C6, ChangeRecipeAt = changeAt };
        Assert.Equal(expected, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Equal(mutations, platform.Mutations.Count);
        Assert.All(platform.Mutations, operation => Assert.Contains(operation, RazerKeyboardDriverInstaller.Operations(platform.Recipe)));
        if (mutations > 0)
        {
            Assert.True(platform.Journal.RequiresRecovery);
            Assert.False(platform.Journal.Completed);
        }
    }

    [Fact]
    public async Task UnknownRecipeCannotDownloadOrInstall()
    {
        var platform = new FakePlatform { UnknownRecipe = true };
        Assert.Equal(RazerDriverInstallOutcome.Unavailable, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.False(platform.Downloaded);
        Assert.False(platform.OpenedJournal);
        Assert.Empty(platform.Mutations);
    }

    [Theory]
    [InlineData(false, RazerDriverInstallOutcome.Failed)]
    [InlineData(true, RazerDriverInstallOutcome.RecoveryRequired)]
    public async Task InvalidPackageIsContainedAndPreservesSubmissionBoundary(bool afterIntent, RazerDriverInstallOutcome expected)
    {
        var platform = new FakePlatform { InvalidPackageDuringMutation = afterIntent, InvalidPackageDuringDownload = !afterIntent };
        Assert.Equal(expected, await new RazerKeyboardDriverInstaller(platform).InstallAsync(default));
        Assert.Equal(afterIntent ? 1 : 0, platform.Mutations.Count);
        Assert.Equal(afterIntent ? 1 : 0, platform.Journal.IntentCount);
        Assert.Equal(afterIntent, platform.Journal.RequiresRecovery);
    }

    private sealed class FakePlatform : IRazerDriverInstallPlatform
    {
        internal readonly List<RazerDriverMutation> Mutations = [];
        internal readonly FakeJournal Journal = new();
        internal bool AlreadyInstalled, Pending, OpenedJournal, Downloaded, DownloadCanceled, ReadbackFails;
        internal int Captures, RefuseCapture = -1, AmbiguousAt = -1, ExitCode;
        internal RazerDriverInstallOutcome Refusal = RazerDriverInstallOutcome.Unavailable;
        internal RazerKeyboardDriverRecipe Recipe = RazerKeyboardDriverRecipes.Pid02E0;
        internal int ChangeRecipeAt = int.MaxValue;
        internal bool UnknownRecipe, InvalidPackageDuringDownload, InvalidPackageDuringMutation;
        internal string? AcquiredRecipeId;

        public RazerDriverInstallSnapshot Capture()
        {
            Captures++;
            return new(Captures != RefuseCapture, AlreadyInstalled || (Mutations.Count == 10 && !ReadbackFails),
                [new("oem101.inf", "already-present")], "test snapshot", Refusal)
            {
                RecipeId = UnknownRecipe ? "unknown-recipe"
                    : Captures >= ChangeRecipeAt ? RazerKeyboardDriverRecipes.Pid02E0.Id : Recipe.Id,
            };
        }
        public bool HasUnfinishedTransaction() => Pending;
        public IRazerDriverInstallJournal OpenJournal() { OpenedJournal = true; return Journal; }
        public Task<string> AcquireVerifiedAsync(string destination, RazerKeyboardDriverRecipe recipe, CancellationToken cancellationToken)
        {
            Downloaded = true;
            AcquiredRecipeId = recipe.Id;
            if (InvalidPackageDuringDownload) { throw new InvalidDataException("Untrusted package input"); }
            return DownloadCanceled ? Task.FromCanceled<string>(new CancellationToken(true)) : Task.FromResult(destination);
        }
        public Task<RazerDriverMutationResult> MutateAsync(string root, RazerDriverMutation operation, RazerKeyboardDriverRecipe recipe, CancellationToken cancellationToken)
        {
            Assert.Equal(Mutations.Count + 1, Journal.IntentCount);
            Assert.Equal(Recipe.Id, recipe.Id);
            Mutations.Add(operation);
            if (InvalidPackageDuringMutation) { throw new InvalidDataException("Changed staged package"); }
            if (Mutations.Count - 1 == AmbiguousAt)
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult(new RazerDriverMutationResult(ExitCode, "simulated Windows output"));
        }
    }

    private sealed class FakeJournal : IRazerDriverInstallJournal
    {
        internal int IntentCount, ObservationCount;
        internal bool Completed, FailIntent;
        internal RazerDriverInstallSnapshot? Baseline;
        public bool RequiresRecovery { get; private set; }
        public string PackageDestination => "unused-test-destination";
        public void Begin(RazerDriverInstallSnapshot baseline) => Baseline = baseline;
        public void Intent(RazerDriverMutation operation)
        {
            if (FailIntent)
            {
                throw new IOException("Simulated flush failure");
            }

            IntentCount++;
            RequiresRecovery = true;
        }
        public void RecordResult(RazerDriverMutationResult result) { }
        public void Observe(RazerDriverMutationResult result, RazerDriverInstallSnapshot after) => ObservationCount++;
        public void Complete() { Completed = true; RequiresRecovery = false; }
        public void Dispose() { }
    }
}
