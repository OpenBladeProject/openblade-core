using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using OpenBlade.Capture;
using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

public sealed class CpuVoltageSessionTests
{
    [Fact]
    public async Task StartupReadinessWaitsForListenerBeyondOrdinaryConnectionDeadline()
    {
        string name = "OpenBlade.Test.Readiness." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        // Do not create the actual listener until after the ordinary two-second timeout.
        string delayedName = name;
        using var delayedClient = new NamedPipeClientStream(".", delayedName, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task connection = WindowsCpuVoltageServiceProcess.WaitForStartupPipeAsync(delayedClient.ConnectAsync, deadline.Token);
        await Task.Delay(2200, deadline.Token);
        Assert.False(connection.IsCompleted);
        using var delayedServer = new NamedPipeServerStream(delayedName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await Task.WhenAll(connection, delayedServer.WaitForConnectionAsync(deadline.Token));
        Assert.True(delayedClient.IsConnected);
    }

    [Fact]
    public async Task StartupReadinessUsesOneBoundedConnectionAndHonorsExistingCancellation()
    {
        int calls = 0;
        using var deadline = new CancellationTokenSource();
        Task connection = WindowsCpuVoltageServiceProcess.WaitForStartupPipeAsync((milliseconds, token) =>
        {
            calls++; Assert.Equal(15_000, milliseconds); Assert.Equal(deadline.Token, token);
            return Task.Delay(Timeout.Infinite, token);
        }, deadline.Token);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsCpuVoltageServiceProcess.WaitForStartupPipeAsync(
            (_, _) => { calls++; return Task.CompletedTask; }, deadline.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyAffirmativelyUnlaunchedFailureCanResolveAndCloseWithoutReplay(bool launchAttempted)
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid());
        state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => { });
        state.Complete(true, true, null, "green");
        var cycle = Request(CpuVoltageSessionAction.VoltageCycle);
        Assert.True(state.Accept(cycle, _ => { }));
        Assert.False(state.CanResolveBeforeOwner(false));
        state.Complete(false, false, null, "preparation failed");
        Assert.Equal(!launchAttempted, state.CanResolveBeforeOwner(launchAttempted));
        if (launchAttempted)
        {
            Assert.Throws<IOException>(() => state.CompleteBeforeOwnerClose(true));
            state.Accept(Request(CpuVoltageSessionAction.Close), _ => { });
            Assert.True(state.MustRetainHost); Assert.NotEqual("Closed", state.Snapshot.State);
        }
        else
        {
            state.Block("continuity revoked");
            state.CompleteBeforeOwnerClose(false);
            Assert.False(state.MustRetainHost); Assert.False(state.Snapshot.ObservationAdmitted);
            Assert.Equal("Blocked", state.Snapshot.State);
            Assert.False(state.Accept(cycle, _ => throw new Exception("replay")));
            Assert.False(state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => throw new Exception("new authority")));
            state.Accept(Request(CpuVoltageSessionAction.Close), _ => { });
            Assert.Equal("Closed", state.Snapshot.State);
        }
    }

    [Fact]
    public async Task OwnerOutputRetainsBoundedFailureLinesWithoutChangingRunIdentity()
    {
        Guid run = Guid.NewGuid(), observed = Guid.Empty;
        var failures = new List<string>();
        using var output = new StringReader($"ignored\nCpuVoltageOwnerRun={run:N}\nCpuVoltageOwner=ResourcesRetained_NoRetry_NoCleanup\n"
            + "CpuVoltageOwner=Unknown_IOException\nCpuVoltageOwner=" + new string('x', 256) + "\nCpuVoltageOwner=bad\tvalue\n");
        await CpuVoltageSessionCommand.ReadOwnerOutputAsync(output, value => observed = value, failures.Add);
        Assert.Equal(run, observed);
        Assert.Equal(new[] { "CpuVoltageOwner=ResourcesRetained_NoRetry_NoCleanup", "CpuVoltageOwner=Unknown_IOException" }, failures);
    }

