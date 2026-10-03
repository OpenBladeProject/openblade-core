using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenBlade.Capture;
using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

public sealed class CpuVoltageOwnedSmokeTests
{
    private static readonly string Source = new('a', 40), Hash = new('A', 64);
    private static readonly string Version = "0.24.0+voltage-validation." + Source[..8];
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-10-02T10:00:00Z");
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    private static string[] Args => ["--payload", @"C:\reviewed", "--source", Source, "--manifest-sha256", Hash,
        "--expected-service-version", Version, "--confirm-target", CpuVoltageOwnedSmokeCommand.Confirmation];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnerCompletionReceiptIsPublishedOnlyAfterVerifiedResourceCleanup(bool observeOnly)
    {
        Guid run = Guid.NewGuid();
        var options = new CpuVoltageOwnedSmokeCommand.Options(@"C:\reviewed", Source, Hash, Version, observeOnly);
        var events = new List<string>(); JsonElement? receipt = null;
        Assert.True(CpuVoltageOwnedSmokeCommand.Complete(new Child { State = CpuPmChildState.Terminal }, () => false,
            () => { }, () => { }, () => 0, () => events.Add("evidence"), () => events.Add("shutdown"),
            () => events.Add("service-terminal"), () => { Assert.Null(receipt); events.Add("driver-absent"); return CpuPmDriverCleanup.Complete; },
            () => throw new InvalidOperationException(), () =>
            {
                receipt = JsonSerializer.SerializeToElement(CpuVoltageOwnedSmokeCommand.CreateCompletionReceipt(run, options));
                events.Add("receipt");
            }));
        Assert.Equal(new[] { "evidence", "shutdown", "service-terminal", "driver-absent", "receipt" }, events);
        var value = receipt!.Value;
        Assert.Equal(run, value.GetProperty("Run").GetGuid());
        Assert.Equal(observeOnly ? "ObserveOnly" : "VoltageCycle", value.GetProperty("Mode").GetString());
        Assert.Equal(Source, value.GetProperty("Source").GetString()); Assert.Equal(Hash, value.GetProperty("ManifestSha256").GetString());
        Assert.Equal(Version, value.GetProperty("ExpectedServiceVersion").GetString());
        Assert.True(value.GetProperty("ServiceTerminal").GetBoolean()); Assert.True(value.GetProperty("OwnedDriverAbsent").GetBoolean());
        Assert.Equal(!observeOnly, value.GetProperty("OriginalVerified").GetBoolean());
    }

    [Theory]
    [InlineData("child")]
    [InlineData("evidence")]
    [InlineData("shutdown")]
    [InlineData("service-terminal")]
    [InlineData("cleanup")]
    public void RetainedOrFailedCompletionCannotPublishOwnerReceipt(string failure)
    {
        bool receipt = false;
        bool Run() => CpuVoltageOwnedSmokeCommand.Complete(new Child { State = failure == "child" ? CpuPmChildState.Unknown : CpuPmChildState.Terminal },
            () => false, () => { }, () => { }, () => 0,
            () => { if (failure == "evidence") { throw new IOException(); } },
            () => { if (failure == "shutdown") { throw new IOException(); } },
            () => { if (failure == "service-terminal") { throw new IOException(); } },
            () => failure == "cleanup" ? CpuPmDriverCleanup.Refused : CpuPmDriverCleanup.Complete,
            () => throw new InvalidOperationException(), () => receipt = true);
        if (failure is "child" or "cleanup") { Assert.False(Run()); }
        else { Assert.Throws<IOException>(() => Run()); }
        Assert.False(receipt);
    }

    [Fact]
    public void OwnerReceiptWriteFailureCannotReturnSuccessfulCompletion()
    {
        Assert.Throws<IOException>(() => CpuVoltageOwnedSmokeCommand.Complete(new Child { State = CpuPmChildState.Terminal },
            () => false, () => { }, () => { }, () => 0, () => { }, () => { }, () => { },
            () => CpuPmDriverCleanup.Complete, () => { }, () => throw new IOException("Receipt publication failed")));
    }

