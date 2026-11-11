#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Security.Cryptography;
using OpenBlade.DeploymentBroker;

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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PartialChannelCleanupAttemptsEveryAcquiredResource(int acquired)
    {
        List<int> released = [];
        Action[] clients = Enumerable.Range(0, acquired).Select(index => (Action)(() =>
        {
            released.Add(index);
            if (index == 0) { throw new IOException("client close failed"); }
        })).ToArray();
        var primary = new InvalidDataException("admission failed");
        Exception error = Assert.ThrowsAny<Exception>(() => ScratchFixtureFailure.Cleanup(primary, clients));
        Assert.Equal(Enumerable.Range(0, acquired), released);
        Assert.Contains("admission failed", error.ToString(), StringComparison.Ordinal);
        Assert.True(ScratchFixtureFailure.IsRefusal(error));
        if (acquired > 0) { Assert.Contains("client close failed", error.ToString(), StringComparison.Ordinal); }
        else { Assert.Same(primary, error); }
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
    public void DataFilesRemainOutsideTheCodeDirectoryAndBothCleanUp()
    {
        string root = Directory.CreateTempSubdirectory("OpenBlade.DataSeparation-").FullName;
        try
        {
            string code = Directory.CreateDirectory(Path.Combine(root, "receiver")).FullName;
            string data = Directory.CreateDirectory(Path.Combine(root, "receiver.data")).FullName;
            File.WriteAllText(Path.Combine(code, "receiver.exe"), "data-only image");
            File.WriteAllText(Path.Combine(data, "child.tmp"), "data-only temp file");
            string environment = ScratchNativeReceiverLaunch.CreateEnvironment(code, data);
            Assert.Contains("TEMP=" + data + '\0', environment, StringComparison.Ordinal);
            Assert.Equal(["receiver.exe"], Directory.GetFiles(code).Select(Path.GetFileName));
        }
        finally { Directory.Delete(root, recursive: true); }
        Assert.False(Directory.Exists(root));
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
