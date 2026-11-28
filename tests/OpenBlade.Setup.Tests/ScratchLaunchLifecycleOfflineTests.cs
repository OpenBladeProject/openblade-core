#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Security.Cryptography;
using OpenBlade.DeploymentBroker;
using OpenBlade.Deployment;

namespace OpenBlade.Setup.Tests;

// Pure injected outcomes and in-memory streams; no process/token/pipe probes.
public sealed class ScratchLaunchLifecycleOfflineTests
{
    [Fact]
    public void NoninheritedChannelRoutingIsCanonicalAndContainsNoSecret()
    {
        const string id = "1234567890abcdef1234567890abcdef";
        var route = ScratchChildChannels.ParseArguments([ScratchChildChannels.Argument, id, "42"]);
        Assert.Equal((id, 42u), route);
        Assert.Equal("OpenBlade.Scratch.Channels." + id + ".2", ScratchChildChannels.Name(id, 2));
        Assert.Equal(HandleInheritability.None, ScratchChildChannels.Inheritability);
        Assert.True(ScratchChildChannels.ServerOptions.HasFlag(System.IO.Pipes.PipeOptions.FirstPipeInstance));
        Assert.True(ScratchChildChannels.ServerOptions.HasFlag(System.IO.Pipes.PipeOptions.CurrentUserOnly));
        foreach (var args in new string[][] { [], ["wrong", id, "42"], [ScratchChildChannels.Argument, id.ToUpperInvariant(), "42"],
            [ScratchChildChannels.Argument, id, "0"], [ScratchChildChannels.Argument, id, "4294967296"],
            [ScratchChildChannels.Argument, new string('0', 32), "42"] })
        {
            Assert.Throws<InvalidDataException>(() => ScratchChildChannels.ParseArguments(args));
        }
    }
    [Fact]
    public void JobConfigurationAndAtomicAssociationPrecedeCreationAndOnlyThenAdmissionResumes()
    {
        List<string> events = [];
        ScratchNativeReceiverLaunch.AdmitSuspendedChild(() => events.Add("kill-on-close"),
            () => events.Add("job-list"), () => events.Add("create-suspended"),
            () => events.Add("authenticate"), () => events.Add("resume"), () => events.Add("connect-created-child"));
        Assert.Equal(["kill-on-close", "job-list", "create-suspended", "authenticate", "resume", "connect-created-child"], events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void FailureAtEveryAdmissionBoundaryNeverFallsBackOrResumesAgain(int boundary)
    {
        List<int> events = [];
        Action Step(int index) => () =>
        {
            events.Add(index);
            if (index == boundary) { throw new IOException("injected"); }
        };
        Assert.Throws<IOException>(() => ScratchNativeReceiverLaunch.AdmitSuspendedChild(
            Step(0), Step(1), Step(2), Step(3), Step(4), Step(5)));
        Assert.Equal(Enumerable.Range(0, boundary + 1), events);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void PartialChannelAcquisitionCleansEveryOwnedResource(int boundary, bool duringInitialization)
    {
        List<int> released = [];
        var primary = new InvalidDataException("channel acquisition failed");
        Exception error = Assert.ThrowsAny<Exception>(() => ScratchChildChannels.Acquire(
            index =>
            {
                if (index == boundary && !duringInitialization) { throw primary; }
                return new TrackedResource(index, released);
            },
            (_, index) => { if (index == boundary && duringInitialization) { throw primary; } }));
        int acquired = boundary + (duringInitialization ? 1 : 0);
        Assert.Equal(Enumerable.Range(0, acquired), released.Order());
        Assert.Contains(primary.Message, error.ToString(), StringComparison.Ordinal);
        Assert.True(ScratchFixtureFailure.IsRefusal(error));
        if (acquired > 0) { Assert.Contains("close failed", error.ToString(), StringComparison.Ordinal); }
    }

    private sealed class TrackedResource(int index, List<int> released) : IDisposable
    {
        public void Dispose()
        {
            released.Add(index);
            if (index == 0) { throw new IOException("close failed"); }
        }
    }

    [Fact]
    public void PrimaryAndEveryCleanupFailureRemainDeliberateRefusals()
    {
        int attempts = 0;
        var failure = new InvalidDataException("primary");
        var error = Assert.Throws<AggregateException>(() => ScratchFixtureFailure.Cleanup(failure,
            () => { attempts++; throw new IOException("unconfirmed termination"); },
            () => { attempts++; throw new UnauthorizedAccessException("release denied"); }));
        Assert.Equal(2, attempts);
        Assert.Same(failure, error.InnerExceptions[0]);
        Assert.Equal(3, error.InnerExceptions.Count);
        Assert.True(ScratchFixtureFailure.IsRefusal(error));
        Assert.False(ScratchFixtureFailure.IsRefusal(new ArgumentException()));
    }

    [Fact]
    public void ContinuousStderrDrainRetainsBoundedTailAndForwardsUsefulDiagnostics()
    {
        string diagnostic = "readiness refused: exact retained image mismatch";
        var drain = new ScratchDiagnosticDrain(new StringReader(new string('x', 128 * 1024) + diagnostic));
        using var destination = new StringWriter();
        drain.Finish(destination);
        Assert.EndsWith(diagnostic, destination.ToString(), StringComparison.Ordinal);
        Assert.Contains("earlier stderr truncated", destination.ToString(), StringComparison.Ordinal);
        Assert.True(destination.ToString().Length < ScratchDiagnosticDrain.Capacity + 64);
    }

    [Fact]
    public void FailedStderrReadIsNotReportedAsSuccessfulDrain()
    {
        var drain = new ScratchDiagnosticDrain(new FailedReader());
        using var destination = new StringWriter();
        var error = Assert.Throws<IOException>(() => drain.Finish(destination));
        Assert.Equal("read failed", error.Message);
    }

    [Fact]
    public void StalledStderrDrainHasABoundedRefusalAndCancellation()
    {
        var reader = new StalledReader();
        var drain = new ScratchDiagnosticDrain(reader);
        using var destination = new StringWriter();
        Assert.Throws<IOException>(() => drain.Finish(destination, TimeSpan.FromMilliseconds(25)));
        Assert.True(reader.Token.IsCancellationRequested);
    }

    [Fact]
    public void ExactBuiltBytesAndProvenanceAreRequiredIndependently()
    {
        byte[] image = "native fixture bytes"u8.ToArray(), provenance = "current-source/config"u8.ToArray();
        string imageHash = Convert.ToHexString(SHA256.HashData(image));
        string provenanceHash = Convert.ToHexString(SHA256.HashData(provenance));
        ScratchFixtureBuildPin.RequireHashes(new MemoryStream(image), new MemoryStream(provenance), imageHash, provenanceHash);
        Assert.Throws<InvalidDataException>(() => ScratchFixtureBuildPin.RequireHashes(
            new MemoryStream("older image"u8.ToArray()), new MemoryStream(provenance), imageHash, provenanceHash));
        Assert.Throws<InvalidDataException>(() => ScratchFixtureBuildPin.RequireHashes(
            new MemoryStream(image), new MemoryStream("fixture-disabled config"u8.ToArray()), imageHash, provenanceHash));
    }

    [Fact]
    public void CopiedReceiverMatchesThisTestAssemblyBuildPin() =>
        ScratchFixtureBuildPin.Require(Path.Combine(AppContext.BaseDirectory, "OpenBlade.CandidateReceipt.Scratch.exe"));

    [Fact]
    public void OwnedLaunchDataIsFreshOutsideCodeAndDeletedOnlyAfterConfirmedRetirement()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        string code = Path.Combine(files.Root, "receiver");
        Directory.CreateDirectory(code);
        File.WriteAllText(Path.Combine(code, "receiver.exe"), "inert code data");
        var first = ScratchLaunchData.Create(files.Root, code, path => files.PinDirectory(path));
        var second = ScratchLaunchData.Create(files.Root, code, path => files.PinDirectory(path));
        Assert.NotEqual(first.Path, second.Path);
        Directory.CreateDirectory(Path.Combine(first.Path, "nested"));
        File.WriteAllText(Path.Combine(first.Path, "nested", "child.tmp"), "temp");
        File.WriteAllText(Path.Combine(second.Path, "child.tmp"), "temp");
        Assert.Contains("TEMP=" + first.Path + '\0', ScratchNativeReceiverLaunch.CreateEnvironment(code, first.Path), StringComparison.Ordinal);
        List<string> order = [];
        ScratchNativeReceiverLaunch.CleanupLaunch(null, () => order.Add("confirmed"), () => order.Add("drain"),
            () => order.Add("release"), first);
        Assert.Equal(["confirmed", "drain", "release"], order);
        Assert.False(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
        ScratchNativeReceiverLaunch.CleanupLaunch(null, () => { }, null, () => { }, second);
        Assert.False(Directory.Exists(second.Path));
        Assert.Equal(["receiver.exe"], Directory.GetFiles(code).Select(Path.GetFileName));
    }

    [Fact]
    public void UnconfirmedRetirementPreservesAndReportsActualLaunchDataAndStillReleasesResources()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        string code = Path.Combine(files.Root, "receiver");
        Directory.CreateDirectory(code);
        var data = ScratchLaunchData.Create(files.Root, code, path => files.PinDirectory(path));
        File.WriteAllText(Path.Combine(data.Path, "child.tmp"), "unresolved");
        bool released = false;
        var error = Assert.Throws<AggregateException>(() => ScratchNativeReceiverLaunch.CleanupLaunch(
            new InvalidDataException("primary"), () => throw new IOException("stop unconfirmed"), null,
            () => released = true, data));
        Assert.True(released);
        Assert.Contains(data.Path, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("stop unconfirmed", error.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(data.Path, "child.tmp")));
        Assert.Throws<InvalidOperationException>(() => data.Finish(true));
    }

    [Fact]
    public void CleanupRefusesTargetsOutsideExactPrivateLaunchDataRoot()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OpenBlade.private-data"));
        foreach (string target in new[] { root, Path.Combine(root, "receiver"), Path.Combine(root, "..", "other"),
            Path.Combine(root + "-other", "launch-data-" + Guid.NewGuid().ToString("N")) })
        { Assert.Throws<IOException>(() => ScratchLaunchData.RequireDeletionTarget(root, target)); }
    }

    [Fact]
    public void NestedAndMixedAggregatesKeepProgrammingFaultsDistinctAndAllDiagnostics()
    {
        var allowed = new AggregateException(new IOException("io"), new AggregateException(new System.ComponentModel.Win32Exception(5)));
        Assert.True(ScratchFixtureFailure.IsRefusal(allowed));
        foreach (var unexpected in new Exception[] { new ArgumentException("argument"), new NullReferenceException("null"),
            new InvalidOperationException("operation"), new ObjectDisposedException("drain") })
        {
            var failure = Assert.Throws<AggregateException>(() => ScratchFixtureFailure.Cleanup(allowed, () => throw unexpected));
            Assert.False(ScratchFixtureFailure.IsRefusal(failure));
            Assert.Contains(unexpected.Message, failure.ToString(), StringComparison.Ordinal);
            Assert.Contains("io", failure.ToString(), StringComparison.Ordinal);
        }
        Assert.False(ScratchFixtureFailure.IsRefusal(new AggregateException()));
    }

    private sealed class FailedReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("read failed"));
    }

    private sealed class StalledReader : TextReader
    {
        internal CancellationToken Token { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
            return new ValueTask<int>(pending.Task);
        }
    }
}
#endif