    [Fact]
    public void RevocationDuringReceiptCannotReturnSuccessfulCompletion()
    {
        bool uncertain = false;
        Assert.Throws<IOException>(() => CpuVoltageOwnedSmokeCommand.Complete(new Child { State = CpuPmChildState.Terminal },
            () => uncertain, () => { }, () => { }, () => 0, () => { }, () => { }, () => { },
            () => CpuPmDriverCleanup.Complete, () => { }, () => uncertain = true));
    }

    [Fact]
    public void ObserveOnlyRequiresMatchingTokenAndPropagatesExactChildMode()
    {
        var args = Args; args[9] = CpuVoltageOwnedSmokeCommand.ObserveConfirmation;
        Assert.Throws<ArgumentException>(() => CpuVoltageOwnedSmokeCommand.Parse(args));
        Assert.Throws<ArgumentException>(() => CpuVoltageOwnedSmokeCommand.Parse([.. Args, "--observe-only"]));
        var options = CpuVoltageOwnedSmokeCommand.Parse([.. args, "--observe-only"]);
        Assert.True(options.ObserveOnly);
        Assert.Equal(new[] { "cpu-voltage-optimizer", "--observe-only", "--confirm-target", "RZ09-0528-CPU-VOLTAGE-OBSERVE-ONLY",
            "--expected-service-version", Version, "--output", "evidence" }, CpuVoltageOwnedSmokeCommand.ChildArguments(options, "evidence"));
        Assert.Throws<ArgumentException>(() => CpuVoltageOwnedSmokeCommand.Parse([.. args, "--observe-only", "--observe-only"]));
        Assert.DoesNotContain("--observe-only", CpuVoltageOwnedSmokeCommand.ChildArguments(CpuVoltageOwnedSmokeCommand.Parse(Args), "evidence"));
    }