    [Fact]
    public void LateOwnerFailureSurvivesLaterBlockMessagesWithoutChangingAdmission()
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid());
        Assert.True(state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => { }));
        const string failure = "CpuVoltageOwner=Unknown_IOException";
        Assert.Equal(state.Snapshot, CpuVoltageSessionCommand.WithOwnerFailure(state.Snapshot, failure));
        state.Complete(false, false, null, "Deadline.");
        state.Block("Session continuity changed.");
        var reply = CpuVoltageSessionCommand.WithOwnerFailure(state.Snapshot, failure);
        Assert.Equal("Session continuity changed. Owner: " + failure, reply.Message);
        Assert.Equal(state.Snapshot, reply with { Message = state.Snapshot.Message });
        Assert.True(state.MustRetainHost);
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.VoltageCycle), _ => throw new Exception("retry")));
    }

    [Fact]
    public async Task OwnerFailureLinesDoNotWeakenOutputOrAnnouncementBounds()
    {
        using var oversized = new StringReader(new string('x', 16385));
        await Assert.ThrowsAsync<IOException>(() => CpuVoltageSessionCommand.ReadOwnerOutputAsync(oversized, _ => { }, _ => { }));
        Guid run = Guid.NewGuid();
        using var duplicate = new StringReader($"CpuVoltageOwnerRun={run:N}\nCpuVoltageOwner=Unknown_IOException\nCpuVoltageOwnerRun={run:N}\n");
        await Assert.ThrowsAsync<IOException>(() => CpuVoltageSessionCommand.ReadOwnerOutputAsync(duplicate, _ => { }, _ => { }));
    }

    [Theory]
    [InlineData("O:BAD:NO_ACCESS_CONTROL")]
    [InlineData("O:BAD:(A;;GW;;;WD)")]
    [InlineData("O:BAD:(A;;GA;;;WD)")]
    [InlineData("O:BAD:(A;;WD;;;WD)")]
    public void ProtectionRejectsNullDaclAndGenericOrSpecificWrites(string sddl)
    {
        Assert.Throws<IOException>(() => WindowsCpuVoltageSessionPlatform.RequireSecurityDescriptor(
            new RawSecurityDescriptor(sddl), (int)(FileSystemRights.Write | FileSystemRights.ChangePermissions), true));
    }
    [Fact]
    public void ExclusiveLeasePreventsAnotherHostUntilResolvedOwnerReleasesIt()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".lock");
        try
        {
            using (var first = WindowsCpuVoltageSessionPlatform.OpenExclusiveLease(path))
            { Assert.Throws<IOException>(() => WindowsCpuVoltageSessionPlatform.OpenExclusiveLease(path)); }
            using var next = WindowsCpuVoltageSessionPlatform.OpenExclusiveLease(path);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void ADeadHostWithoutMatchingResolvedClosureDoesNotAuthorizeNewEnrollment()
    {
        Guid previous = Guid.NewGuid();
        Assert.Throws<IOException>(() => WindowsCpuVoltageSessionPlatform.RequireMatchingClosure(previous,
            JsonSerializer.SerializeToUtf8Bytes(new { session = previous, idleResolved = false })));
        Assert.Throws<IOException>(() => WindowsCpuVoltageSessionPlatform.RequireMatchingClosure(previous,
            JsonSerializer.SerializeToUtf8Bytes(new { session = Guid.NewGuid(), idleResolved = true })));
        WindowsCpuVoltageSessionPlatform.RequireMatchingClosure(previous,
            JsonSerializer.SerializeToUtf8Bytes(new { session = previous, idleResolved = true }));
    }
    [Fact]
    public void ProtectedReadableEnrollmentAllowsOnlyPrivilegedMutation()
    {
        WindowsCpuVoltageSessionPlatform.RequireSecurityDescriptor(new RawSecurityDescriptor("O:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;GR;;;WD)"),
            (int)FileSystemRights.Write, true);
    }
    private static CpuVoltageSessionRequest Request(CpuVoltageSessionAction action) => new(1, Guid.NewGuid(), action);
    [Theory]
    [InlineData("observeonly")]
    [InlineData("0")]
    [InlineData("Set")]
    [InlineData("ObserveOnly ")]
    public void ProtocolRejectsNoncanonicalActions(string action)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($$"""{"version":1,"requestId":"{{Guid.NewGuid()}}","action":"{{action}}"}""");
        Assert.ThrowsAny<Exception>(() => CpuVoltageSessionProtocol.Parse(bytes));
    }
    [Theory]
    [InlineData(",\"path\":\"C:\\\\x.exe\"")]
    [InlineData(",\"action\":\"VoltageCycle\"")]
    public void ProtocolRejectsExtraAndDuplicateFields(string field)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($$"""{"version":1,"requestId":"{{Guid.NewGuid()}}","action":"ObserveOnly"{{field}}}""");
        Assert.ThrowsAny<Exception>(() => CpuVoltageSessionProtocol.Parse(bytes));
    }
    [Fact]
    public async Task FixedMessagesRoundTripAndOversizedFramesRefuseBeforeBody()
    {
        using var stream = new MemoryStream(); var request = Request(CpuVoltageSessionAction.ObserveOnly);
        await CpuVoltageSessionProtocol.WriteAsync(stream, request, 512, default); stream.Position = 0;
        Assert.Equal(request, CpuVoltageSessionProtocol.Parse(await CpuVoltageSessionProtocol.ReadAsync(stream, 512, default)));
        using var oversized = new MemoryStream(BitConverter.GetBytes(513));
        await Assert.ThrowsAsync<InvalidDataException>(() => CpuVoltageSessionProtocol.ReadAsync(oversized, 512, default));
    }
    [Fact]
    public void AcceptanceIsDurableBeforeJobAndDuplicateCannotReplay()
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid()); var request = Request(CpuVoltageSessionAction.ObserveOnly);
        bool persisted = false;
        Assert.True(state.Accept(request, value => { Assert.Equal(request, value); Assert.Null(state.Snapshot.Job); persisted = true; }));
        Assert.True(persisted); Assert.Equal(request.RequestId, state.Snapshot.Job);
        Assert.False(state.Accept(request, _ => throw new Exception("Duplicate persisted")));
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.VoltageCycle), _ => throw new Exception("Concurrent job persisted")));
    }
    [Fact]
    public void PersistenceFailurePreventsLaunchClosesAdmissionButAllowsSafeExit()
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid());
        Assert.Throws<IOException>(() => state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => throw new IOException("disk")));
        Assert.False(state.MustRetainHost); Assert.Equal("Blocked", state.Snapshot.State);
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => throw new Exception("retry")));
        state.Accept(Request(CpuVoltageSessionAction.Close), _ => { }); Assert.Equal("Closed", state.Snapshot.State);
    }
    [Fact]
    public void OnlyOneAdmittedObservationThenOneCycleCanRun()
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid());
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.VoltageCycle), _ => throw new Exception("premature cycle")));
        Assert.True(state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => { }));
        state.Complete(true, true, null, "observed");
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => throw new Exception("repeat observation")));
        Assert.True(state.Accept(Request(CpuVoltageSessionAction.VoltageCycle), _ => { }));
        state.Complete(true, true, null, "restored");
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.VoltageCycle), _ => throw new Exception("repeat cycle")));
        Assert.False(state.MustRetainHost);
        state.Accept(Request(CpuVoltageSessionAction.Close), _ => { }); Assert.Equal("Closed", state.Snapshot.State);
    }
    [Fact]
    public void RecordedRefusalBlocksCycleButIsSafelyCloseable()
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid());
        state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => { });
        state.Complete(true, false, null, "refused and cleaned");
        Assert.False(state.Accept(Request(CpuVoltageSessionAction.VoltageCycle), _ => throw new Exception("cycle after refusal")));
        Assert.False(state.MustRetainHost);
        state.Accept(Request(CpuVoltageSessionAction.Close), _ => { }); Assert.Equal("Closed", state.Snapshot.State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FatalServerFailureRetainsAnyUnresolvedOwner(bool alreadyUnknown)
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid());
        state.Accept(Request(CpuVoltageSessionAction.ObserveOnly), _ => { });
        if (alreadyUnknown) { state.Complete(false, false, null, "unknown"); }
        state.Block("server failed");
        Assert.True(state.MustRetainHost);
        state.Accept(Request(CpuVoltageSessionAction.Close), _ => { }); Assert.NotEqual("Closed", state.Snapshot.State);
        Assert.True(state.MustRetainHost);
    }
    [Fact]
    public void FatalServerFailureWhenIdleDoesNotRequireRetention()
    {
        var state = new CpuVoltageSessionState(Guid.NewGuid()); state.Block("server failed");
        Assert.False(state.MustRetainHost);
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void OwnerArgumentsAreDerivedFromEnrollment(int actionValue, bool observation)
    {
        var action = (CpuVoltageSessionAction)actionValue;
        var options = new CpuVoltageOwnedSmokeCommand.Options(@"C:\private\reviewed", new string('a', 40), new string('B', 64), "1.2.3+voltage-validation.aaaaaaaa");
        var args = WindowsCpuVoltageSessionPlatform.OwnerArguments(options, action);
        Assert.Equal(CpuVoltageOwnedSmokeCommand.Name, args[0]);
        Assert.Equal(options with { ObserveOnly = observation }, CpuVoltageOwnedSmokeCommand.Parse(args[1..]));
        Assert.Throws<ArgumentException>(() => WindowsCpuVoltageSessionPlatform.OwnerArguments(options, CpuVoltageSessionAction.Status));
    }
    [Fact]
    public void HostRequiresSeparateExplicitEnrollmentConfirmation()
    {
        string[] args = ["--payload", @"C:\private\reviewed", "--source", new string('a', 40), "--manifest-sha256", new string('B', 64),
            "--expected-service-version", "1.2.3+voltage-validation.aaaaaaaa", "--confirm-target", CpuVoltageSessionCommand.Confirmation];
        Assert.False(CpuVoltageSessionCommand.ParseHost(args).ObserveOnly);
        args[^1] = CpuVoltageOwnedSmokeCommand.Confirmation;
        Assert.Throws<ArgumentException>(() => CpuVoltageSessionCommand.ParseHost(args));
    }
    [Theory]
    [InlineData(ControlOperationStatus.Stored, true, true)]
    [InlineData(ControlOperationStatus.Stored, false, false)]
    [InlineData(ControlOperationStatus.Blocked, true, false)]
    [InlineData(ControlOperationStatus.Failed, false, false)]
    public void CompletedObservationOnlyAdmitsCycleForStoredWritableTypedState(ControlOperationStatus status, bool writable, bool expected)
    {
        var (enrollment, run, records, now) = Evidence(status, writable);
        Assert.Equal(expected, CpuVoltageSessionCommand.VerifyCompletion(enrollment, CpuVoltageSessionAction.ObserveOnly, run, records,
            now.AddSeconds(-1), now.AddSeconds(1)));
    }
    [Fact]
    public void ExitSuccessCannotSubstituteForMissingCleanupReceiptOrDifferentRun()
    {
        var (enrollment, run, records, now) = Evidence(ControlOperationStatus.Stored, true);
        Assert.Throws<InvalidDataException>(() => CpuVoltageSessionCommand.VerifyCompletion(enrollment, CpuVoltageSessionAction.ObserveOnly,
            Guid.NewGuid(), records, now.AddSeconds(-1), now.AddSeconds(1)));
        records.Remove("control/voltage-owner-completed.json");
        Assert.Throws<KeyNotFoundException>(() => CpuVoltageSessionCommand.VerifyCompletion(enrollment, CpuVoltageSessionAction.ObserveOnly,
            run, records, now.AddSeconds(-1), now.AddSeconds(1)));
    }
    private static (CpuVoltageSessionEnrollment Enrollment, Guid Run, Dictionary<string, JsonElement> Records, DateTimeOffset Now)
        Evidence(ControlOperationStatus operation, bool writable)
    {
        var now = DateTimeOffset.UtcNow; Guid run = Guid.NewGuid();
        var enrollment = new CpuVoltageSessionEnrollment(Guid.NewGuid(), "S-1-5-21-1-2-3-1000", 1, 123, 456,
            @"C:\private\OpenBlade.Capture.exe", new string('a', 40), new string('B', 64), "1.2.3+voltage-validation.aaaaaaaa");
        var unavailable = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable);
        var cpu = new CpuTuningStatus(null, null, unavailable, new(writable ? DeviceFeatureAccess.ReadWrite : DeviceFeatureAccess.ObservedOnly,
            CpuTuningBackendAvailability.Available), CpuTuningPendingBoot.Unknown, false);
        var result = new
        {
            mode = "ObserveOnly",
            outcome = "ObservationRecorded",
            phase = "Observation",
            expectedServiceVersion = enrollment.ServiceVersion,
            applyAttempted = false,
            restoreAttempted = false,
            originalVerified = false,
            baseline = (object?)null,
            forward = (object?)null,
            final = (object?)null,
            responseReceivedAtUtc = now,
            controlResponse = new ControlOperationResult(operation, "actual reason") { CpuTuning = cpu },
        };
        var receipt = new
        {
            Run = run,
            Mode = "ObserveOnly",
            enrollment.Source,
            ManifestSha256 = enrollment.Manifest,
            ExpectedServiceVersion = enrollment.ServiceVersion,
            ServiceTerminal = true,
            OwnedDriverAbsent = true,
            OriginalVerified = false,
        };
        return (enrollment, run, new()
        {
            ["evidence/cpu-voltage-optimizer.json"] = JsonSerializer.SerializeToElement(result, CpuVoltageSessionProtocol.Json),
            ["control/voltage-owner-completed.json"] = JsonSerializer.SerializeToElement(receipt),
        }, now);
    }
}
