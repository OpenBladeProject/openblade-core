#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup.Tests;

public sealed class ScratchLaunchDataOfflineTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("nonempty")]
    [InlineData("refused")]
    public void FinalPinFailureCleansOnlyConfirmedEmptyDataOrReportsRetainedPath(string mode)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        string code = Path.Combine(files.Root, "receiver");
        Directory.CreateDirectory(code);
        string sibling = Path.Combine(files.Root, "sibling.txt");
        File.WriteAllText(sibling, "preserve");
        File.WriteAllText(Path.Combine(code, "code.txt"), "inert code");
        int calls = 0, held = 0;
        string? created = null;
        var primary = new IOException("injected final pin failure");
        IDisposable Pin(string path)
        {
            calls++;
            if (calls == 2)
            {
                created = path;
                if (mode == "nonempty") { File.WriteAllText(Path.Combine(path, "unknown.tmp"), "preserve"); }
                throw primary;
            }
            if (calls == 3 && mode == "refused") { throw new IOException("cleanup pin refused"); }
            var pin = files.PinDirectory(path);
            held++;
            return new TrackedLease(pin, () => held--);
        }
        var error = Record.Exception(() => ScratchLaunchData.Create(files.Root, code, Pin));
        Assert.True(ScratchFixtureFailure.IsRefusal(error!));
        Assert.NotNull(created);
        Assert.Equal(3, calls);
        Assert.Equal(0, held);
        if (mode == "empty")
        {
            Assert.Same(primary, error);
            Assert.False(Directory.Exists(created));
        }
        else
        {
            var aggregate = Assert.IsType<AggregateException>(Assert.IsType<ScratchFixtureFailure.RetainedDataException>(error).InnerException);
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            Assert.Contains(created, error!.ToString(), StringComparison.Ordinal);
            Assert.True(Directory.Exists(created));
            if (mode == "nonempty") { Assert.Equal("preserve", File.ReadAllText(Path.Combine(created, "unknown.tmp"))); }
            else { Assert.Contains("cleanup pin refused", aggregate.ToString(), StringComparison.Ordinal); }
        }
        Assert.Equal("preserve", File.ReadAllText(sibling));
        Assert.Equal("inert code", File.ReadAllText(Path.Combine(code, "code.txt")));
    }

    [Fact]
    public void BreadthExhaustionStopsAtSharedEntryBudgetAndReleasesEveryPin()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        int held = 0;
        IDisposable Pin(string path)
        {
            held++;
            return new TrackedLease(files.PinDirectory(path), () => held--);
        }
        var data = ScratchLaunchData.Create(files.Root, Path.Combine(files.Root, "code"), Pin);
        for (int i = 0; i < 3; i++) { File.WriteAllText(Path.Combine(data.Path, i + ".tmp"), "retain on exhaustion"); }
        var budget = new ScratchLaunchData.CleanupBudget(maxEntries: 2);
        var error = Assert.ThrowsAny<IOException>(() => data.Finish(true, budget));
        Assert.Equal(2, budget.Entries);
        Assert.Single(Directory.GetFiles(data.Path));
        Assert.Contains("entry budget", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(data.Path, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, held);
        Assert.Throws<InvalidOperationException>(() => data.Finish(true));
    }

    [Fact]
    public void NestedTraversalSharesBudgetAcrossDirectories()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        var data = ScratchLaunchData.Create(files.Root, Path.Combine(files.Root, "code"), path => files.PinDirectory(path));
        string nested = Directory.CreateDirectory(Path.Combine(data.Path, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "a.tmp"), "one");
        File.WriteAllText(Path.Combine(nested, "b.tmp"), "two");
        var budget = new ScratchLaunchData.CleanupBudget(maxEntries: 2);
        var error = Assert.ThrowsAny<IOException>(() => data.Finish(true, budget));
        Assert.Equal(2, budget.Entries); // The directory and exactly one child.
        Assert.Single(Directory.GetFiles(nested));
        Assert.Contains(data.Path, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("cancel")]
    [InlineData("depth")]
    public void DeadlineCancellationAndDepthRetainDataAndReleasePins(string mode)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        int held = 0;
        IDisposable Pin(string path)
        {
            var lease = files.PinDirectory(path);
            held++;
            return new TrackedLease(lease, () => held--);
        }
        var data = ScratchLaunchData.Create(files.Root, Path.Combine(files.Root, "code"), Pin);
        string nested = Directory.CreateDirectory(Path.Combine(data.Path, "nested")).FullName;
        string child = Path.Combine(nested, "child.tmp");
        File.WriteAllText(child, "preserve");
        using var cancellation = new CancellationTokenSource();
        if (mode == "cancel") { cancellation.Cancel(); }
        var budget = new ScratchLaunchData.CleanupBudget(maxDepth: mode == "depth" ? 0 : 16,
            cancellationToken: cancellation.Token, elapsed: () => mode == "deadline" ? TimeSpan.FromSeconds(3) : TimeSpan.Zero);
        var error = Assert.ThrowsAny<IOException>(() => data.Finish(true, budget));
        Assert.True(ScratchFixtureFailure.IsRefusal(error));
        Assert.Equal(0, held);
        Assert.Equal("preserve", File.ReadAllText(child));
        Assert.Contains(data.Path, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(mode == "cancel" ? "canceled" : mode, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcquisitionRetainsAllCausesAndClassifiesUnexpectedCleanup(bool nested)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        var primary = new UnauthorizedAccessException("expected acquisition refusal");
        var bug = new InvalidOperationException("unexpected cleanup pin fault");
        Exception cleanup = nested ? new AggregateException(new IOException("known"), new AggregateException(bug)) : bug;
        int calls = 0, held = 0;
        string? created = null;
        IDisposable Pin(string path)
        {
            calls++;
            if (calls == 2) { created = path; throw primary; }
            if (calls == 3) { throw cleanup; }
            var pin = files.PinDirectory(path);
            held++;
            return new TrackedLease(pin, () => held--);
        }
        var error = Assert.Throws<ScratchFixtureFailure.RetainedDataException>(() => ScratchLaunchData.Create(files.Root, Path.Combine(files.Root, "code"), Pin));
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.False(ScratchFixtureFailure.IsRefusal(error));
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Same(cleanup, aggregate.InnerExceptions[1]);
        Assert.NotNull(created);
        Assert.Contains(created, error.ToString(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(created));
        Assert.Equal(3, calls);
        Assert.Equal(0, held);
    }

    [Theory]
    [InlineData("traversal")]
    [InlineData("elapsed")]
    [InlineData("final")]
    [InlineData("releases")]
    public void FinishPreservesUnexpectedFaultsRetainedPathAndEveryRelease(string stage)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        var bug = new InvalidOperationException("unexpected " + stage);
        var release = new ArgumentException("unexpected release");
        int calls = 0, held = 0;
        bool finishing = false;
        IDisposable Pin(string path)
        {
            calls++;
            if (finishing && ((stage == "traversal" && calls == 4) || (stage == "final" && calls == 5))) { throw bug; }
            var pin = files.PinDirectory(path);
            held++;
            return new TrackedLease(pin, () =>
            {
                held--;
                if (finishing && stage == "releases") { throw path == files.Root ? bug : release; }
            });
        }
        var data = ScratchLaunchData.Create(files.Root, Path.Combine(files.Root, "code"), Pin);
        finishing = true;
        var budget = new ScratchLaunchData.CleanupBudget(elapsed: () =>
        {
            if (stage == "elapsed") { throw bug; }
            return stage == "releases" ? TimeSpan.FromSeconds(3) : TimeSpan.Zero;
        });
        var error = Assert.Throws<ScratchFixtureFailure.RetainedDataException>(() => data.Finish(true, budget));
        Assert.False(ScratchFixtureFailure.IsRefusal(error));
        Assert.Contains(data.Path, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(bug.ToString(), error.ToString(), StringComparison.Ordinal);
        if (stage == "releases")
        {
            var aggregate = Assert.IsType<AggregateException>(error.InnerException);
            Assert.Equal(3, aggregate.InnerExceptions.Count);
            Assert.Contains("deadline exhausted", error.ToString(), StringComparison.Ordinal);
            Assert.Contains(release.ToString(), error.ToString(), StringComparison.Ordinal);
        }
        Assert.True(Directory.Exists(data.Path));
        Assert.Equal(0, held);
        int attemptedCalls = calls;
        Assert.Throws<InvalidOperationException>(() => data.Finish(true));
        Assert.Equal(attemptedCalls, calls);
    }

    [Fact]
    public void RetainedContextOnlyUnwrapsItsOwnCauseAndKeepsKnownRefusals()
    {
        var bug = new InvalidOperationException("programming fault");
        Assert.True(ScratchFixtureFailure.IsRefusal(new IOException("typed OS wrapper", bug)));
        Assert.True(ScratchFixtureFailure.IsRefusal(new InvalidDataException("typed input wrapper", bug)));
        Exception known = new AggregateException(new UnauthorizedAccessException(),
            new AggregateException(new OperationCanceledException(), new IOException()));
        Assert.True(ScratchFixtureFailure.IsRefusal(new ScratchFixtureFailure.RetainedDataException("path", known)));
        Assert.False(ScratchFixtureFailure.IsRefusal(new AggregateException(known,
            new ScratchFixtureFailure.RetainedDataException("path", new AggregateException(known, bug)))));
    }

    [Fact]
    public void TraversalOperationAndReleaseFaultsBothReachEntryClassification()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        var bug = new InvalidOperationException("elapsed after traversal pin acquisition");
        var release = new IOException("traversal release refused");
        int calls = 0, held = 0, checks = 0;
        IDisposable Pin(string path)
        {
            int call = ++calls;
            var pin = files.PinDirectory(path);
            held++;
            return new TrackedLease(pin, () => { held--; if (call == 4) { throw release; } });
        }
        var data = ScratchLaunchData.Create(files.Root, Path.Combine(files.Root, "code"), Pin);
        var budget = new ScratchLaunchData.CleanupBudget(elapsed: () => ++checks == 2 ? throw bug : TimeSpan.Zero);
        var error = Assert.Throws<ScratchFixtureFailure.RetainedDataException>(() => data.Finish(true, budget));
        Assert.False(ScratchFixtureFailure.IsRefusal(error));
        var causes = Assert.IsType<AggregateException>(error.InnerException).Flatten().InnerExceptions;
        Assert.Contains(bug, causes);
        Assert.Contains(release, causes);
        Assert.Contains(data.Path, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, held);
        Assert.True(Directory.Exists(data.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcquisitionParentReleaseFailureNeverLosesCauseOrAcquiredDataLease(bool failChildPin)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        var bug = new InvalidOperationException("parent release fault");
        var refusal = new IOException("child pin refusal");
        int calls = 0, held = 0;
        string? created = null;
        IDisposable Pin(string path)
        {
            int call = ++calls;
            if (call == 2)
            {
                created = path;
                if (failChildPin) { File.WriteAllText(Path.Combine(path, "unknown.tmp"), "retain"); throw refusal; }
            }
            var pin = files.PinDirectory(path);
            held++;
            return new TrackedLease(pin, () => { held--; if (call == 1) { throw bug; } });
        }
        var error = Assert.Throws<ScratchFixtureFailure.RetainedDataException>(() => ScratchLaunchData.Create(files.Root, Path.Combine(files.Root, "code"), Pin));
        Assert.False(ScratchFixtureFailure.IsRefusal(error));
        Assert.Contains(bug, Assert.IsType<AggregateException>(error.InnerException).Flatten().InnerExceptions);
        if (failChildPin) { Assert.Contains(refusal, Assert.IsType<AggregateException>(error.InnerException).Flatten().InnerExceptions); }
        Assert.NotNull(created);
        Assert.Contains(created, error.ToString(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(created));
        Assert.Equal(0, held);
    }

    [Fact]
    public void AcquisitionReadAndRepinReleaseFailuresRemainVisibleTogether()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        var refusal = new IOException("initial pin refused");
        var bug = new InvalidOperationException("repin release fault");
        int calls = 0, held = 0;
        string? created = null;
        IDisposable Pin(string path)
        {
            int call = ++calls;
            if (call == 2) { created = path; throw refusal; }
            if (call == 3)
            {
                // A private filesystem race makes the actual attribute read fail;
                // inert lease release then fails independently, with no OS probe.
                Directory.Delete(path);
                held++;
                return new TrackedLease(new MemoryStream(), () => { held--; throw bug; });
            }
            var pin = files.PinDirectory(path);
            held++;
            return new TrackedLease(pin, () => held--);
        }
        var error = Assert.Throws<ScratchFixtureFailure.RetainedDataException>(() => ScratchLaunchData.Create(files.Root, Path.Combine(files.Root, "code"), Pin));
        Assert.False(ScratchFixtureFailure.IsRefusal(error));
        var causes = Assert.IsType<AggregateException>(error.InnerException).Flatten().InnerExceptions;
        Assert.Contains(refusal, causes);
        Assert.Contains(bug, causes);
        Assert.Contains(causes, cause => cause is FileNotFoundException or DirectoryNotFoundException);
        Assert.NotNull(created);
        Assert.Contains(created, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, held);
    }

    private sealed class TrackedLease(IDisposable inner, Action released) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            released();
        }
    }
}
#endif