    [Theory]
    [InlineData(ControlOperationStatus.Stored)]
    [InlineData(ControlOperationStatus.Blocked)]
    [InlineData(ControlOperationStatus.Failed)]
    public void RecordedReadOnlyResponsePermitsOnlyOrderedResourceCleanup(ControlOperationStatus status)
    {
        byte[] result = ObservationEvidence(status);
        var events = new List<string>();
        Assert.True(CpuVoltageOwnedSmokeCommand.Complete(new Child { State = CpuPmChildState.Terminal }, () => false,
            () => { }, () => events.Add("revoke"), () => 0,
            () => { CpuVoltageOwnedSmokeEvidence.VerifyObservation(result, Version, Started, Started.AddMinutes(1)); events.Add("recorded"); },
            () => events.Add("shutdown"), () => events.Add("service-terminal"),
            () => { events.Add("cleanup"); return CpuPmDriverCleanup.Complete; }, () => throw new InvalidOperationException()));
        Assert.Equal(new[] { "revoke", "recorded", "shutdown", "service-terminal", "cleanup" }, events);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("outcome")]
    [InlineData("phase")]
    [InlineData("apply")]
    [InlineData("restore")]
    [InlineData("verified")]
    [InlineData("version")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("response")]
    [InlineData("status")]
    [InlineData("message")]
    [InlineData("baseline")]
    [InlineData("duplicate")]
    public void WrongReadOnlyEvidenceNeverPermitsShutdownOrCleanup(string failure)
    {
        var report = JsonNode.Parse(ObservationEvidence(ControlOperationStatus.Blocked))!;
        switch (failure)
        {
            case "mode": report["mode"] = "Cycle"; break;
            case "outcome": report["outcome"] = "Unknown"; break;
            case "phase": report["phase"] = "Admission"; break;
            case "apply": report["applyAttempted"] = true; break;
            case "restore": report["restoreAttempted"] = true; break;
            case "verified": report["originalVerified"] = true; break;
            case "version": report["expectedServiceVersion"] = "wrong"; break;
            case "stale": report["responseReceivedAtUtc"] = Started.AddSeconds(-1); break;
            case "future": report["responseReceivedAtUtc"] = Started.AddMinutes(2); break;
            case "response": report["controlResponse"] = null; break;
            case "status": report["controlResponse"]!["status"] = "Unknown"; break;
            case "message": report["controlResponse"]!.AsObject().Remove("message"); break;
            case "baseline": report["baseline"] = new JsonObject(); break;
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report);
        if (failure == "duplicate") { bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Insert(1, "\"mode\":\"ObserveOnly\",")); }
        bool shutdown = false, cleanup = false;
        Assert.ThrowsAny<Exception>(() => CpuVoltageOwnedSmokeCommand.Complete(new Child { State = CpuPmChildState.Terminal },
            () => false, () => { }, () => { }, () => 0,
            () => CpuVoltageOwnedSmokeEvidence.VerifyObservation(bytes, Version, Started, Started.AddMinutes(1)),
            () => shutdown = true, () => { }, () => { cleanup = true; return CpuPmDriverCleanup.Complete; }, () => { }));
        Assert.False(shutdown); Assert.False(cleanup);
    }

    [Fact]
    public void CycleCompletionRejectsReadOnlyModeEvenWithRestoredVectors()
    {
        var data = Evidence(0); var result = JsonNode.Parse(data.Result)!; result["mode"] = "ObserveOnly";
        Assert.Throws<InvalidDataException>(() => Verify((data.Baseline, JsonSerializer.SerializeToUtf8Bytes(result))));
    }

    private static byte[] ObservationEvidence(ControlOperationStatus status) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        Mode = "ObserveOnly",
        Outcome = "ObservationRecorded",
        Phase = "Observation",
        ExpectedServiceVersion = Version,
        ApplyAttempted = false,
        RestoreAttempted = false,
        OriginalVerified = false,
        Baseline = (CpuLiveState?)null,
        Forward = (CpuLiveState?)null,
        Final = (CpuLiveState?)null,
        ResponseReceivedAtUtc = Started.AddSeconds(1),
        ControlResponse = new ControlOperationResult(status, "actual CPU diagnostic response"),
    }, Json);
    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    public void ActualQuickExitRequiresZeroAndFullRestorationBeforeShutdownAndCleanup(int exit, bool blockedResult)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) { return; }
        string image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        using var files = new WindowsCpuPmRetainedLaunchProcessTests.NativeImageFiles(image);
        var pin = files.PinFile(image, CpuPmLaunchSchema.MaximumFileBytes, true);
        using var actual = Process.Start(new ProcessStartInfo(image, $"/d /c exit {exit}") { UseShellExecute = false, CreateNoWindow = true })!;
        Assert.True(actual.WaitForExit(10000)); Assert.Equal(exit, actual.ExitCode);
        var child = CpuPmRetainedLaunchProcess.RetainForCompletion(WindowsCpuPmProcessProbe.FromLaunchedHandle(actual.SafeHandle), files, image, pin.Sha256, pin);
        var data = Evidence(-1);
        if (blockedResult)
        {
            var result = JsonNode.Parse(data.Result)!; result["outcome"] = "Blocked"; result["originalVerified"] = false;
            data = (data.Baseline, JsonSerializer.SerializeToUtf8Bytes(result));
        }
        var events = new List<string>();
        bool Finish() => CpuVoltageOwnedSmokeCommand.Complete(child, () => false, files.AssertAllPins, () => events.Add("revoke"),
            () => actual.ExitCode, () => { Verify(data); events.Add("restoration"); }, () => events.Add("shutdown"),
            () => events.Add("service-terminal"), () => { events.Add("cleanup"); return CpuPmDriverCleanup.Complete; },
            () => throw new InvalidOperationException("Quick child must already be terminal."));
        if (exit == 0 && !blockedResult)
        {
            Assert.True(Finish());
            Assert.Equal(new[] { "revoke", "restoration", "shutdown", "service-terminal", "cleanup" }, events);
        }
        else
        {
            Assert.ThrowsAny<Exception>(() => Finish()); Assert.Equal(new[] { "revoke" }, events);
            Assert.Equal(child.Identity, child.ReadFreshIdentity()); files.AssertAllPins();
        }
        // Test-owned harmless process only; failed production completion parks.
        child.ReleaseAfterTerminal();
    }
    [Fact]
    public void HelpIsInertAndExactApprovalIsRequired()
    {
        using var output = new StringWriter(); Assert.Equal(0, CpuVoltageOwnedSmokeCommand.Run(["--help"], output));
        Assert.Contains("600-second", output.ToString()); Assert.Contains("normal shutdown cleanup", output.ToString());
        Assert.Equal(Version, CpuVoltageOwnedSmokeCommand.Parse(Args).ServiceVersion);
        foreach (int index in new[] { 1, 3, 5, 7, 9 })
        { var args = Args; args[index] = "invalid"; Assert.ThrowsAny<ArgumentException>(() => CpuVoltageOwnedSmokeCommand.Parse(args)); }
        var duplicate = Args; duplicate[2] = "--payload"; Assert.Throws<ArgumentException>(() => CpuVoltageOwnedSmokeCommand.Parse(duplicate));
        Assert.Equal(2, CpuVoltageOwnedSmokeCommand.Run([], output));
    }
    [Fact]
    public void ShortProfileVersionMustMatchFullReviewedSourcePrefix()
    {
        var maximum = Args; maximum[7] = "65535.65534.65534.65534+voltage-validation." + Source[..8];
        Assert.Equal(Source, CpuVoltageOwnedSmokeCommand.Parse(maximum).Source);
        Assert.True(CpuVoltageOwnedSmokeCommand.SourceVersion(maximum[7], Source));
        foreach (string version in new[] { "0.24.0+voltage-validation.bbbbbbbb", "0.24.0+voltage-validation." + Source,
            "0.24.0+voltage-validation." + Source[..7], "0.24.0+arbitrary." + Source[..8] })
        {
            var args = Args; args[7] = version;
            Assert.Throws<ArgumentException>(() => CpuVoltageOwnedSmokeCommand.Parse(args));
            Assert.False(CpuVoltageOwnedSmokeCommand.SourceVersion(version, Source));
        }
        var abbreviatedSource = Args; abbreviatedSource[3] = Source[..8];
        Assert.Throws<ArgumentException>(() => CpuVoltageOwnedSmokeCommand.Parse(abbreviatedSource));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BothExactCyclesVerifyThenShutdownThenTerminalThenCleanup(int original)
    {
        var evidence = Evidence(original); var child = new Child(); var events = new List<string>(); int polls = 0;
        bool terminal = false;
        Assert.True(CpuVoltageOwnedSmokeCommand.Complete(child, () => false, () => { }, () => events.Add("revoke"),
            () => 0,
            () => { Verify(evidence); events.Add("verified"); }, () => events.Add("shutdown"),
            () => { terminal = true; events.Add("service-terminal"); },
            () => { Assert.True(terminal); events.Add("cleanup"); return CpuPmDriverCleanup.Complete; },
            () => { polls++; child.State = CpuPmChildState.Terminal; }));
        Assert.Equal(1, polls); Assert.Equal(new[] { "revoke", "verified", "shutdown", "service-terminal", "cleanup" }, events);
    }
    [Theory]
    [InlineData("outcome")]
    [InlineData("restored")]
    [InlineData("forward")]
    [InlineData("original")]
    [InlineData("missing-core")]
    [InlineData("wrong-core")]
    [InlineData("version")]
    [InlineData("freshness")]
    [InlineData("ppt")]
    [InlineData("ownership")]
    public void UnverifiedResultPreventsShutdownAndCleanup(string failure)
    {
        var data = Evidence(0); var b = JsonNode.Parse(data.Baseline)!; var r = JsonNode.Parse(data.Result)!;
        switch (failure)
        {
            case "outcome": r["outcome"] = "Unknown"; break;
            case "restored": r["originalVerified"] = false; break;
            case "forward": r["forward"]!["coreValues"]![0]!["value"] = -2; break;
            case "original": r["final"]!["coreValues"]![0]!["value"] = -1; break;
            case "missing-core": r["final"]!["coreValues"]!.AsArray().RemoveAt(0); break;
            case "wrong-core": r["final"]!["coreValues"]![0]!["coreIndex"] = 1; break;
            case "version": r["expectedServiceVersion"] = "0.24.0+" + Source; break;
            case "freshness": b["capturedAtUtc"] = Started.AddSeconds(-1); break;
            case "ppt": b["requestedSettings"]!["powerLimitWatts"] = 45; break;
            case "ownership": b["cpuTuning"]!["restorePending"] = true; break;
        }
        var changed = (JsonSerializer.SerializeToUtf8Bytes(b), JsonSerializer.SerializeToUtf8Bytes(r));
        bool shutdown = false, cleanup = false;
        Assert.ThrowsAny<Exception>(() => CpuVoltageOwnedSmokeCommand.Complete(new Child { State = CpuPmChildState.Terminal },
            () => false, () => { }, () => { }, () => 0, () => Verify(changed), () => shutdown = true, () => { },
            () => { cleanup = true; return CpuPmDriverCleanup.Complete; }, () => { }));
        Assert.False(shutdown); Assert.False(cleanup);
    }
    [Theory]
    [InlineData("child-unknown")]
    [InlineData("child-identity")]
    [InlineData("deadline")]
    [InlineData("shutdown")]
    [InlineData("service-terminal")]
    [InlineData("pins-after-result")]
    public void EachUncertainBoundaryRetainsResources(string failure)
    {
        var child = new Child { State = CpuPmChildState.Terminal, IdentityChanged = failure == "child-identity" };
        if (failure == "child-unknown") { child.State = CpuPmChildState.Unknown; }
        int verified = 0, shutdown = 0, cleanup = 0;
        bool Run() => CpuVoltageOwnedSmokeCommand.Complete(child, () => failure == "deadline", () =>
            { if (failure == "pins-after-result" && verified != 0) { throw new IOException(); } }, () => { },
            () => 0,
            () => verified++, () => { shutdown++; if (failure == "shutdown") { throw new IOException(); } },
            () => { if (failure == "service-terminal") { throw new IOException(); } },
            () => { cleanup++; return CpuPmDriverCleanup.Complete; }, () => throw new IOException());
        if (failure is "shutdown" or "service-terminal" or "pins-after-result") { Assert.Throws<IOException>(() => Run()); }
        else { Assert.False(Run()); }
        Assert.Equal(0, cleanup);
        if (failure is not ("shutdown" or "service-terminal")) { Assert.Equal(0, shutdown); }
    }
    [Fact]
    public void RetainedServiceProofRejectsPidReuseReplacementPinsAndUnknownWait()
    {
        var probe = new Probe(); var pins = new Pins(); int[] inventory = [7];
        var service = new WindowsCpuVoltageServiceProcess(probe, probe.Value, pins, Version, () => inventory);
        service.AssertState(CpuPmChildState.Running);
        probe.Value = probe.Value with { Created = 99 }; Assert.Throws<IOException>(() => service.AssertState(CpuPmChildState.Running));
        probe.Value = probe.Value with { Created = 8, State = CpuPmChildState.Unknown };
        Assert.Equal(CpuPmChildState.Unknown, service.ReadState());
        probe.Value = probe.Value with { State = CpuPmChildState.Terminal, Image = null }; inventory = [];
        service.AssertState(CpuPmChildState.Terminal);
        inventory = [9]; Assert.Throws<IOException>(() => service.AssertState(CpuPmChildState.Terminal));
        inventory = []; pins.Broken = true; Assert.Throws<IOException>(() => service.AssertState(CpuPmChildState.Terminal));
        Assert.False(probe.Disposed);
    }
    [Fact]
    public void FixedVoltageBudgetPreservesOrdinaryPmExpiryAndContinuityGuards()
    {
        var pm = new CpuPmParentContinuityTests.FakePlatform(); var voltage = new CpuPmParentContinuityTests.FakePlatform();
        var old = new CpuPmParentContinuity(pm); var co = new CpuPmParentContinuity(voltage, voltageValidation: true);
        Assert.True(old.Arm()); Assert.True(co.Arm());
        pm.Value = pm.Value with { TickMilliseconds = pm.Value.TickMilliseconds + 120000, Unbiased100Nanoseconds = pm.Value.Unbiased100Nanoseconds + 1200000000 };
        voltage.Value = voltage.Value with { TickMilliseconds = voltage.Value.TickMilliseconds + 120000, Unbiased100Nanoseconds = voltage.Value.Unbiased100Nanoseconds + 1200000000 };
        Assert.False(old.ReadFresh()); Assert.True(co.ReadFresh());
        voltage.Value = voltage.Value with { TickMilliseconds = voltage.Value.TickMilliseconds + 480000, Unbiased100Nanoseconds = voltage.Value.Unbiased100Nanoseconds + 4800000000 };
        Assert.False(co.ReadFresh()); Assert.False(co.ReadFresh());
        Assert.Equal(TimeSpan.FromSeconds(600), CpuVoltageOwnedSmokeCommand.Budget);
    }
    [Fact]
    public void StrictEvidenceRejectsDuplicateFieldsAndArbitraryVersionMetadata()
    {
        var evidence = Evidence(-1);
        var duplicate = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(evidence.Result).Insert(1, "\"outcome\":\"Passed\","));
        Assert.ThrowsAny<Exception>(() => Verify((evidence.Baseline, duplicate)));
        Assert.True(CpuVoltageOwnedSmokeCommand.SourceVersion(Version, Source));
        Assert.True(CpuVoltageOwnedSmokeCommand.SourceVersion("0.24.0+" + Source, Source));
        Assert.False(CpuVoltageOwnedSmokeCommand.SourceVersion("arbitrary" + Source, Source));
        Assert.False(CpuVoltageOwnedSmokeCommand.SourceVersion("0.24.0+other." + Source, Source));
    }
    [Fact]
    public void ManifestReuseKeepsPmRequiredFilesAndExactSourceHash()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            source = Source,
            files = new[] { new { name = "firmware-smoke/OpenBlade.FirmwareSmoke.exe", length = 5, sha256 = Hash } }
        });
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        Assert.Single(CpuPmLaunchSchema.ParseManifest(bytes, Source, hash, ["firmware-smoke/OpenBlade.FirmwareSmoke.exe"]));
        Assert.ThrowsAny<Exception>(() => CpuPmLaunchSchema.ParseManifest(bytes, new CpuPmReviewedLaunchBootstrap(Source, hash)));
        Assert.ThrowsAny<Exception>(() => CpuPmLaunchSchema.ParseManifest(bytes, Source, Hash, ["firmware-smoke/OpenBlade.FirmwareSmoke.exe"]));
        Assert.ThrowsAny<Exception>(() => CpuPmLaunchSchema.ParseManifest(bytes, new string('b', 40), hash, ["firmware-smoke/OpenBlade.FirmwareSmoke.exe"]));
    }
    [Theory]
    [InlineData(257)]
    [InlineData(994)]
    [InlineData(1536)]
    public void CompleteVoltageClosuresFitWithoutWideningPmManifestLimits(int count)
    {
        byte[] bytes = CapacityManifest(count, 1024);
        Assert.Equal(count, ParseCapacity(bytes, true).Count);
        Assert.ThrowsAny<Exception>(() => ParseCapacity(bytes, false));
        Assert.ThrowsAny<Exception>(() => CpuPmLaunchSchema.ParseManifest(bytes,
            new CpuPmReviewedLaunchBootstrap(Source, Digest(bytes))));
    }
    [Fact]
    public void VoltageCapacityRetainsExplicitByteFileAndTotalBounds()
    {
        Assert.ThrowsAny<Exception>(() => ParseCapacity(CapacityManifest(1537, 1), true));
        byte[] normal = CapacityManifest(2, 1);
        byte[] boundary = new byte[CpuPmLaunchSchema.VoltageManifestLimit];
        Array.Fill(boundary, (byte)' '); normal.CopyTo(boundary, 0);
        Assert.Equal(2, ParseCapacity(boundary, true).Count);
        Assert.ThrowsAny<Exception>(() => ParseCapacity(boundary, false));
        Assert.ThrowsAny<Exception>(() => ParseCapacity([.. boundary, (byte)' '], true));
        Assert.Equal(8, ParseCapacity(CapacityManifest(8, CpuPmLaunchSchema.MaximumFileBytes), true).Count);
        Assert.ThrowsAny<Exception>(() => ParseCapacity(CapacityManifest(9, CpuPmLaunchSchema.MaximumFileBytes), true));
        Assert.Equal(4, ParseCapacity(CapacityManifest(4, CpuPmLaunchSchema.MaximumFileBytes), false).Count);
        Assert.ThrowsAny<Exception>(() => ParseCapacity(CapacityManifest(5, CpuPmLaunchSchema.MaximumFileBytes), false));
        Assert.ThrowsAny<Exception>(() => ParseCapacity(CapacityManifest(2, CpuPmLaunchSchema.MaximumFileBytes + 1), true));
        Assert.ThrowsAny<Exception>(() => ParseCapacity(CapacityManifest(2, CpuPmLaunchSchema.MaximumFileBytes + 1), false));
    }
    [Fact]
    public void VoltageInventoryPinsEveryCompleteClosureEntryAndStillRejectsExtras()
    {
        var manifest = ParseCapacity(CapacityManifest(994, 1024), true);
        var files = new ClosureFiles(manifest);
        CpuPmAuthenticatedChildRun.ValidatePayload(files, @"C:\reviewed", manifest, CancellationToken.None, voltageValidation: true);
        Assert.Equal(994, files.FilePins);
        Assert.ThrowsAny<Exception>(() => CpuPmAuthenticatedChildRun.ValidatePayload(new ClosureFiles(manifest),
            @"C:\reviewed", manifest, CancellationToken.None));
        files.Extra = true;
        Assert.ThrowsAny<Exception>(() => CpuPmAuthenticatedChildRun.ValidatePayload(files,
            @"C:\reviewed", manifest, CancellationToken.None, voltageValidation: true));
    }
    private static byte[] CapacityManifest(int count, long length) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        source = Source,
        files = Enumerable.Range(0, count).Select(index => new
        {
            name = index switch
            { 0 => "OpenBlade.Capture.exe", 1 => "OpenBlade.PmSupervisor.exe", _ => $"dependency-{index:D4}.dll" },
            length,
            sha256 = Hash
        })
    });
    private static string Digest(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    private static IReadOnlyList<CpuPmLaunchFile> ParseCapacity(byte[] bytes, bool voltage) =>
        CpuPmLaunchSchema.ParseManifest(bytes, Source, Digest(bytes), ["OpenBlade.Capture.exe", "OpenBlade.PmSupervisor.exe"], voltage);
    private sealed class ClosureFiles(IReadOnlyList<CpuPmLaunchFile> manifest) : ICpuPmLaunchFiles
    {
        internal int FilePins;
        internal bool Extra;
        public void PinDirectory(string path, bool privateAcl, bool protectedAcl) { }
        public ICpuPmLaunchFilePin PinFile(string path, long maximumBytes, bool privateAcl)
        {
            FilePins++; return new FilePin(manifest.Single(file => file.Name == Path.GetFileName(path)));
        }
        public IEnumerable<CpuPmLaunchEntry> Enumerate(string path) =>
            manifest.Select(file => new CpuPmLaunchEntry(file.Name, false)).Append(new("manifest.json", false))
                .Concat(Extra ? [new CpuPmLaunchEntry("unexpected.dll", false)] : []);
        public void AssertAllPins() { }
        public void Dispose() => throw new NotSupportedException();
        private sealed class FilePin(CpuPmLaunchFile file) : ICpuPmLaunchFilePin
        {
            public long Length => file.Length;
            public string Sha256 => file.Sha256;
            public byte[] ReadBounded(int maximumBytes) => throw new NotSupportedException();
        }
    }
    private static void Verify((byte[] Baseline, byte[] Result) value) =>
        CpuVoltageOwnedSmokeEvidence.Verify(value.Baseline, value.Result, Version, Started, Started.AddMinutes(1));
    private static (byte[] Baseline, byte[] Result) Evidence(int original)
    {
        CpuLiveState Vector(int value) => new(null, new[] { 0, 2, 4, 6, 8, 9, 10, 11, 12, 13, 14, 15 }.Select(core => new CpuCoreCurveOptimizerValue(core, value)).ToArray());
        var originalState = Vector(original);
        var status = new CpuTuningStatus(null, originalState,
            new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Available),
            new(DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available), CpuTuningPendingBoot.None, false)
        { LiveRecovery = new(CpuTuningRecoveryState.None, CpuTuningFeatures.None), FirmwareRecovery = new(CpuTuningRecoveryState.None, CpuTuningFeatures.None) };
        return (JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            CoreMask = 0xFF55,
            CapturedAtUtc = Started.AddSeconds(1),
            ExpectedServiceVersion = Version,
            Status = new { ServiceVersion = Version },
            CpuTuning = status,
            RequestedSettings = new CpuTuningSettings(null, new(original == 0, -1))
        }, Json),
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                Outcome = "Passed",
                Phase = "OriginalReadback",
                ApplyAttempted = true,
                RestoreAttempted = true,
                OriginalVerified = true,
                ExpectedServiceVersion = Version,
                Baseline = originalState,
                Forward = Vector(original == 0 ? -1 : 0),
                Final = originalState
            }, Json));
    }
    private sealed class Child : ICpuPmRetainedChild
    {
        internal CpuPmChildState State = CpuPmChildState.Running;
        internal bool IdentityChanged;
        public CpuPmAuthorityPeer Identity { get; } = new(4, 5, @"C:\reviewed\child.exe", Hash);
        public CpuPmAuthorityPeer? ReadFreshIdentity() => IdentityChanged ? null : Identity;
        public CpuPmChildState ReadState() => State;
    }
    private sealed class Probe : ICpuPmRetainedProcessProbe
    {
        internal CpuPmProcessObservation Value = new(7, 8, CpuPmChildState.Running, WindowsCpuVoltageServiceProcess.Image);
        internal bool Disposed;
        public CpuPmProcessObservation Read(bool includeLiveImage) => Value;
        public void Dispose() => Disposed = true;
    }
    private sealed class Pins : ICpuPmLaunchFiles
    {
        internal bool Broken;
        public void AssertAllPins() { if (Broken) { throw new IOException(); } }
        public void PinDirectory(string path, bool privateAcl, bool protectedAcl) => throw new NotSupportedException();
        public ICpuPmLaunchFilePin PinFile(string path, long maximumBytes, bool privateAcl) => throw new NotSupportedException();
        public IEnumerable<CpuPmLaunchEntry> Enumerate(string path) => throw new NotSupportedException();
        public void Dispose() => throw new NotSupportedException();
    }
}
