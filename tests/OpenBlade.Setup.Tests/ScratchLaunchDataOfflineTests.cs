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
            var aggregate = Assert.IsType<AggregateException>(error);
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            Assert.Contains(created, aggregate.ToString(), StringComparison.Ordinal);
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
        var error = Assert.Throws<IOException>(() => data.Finish(true, budget));
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
        var error = Assert.Throws<IOException>(() => data.Finish(true, budget));
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
        var error = Assert.Throws<IOException>(() => data.Finish(true, budget));
        Assert.Equal(0, held);
        Assert.Equal("preserve", File.ReadAllText(child));
        Assert.Contains(data.Path, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(mode == "cancel" ? "canceled" : mode, error.ToString(), StringComparison.OrdinalIgnoreCase);
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
