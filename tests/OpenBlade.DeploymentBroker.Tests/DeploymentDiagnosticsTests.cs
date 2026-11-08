using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class DeploymentDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OpenBlade.DeploymentBroker.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(BrokerAction.ReadDeploymentStatus, true, false, "deployment-status")]
    [InlineData(BrokerAction.ReadDeploymentStatus, false, false, "diagnostics-unavailable")]
    [InlineData(BrokerAction.ApplyFixedInbox, true, false, "unresolved-deployment")]
    [InlineData(BrokerAction.ApplyCpuVoltageInbox, true, false, "unresolved-deployment")]
    public async Task Pipe_BlockedStartupAllowsOnlyAuthenticatedNormalDiagnosticsWithoutDurableWrites(
        BrokerAction action, bool normal, bool installEnabled, string code)
    {
        string name = "OpenBlade.Diagnostics.Tests." + Guid.NewGuid().ToString("N");
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        var handler = new CountingHandler();
        var context = new FixedContext(normal);
        var server = new BrokerPipeServer(BrokerEnrollmentTests.CreateEnrollment(), context, new ConsoleSession(),
            new BrokerReplayStore(paths), new BrokerSingleFlightGate(), handler,
            options => new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, options),
            () => new(false, "None", null, []), installEnabled);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serving = server.ServeOnceAsync(deadline.Token);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(deadline.Token);
        Guid id = Guid.NewGuid();
        byte[] request = BrokerRequestCodec.SerializeCanonical(new(1, id, action));
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, request.Length);
        await client.WriteAsync(header, deadline.Token);
        await client.WriteAsync(request, deadline.Token);
        await client.FlushAsync(deadline.Token);
        await client.ReadExactlyAsync(header, deadline.Token);
        byte[] reply = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await client.ReadExactlyAsync(reply, deadline.Token);
        await serving;
        using JsonDocument document = JsonDocument.Parse(reply);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        if (action == BrokerAction.ReadDeploymentStatus && normal)
        { Assert.False(DeploymentStatusCodec.Parse(reply, id).Diagnostics.InstallEnabled); }
        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Startup_UnresolvedOrInvalidJournalDisablesInstallsWithoutLosingDiagnostics()
    {
        Assert.False(await Worker.CheckInstallReadinessAsync(() => Task.CompletedTask,
            () => Task.FromResult(false), _ => { }));
        bool reconciled = false;
        Assert.False(await Worker.CheckInstallReadinessAsync(() => throw new InvalidDataException(),
            () => { reconciled = true; return Task.FromResult(true); }, _ => { }));
        Assert.False(reconciled);
        Assert.True(await Worker.CheckInstallReadinessAsync(() => Task.CompletedTask,
            () => Task.FromResult(true), _ => { }));
    }

    [Fact]
    public void Reader_NoCurrentRequestDoesNotEnumerateHistoryOrCreateFiles()
    {
        var snapshot = new BrokerDeploymentDiagnostics(BrokerPaths.CreateForTests(root)).Read(false);
        Assert.Equal("None", snapshot.Selection);
        Assert.Empty(snapshot.Files);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Reader_BindsRecordsToCurrentRequestAndBoundsLogTailWithoutChangingOriginals()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        Guid id = Guid.NewGuid();
        await new BrokerInFlightStore(paths).CreateOnceAsync(id, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(paths.ReportDirectory);
        Directory.CreateDirectory(paths.JournalDirectory);
        string stage = Path.Combine(paths.StagingDirectory, id.ToString("N"));
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(paths.ReportDirectory, id.ToString("N") + ".json"),
            JsonSerializer.Serialize(new { requestId = Guid.NewGuid() }));
        File.WriteAllText(Path.Combine(paths.JournalDirectory, id.ToString("N") + ".jsonl"), "torn");
        string log = Path.Combine(stage, "installer.log");
        byte[] original = Encoding.UTF8.GetBytes(new string('x', 40000) + "exit1610");
        File.WriteAllBytes(log, original);
        File.WriteAllText(Path.Combine(stage, "installer_001_OpenBladeSetup.log"), "Setup failure reason");
        File.WriteAllText(Path.Combine(stage, "unrelated.log"), "not exported");
        byte[] before = File.ReadAllBytes(paths.InFlightPath);
        DeploymentStatusSnapshot snapshot = new BrokerDeploymentDiagnostics(paths).Read(true);
        Assert.False(snapshot.InstallEnabled);
        Assert.Equal(id, snapshot.DeploymentRequestId);
        Assert.Equal("Unavailable", snapshot.Files.Single(f => f.Name == "report").State);
        Assert.Equal("Unavailable", snapshot.Files.Single(f => f.Name == "journal").State);
        Assert.Equal("Missing", snapshot.Files.Single(f => f.Name == "cpuLaunch").State);
        var tail = snapshot.Files.Single(f => f.Name == "installerLog");
        Assert.True(tail.Truncated);
        Assert.Equal(DeploymentStatusCodec.MaximumFileBytes, Convert.FromBase64String(tail.Base64!).Length);
        Assert.EndsWith("exit1610", Encoding.UTF8.GetString(Convert.FromBase64String(tail.Base64!)));
        Assert.Equal("Setup failure reason", Encoding.UTF8.GetString(Convert.FromBase64String(
            snapshot.Files.Single(f => f.Name == "setupLog1").Base64!)));
        Assert.Equal(original, File.ReadAllBytes(log));
        Assert.Equal(before, File.ReadAllBytes(paths.InFlightPath));
    }

    [Theory]
    [InlineData("installer_001_OpenBladeSetup.log", true)]
    [InlineData("installer_999_OpenBladeSetup.log", true)]
    [InlineData("installer_001_OpenBladeSetup.log/other", false)]
    [InlineData("installer_01_OpenBladeSetup.log", false)]
    [InlineData("installer_abc_OpenBladeSetup.log", false)]
    [InlineData("installer_001_AnotherPackage.log", false)]
    public void SetupLogNames_AcceptOnlyOwnedFixedPackagePattern(string name, bool expected) =>
        Assert.Equal(expected, BrokerDeploymentDiagnostics.IsSetupLogName(name));

    [Fact]
    public void Codec_PreservesOrdinaryReplyAndRejectsWrongIdentityOrOversizeDiagnostic()
    {
        Guid id = Guid.NewGuid();
        byte[] ordinary = BrokerResponseCodec.SerializeCanonical(new(1, id, BrokerResponseStatus.Succeeded, "install-succeeded"));
        Assert.DoesNotContain("diagnostics", Encoding.UTF8.GetString(ordinary));
        var response = new DeploymentStatusResponse(1, id, "Succeeded", "deployment-status", new(false, "None", null, []));
        byte[] bytes = DeploymentStatusCodec.Serialize(response);
        Assert.Equal(id, DeploymentStatusCodec.Parse(bytes, id).RequestId);
        Assert.Throws<InvalidDataException>(() => DeploymentStatusCodec.Parse(bytes, Guid.NewGuid()));
        var oversized = new DeploymentStatusSnapshot(false, "None", null,
            [new("installerLog", "Available", true, Convert.ToBase64String(new byte[DeploymentStatusCodec.MaximumFileBytes + 1]))]);
        Assert.Throws<InvalidDataException>(() => DeploymentStatusCodec.Serialize(response with { Diagnostics = oversized }));
    }

    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    private sealed class FixedContext(bool normal) : IBrokerClientContextResolver
    {
        public BrokerClientContext Resolve(NamedPipeServerStream pipe) =>
            new(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), 7, BrokerClientLogonKind.Interactive) { NormalIntegrity = normal };
    }
    private sealed class ConsoleSession : IActiveConsoleSessionSource { public int? GetActiveConsoleSessionId() => 7; }
    private sealed class CountingHandler : IBrokerRequestHandler
    {
        public int Calls;
        public Task<BrokerResponse> HandleAsync(BrokerRequest request, BrokerEnrollment enrollment, CancellationToken token)
        { Calls++; return Task.FromResult(new BrokerResponse(1, request.RequestId, BrokerResponseStatus.Succeeded, "unexpected")); }
    }
}
