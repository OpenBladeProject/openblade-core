using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090581LightingCadenceValidationCommandTests
{
    private static readonly ProductionLightingBuildIdentity BuildIdentity = new(
        "0.1.0+403aa01b11e969a6351595154569ef56f8878efa",
        "403aa01b11e969a6351595154569ef56f8878efa",
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");

    [Fact]
    public async Task RunAsync_QualifiesAllStagesAndWritesSanitizedArtifacts()
    {
        var port = new FakePort();
        var artifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool markerRunnerCalled = false;
        bool tidalRunnerCalled = false;
        Rz090581LightingCadenceValidationCommand command = Create(
            port,
            "YES\r\nYES\r\nYES\r\nYES\r\nYES\r\nYES\r\n",
            (rate, _, _, _, _) =>
            {
                markerRunnerCalled = true;
                return Task.FromResult(QualifiedStage(rate));
            },
            (path, content, _) =>
            {
                artifacts[path] = content;
                return Task.CompletedTask;
            },
            runTidalStage: async (rate, _, _, pushFrame, cancellationToken) =>
            {
                tidalRunnerCalled = true;
                await pushFrame(
                    LightingCadenceProbeRunner.CreateFrame(
                        LightingCadencePattern.Tidal,
                        0,
                        TimeSpan.Zero),
                    cancellationToken);
                return QualifiedStage(rate);
            });

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, artifacts.Count);
        Assert.Equal(
            [
                LightingEffect.Static,
                LightingEffect.AmbientAwareness,
                LightingEffect.Static,
                LightingEffect.AmbientAwareness,
                LightingEffect.Static,
                LightingEffect.AmbientAwareness,
                LightingEffect.Static,
                LightingEffect.Static,
            ],
            port.Settings.Select(settings => settings.Effect));
        string json = artifacts.Single(pair => pair.Key.EndsWith(".json")).Value;
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(6, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("tidal", root.GetProperty("pattern").GetString());
        Assert.True(root.GetProperty("transportQualified").GetBoolean());
        Assert.True(root.GetProperty("opticalValidationRequired").GetBoolean());
        Assert.False(root.GetProperty("lightingRecoveryRequired").GetBoolean());
        Assert.True(root.GetProperty("cleanupRequired").GetBoolean());
        Assert.True(root.GetProperty("cleanupCompleted").GetBoolean());
        Assert.False(root.GetProperty("cleanupPending").GetBoolean());
        Assert.Equal(
            "knownStaticGreenBaseline",
            root.GetProperty("restoredStateKind").GetString());
        Assert.Equal(
            "knownStaticGreenBaselineConfirmed",
            root.GetProperty("restorationOutcome").GetString());
        Assert.False(root.GetProperty("unknownPreTestStateRestored").GetBoolean());
        Assert.Equal(3, root.GetProperty("stages").GetArrayLength());
        JsonElement processorUsage = root.GetProperty("stages")[0]
            .GetProperty("transport")
            .GetProperty("processorUsage");
        Assert.Equal(500, processorUsage
            .GetProperty("processCpuTimeMilliseconds").GetDouble());
        Assert.Equal(5000, processorUsage
            .GetProperty("measurementWallTimeMilliseconds").GetDouble());
        Assert.Equal(20, processorUsage
            .GetProperty("logicalProcessorCount").GetInt32());
        Assert.Equal(10, processorUsage
            .GetProperty("singleLogicalProcessorPercent").GetDouble());
        Assert.Equal(0.5, processorUsage
            .GetProperty("wholeMachineCapacityPercent").GetDouble());
        JsonElement writePhases = root.GetProperty("stages")[0]
            .GetProperty("transport")
            .GetProperty("writePhases");
        Assert.Equal(100, writePhases.GetProperty("capturedSamples").GetInt32());
        Assert.Equal(0, writePhases.GetProperty("alignmentFailures").GetInt32());
        Assert.Equal(0.4, writePhases
            .GetProperty("controllerGuard")
            .GetProperty("p99Milliseconds")
            .GetDouble());
        JsonElement transport = root.GetProperty("stages")[0]
            .GetProperty("transport");
        Assert.True(transport
            .GetProperty("qualificationExcludesControllerGuard")
            .GetBoolean());
        Assert.Equal(0.6, transport
            .GetProperty("qualificationWriteDuration")
            .GetProperty("p99Milliseconds")
            .GetDouble());
        string markdown = artifacts.Single(pair => pair.Key.EndsWith(".md")).Value;
        Assert.Contains("CPU (1 LP=100%)", markdown, StringComparison.Ordinal);
        Assert.Contains("p99 qualified", markdown, StringComparison.Ordinal);
        Assert.Contains("p99 guard", markdown, StringComparison.Ordinal);
        Assert.Contains("Timing alignment failures", markdown, StringComparison.Ordinal);
        Assert.Contains("10.00%", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("serial", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(markerRunnerCalled);
        Assert.True(tidalRunnerCalled);
        Assert.Equal(3, port.PushedFrames);
    }

    [Fact]
    public async Task RunAsync_RejectsUnknownPatternBeforeOpeningHardware()
    {
        bool opened = false;
        var command = new Rz090581LightingCadenceValidationCommand(
            _ =>
            {
                opened = true;
                return Task.FromResult(
                    new HidSessionResult<IProductionLightingPort>(new FakePort(), null));
            },
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(64),
                    null)),
            ExactIdentity,
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            new StringReader(string.Empty),
            new StringWriter(),
            BuildIdentity,
            writeArtifact: (_, _, _) => Task.CompletedTask,
            registerCancellation: _ => NoopDisposable.Instance,
            checkEnvironment: () => null);
        string[] arguments = ValidArguments();
        arguments[5] = "unknown";

        int exitCode = await command.RunAsync(arguments, CancellationToken.None);

        Assert.Equal(Rz090581LightingCadenceValidationCommand.UsageExitCode, exitCode);
        Assert.False(opened);
    }

    [Fact]
    public async Task RunAsync_RejectsNoncanonicalRatesBeforeOpeningHardware()
    {
        bool opened = false;
        var command = new Rz090581LightingCadenceValidationCommand(
            _ =>
            {
                opened = true;
                return Task.FromResult(
                    new HidSessionResult<IProductionLightingPort>(new FakePort(), null));
            },
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(64),
                    null)),
            ExactIdentity,
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            new StringReader(string.Empty),
            new StringWriter(),
            BuildIdentity,
            writeArtifact: (_, _, _) => Task.CompletedTask,
            registerCancellation: _ => NoopDisposable.Instance,
            checkEnvironment: () => null);
        string[] arguments = ValidArguments();
        arguments[3] = "30,60";

        int exitCode = await command.RunAsync(arguments, CancellationToken.None);

        Assert.Equal(Rz090581LightingCadenceValidationCommand.UsageExitCode, exitCode);
        Assert.False(opened);
    }

    [Fact]
    public async Task RunAsync_DoesNotRestoreLightingAfterIndeterminateWrite()
    {
        var port = new FakePort
        {
            PushFailure = new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "Injected indeterminate write."),
        };
        string? json = null;
        Rz090581LightingCadenceValidationCommand command = Create(
            port,
            "YES\r\nYES\r\n",
            async (rate, _, _, pushFrame, cancellationToken) =>
            {
                try
                {
                    await pushFrame(
                        LightingCadenceProbeRunner.CreateFrame(0, TimeSpan.Zero),
                        cancellationToken);
                }
                catch (BladeWriteException exception)
                {
                    return FailedStage(rate, exception.SubmissionState);
                }
                throw new InvalidOperationException("The injected write should fail.");
            },
            (path, content, _) =>
            {
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    json = content;
                }
                return Task.CompletedTask;
            });

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.Equal(
            [LightingEffect.Static, LightingEffect.AmbientAwareness],
            port.Settings.Select(settings => settings.Effect));
        Assert.NotNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement
            .GetProperty("lightingRecoveryRequired").GetBoolean());
        Assert.Equal(
            "indeterminateMatrixWrite",
            document.RootElement.GetProperty("lightingRecoveryReason").GetString());
        Assert.False(document.RootElement
            .GetProperty("restorationAttempted").GetBoolean());
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("restoredStateKind").ValueKind);
        Assert.Equal(
            "skippedForUnprovenLightingState",
            document.RootElement.GetProperty("restorationOutcome").GetString());
        Assert.Equal(1, port.PushedFrames);
    }

    [Fact]
    public async Task RunAsync_RejectsMismatchedBiosBeforeOpeningHardware()
    {
        bool opened = false;
        var command = new Rz090581LightingCadenceValidationCommand(
            _ =>
            {
                opened = true;
                return Task.FromResult(
                    new HidSessionResult<IProductionLightingPort>(new FakePort(), null));
            },
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(64),
                    null)),
            ExactIdentity,
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            new StringReader(string.Empty),
            new StringWriter(),
            BuildIdentity,
            writeArtifact: (_, _, _) => Task.CompletedTask,
            registerCancellation: _ => NoopDisposable.Instance,
            checkEnvironment: () => null);
        string[] arguments = ValidArguments();
        arguments[1] =
            Rz090581LightingCadenceValidationCommand.ExactTargetConfirmation("4.00");

        int exitCode = await command.RunAsync(arguments, CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.False(opened);
    }

    [Fact]
    public async Task RunAsync_HandlesIdentityReadFailureBeforeOpeningHardware()
    {
        bool opened = false;
        var command = new Rz090581LightingCadenceValidationCommand(
            _ =>
            {
                opened = true;
                return Task.FromResult(
                    new HidSessionResult<IProductionLightingPort>(new FakePort(), null));
            },
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(64),
                    null)),
            () => throw new IOException("Injected identity read failure."),
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            new StringReader(string.Empty),
            new StringWriter(),
            BuildIdentity,
            writeArtifact: (_, _, _) => Task.CompletedTask,
            registerCancellation: _ => NoopDisposable.Instance,
            checkEnvironment: () => null);

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.False(opened);
    }

    [Fact]
    public async Task RunAsync_RejectsUnsafeEnvironmentBeforeOpeningHardware()
    {
        bool opened = false;
        var command = new Rz090581LightingCadenceValidationCommand(
            _ =>
            {
                opened = true;
                return Task.FromResult(
                    new HidSessionResult<IProductionLightingPort>(new FakePort(), null));
            },
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(64),
                    null)),
            ExactIdentity,
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            new StringReader(string.Empty),
            new StringWriter(),
            BuildIdentity,
            writeArtifact: (_, _, _) => Task.CompletedTask,
            registerCancellation: _ => NoopDisposable.Instance,
            checkEnvironment: () => "Service is running.");

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.False(opened);
    }

    [Fact]
    public async Task RunAsync_RejectsWrongSkuBeforeOpeningHardware()
    {
        bool opened = false;
        var command = new Rz090581LightingCadenceValidationCommand(
            _ =>
            {
                opened = true;
                return Task.FromResult(
                    new HidSessionResult<IProductionLightingPort>(new FakePort(), null));
            },
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(64),
                    null)),
            () => new HostIdentityReadResult(
                new HostIdentityEvidence(
                    "Razer",
                    "Blade 16 - RZ09-0581",
                    "different-sku",
                    "3.01"),
                null,
                HostFirmwareCompatibility.Tested),
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            new StringReader(string.Empty),
            new StringWriter(),
            BuildIdentity,
            writeArtifact: (_, _, _) => Task.CompletedTask,
            registerCancellation: _ => NoopDisposable.Instance,
            checkEnvironment: () => null);

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.False(opened);
    }

    [Fact]
    public async Task RunAsync_RejectsInvalidBuildIdentityBeforeOpeningHardware()
    {
        bool opened = false;
        var command = new Rz090581LightingCadenceValidationCommand(
            _ =>
            {
                opened = true;
                return Task.FromResult(
                    new HidSessionResult<IProductionLightingPort>(new FakePort(), null));
            },
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(64),
                    null)),
            ExactIdentity,
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            new StringReader(string.Empty),
            new StringWriter(),
            BuildIdentity with { CommitHash = "invalid" },
            writeArtifact: (_, _, _) => Task.CompletedTask,
            registerCancellation: _ => NoopDisposable.Instance,
            checkEnvironment: () => null);

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.False(opened);
    }

    [Fact]
    public async Task RunAsync_RestoresBaselineAfterNonWriteFailure()
    {
        var port = new FakePort();
        string? json = null;
        Rz090581LightingCadenceValidationCommand command = Create(
            port,
            "YES\r\nYES\r\nYES\r\nYES\r\n",
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            (path, content, _) =>
            {
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    json = content;
                }
                return Task.CompletedTask;
            },
            (_, _) => throw new IOException("Injected cooldown failure."));

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.Equal(LightingEffect.Static, port.Settings[^1].Effect);
        Assert.NotNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement
            .GetProperty("restorationAttempted").GetBoolean());
        Assert.False(document.RootElement
            .GetProperty("lightingRecoveryRequired").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_ReportsPendingBoundedCleanup()
    {
        var port = new FakePort { CleanupCompletes = false };
        string? json = null;
        var output = new StringWriter();
        Rz090581LightingCadenceValidationCommand command = Create(
            port,
            "YES\r\nYES\r\nYES\r\nYES\r\nYES\r\nYES\r\n",
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            (path, content, _) =>
            {
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    json = content;
                }
                return Task.CompletedTask;
            },
            output: output);

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.NotNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("cleanupRequired").GetBoolean());
        Assert.False(document.RootElement.GetProperty("cleanupCompleted").GetBoolean());
        Assert.True(document.RootElement.GetProperty("cleanupPending").GetBoolean());
        Assert.False(document.RootElement
            .GetProperty("lightingRecoveryRequired").GetBoolean());
        Assert.Equal(
            "knownStaticGreenBaselineConfirmed",
            document.RootElement.GetProperty("restorationOutcome").GetString());
        Assert.Contains(
            "cleanupPending",
            document.RootElement.GetProperty("failure").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "Cleanup status is separate from the lighting restoration evidence",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WarnsAboutLightingRecoveryWhenArtifactWriteFails()
    {
        var port = new FakePort
        {
            PushFailure = new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "Injected indeterminate write."),
        };
        var output = new StringWriter();
        Rz090581LightingCadenceValidationCommand command = Create(
            port,
            "YES\r\nYES\r\n",
            async (rate, _, _, pushFrame, cancellationToken) =>
            {
                try
                {
                    await pushFrame(
                        LightingCadenceProbeRunner.CreateFrame(0, TimeSpan.Zero),
                        cancellationToken);
                }
                catch (BladeWriteException exception)
                {
                    return FailedStage(rate, exception.SubmissionState);
                }
                throw new InvalidOperationException("The injected write should fail.");
            },
            (_, _, _) => throw new IOException("Injected artifact failure."),
            output: output);

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        string text = output.ToString();
        Assert.Contains("Validation artifacts could not be written.", text);
        Assert.Contains(
            "Lighting recovery is required (indeterminateMatrixWrite)",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DoesNotClaimRestoredStateWhenRestoreWriteFails()
    {
        var port = new FakePort { FailStaticWriteNumber = 5 };
        string? json = null;
        Rz090581LightingCadenceValidationCommand command = Create(
            port,
            "YES\r\nYES\r\nYES\r\nYES\r\nYES\r\n",
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            (path, content, _) =>
            {
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    json = content;
                }
                return Task.CompletedTask;
            });

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.NotNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("restoredStateKind").ValueKind);
        Assert.Equal(
            "knownStaticGreenBaselineUnconfirmed",
            document.RootElement.GetProperty("restorationOutcome").GetString());
        Assert.True(document.RootElement
            .GetProperty("lightingRecoveryRequired").GetBoolean());
        Assert.Equal(
            "indeterminateRestoreWrite",
            document.RootElement.GetProperty("lightingRecoveryReason").GetString());
    }

    [Fact]
    public async Task RunAsync_RequiresEffectReplacementApprovalBeforeOpeningHardware()
    {
        bool opened = false;
        var artifacts = new Dictionary<string, string>();
        var command = new Rz090581LightingCadenceValidationCommand(
            _ =>
            {
                opened = true;
                return Task.FromResult(
                    new HidSessionResult<IProductionLightingPort>(new FakePort(), null));
            },
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(64),
                    null)),
            ExactIdentity,
            (rate, _, _, _, _) => Task.FromResult(QualifiedStage(rate)),
            new StringReader("NO\r\n"),
            new StringWriter(),
            BuildIdentity,
            () => DateTimeOffset.Parse("2026-08-15T12:00:00Z"),
            (path, content, _) =>
            {
                artifacts[path] = content;
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            _ => NoopDisposable.Instance,
            () => null);

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090581LightingCadenceValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.False(opened);
        string json = artifacts.Single(pair => pair.Key.EndsWith(".json")).Value;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            "effectReplacementDeclined",
            document.RootElement.GetProperty("failure").GetString());
    }

    private static Rz090581LightingCadenceValidationCommand Create(
        FakePort port,
        string answers,
        Func<int, TimeSpan, TimeSpan,
            Func<AmbientLightingFrame, CancellationToken, Task>, CancellationToken,
            Task<LightingCadenceStageResult>> runStage,
        Func<string, string, CancellationToken, Task> writeArtifact,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TextWriter? output = null,
        Func<int, TimeSpan, TimeSpan,
            Func<AmbientLightingFrame, CancellationToken, Task>, CancellationToken,
            Task<LightingCadenceStageResult>>? runTidalStage = null) =>
        new(
            _ => Task.FromResult(
                new HidSessionResult<IProductionLightingPort>(port, null)),
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(
                        LightingBrightnessProtocol.PercentToRaw(25)),
                    null)),
            ExactIdentity,
            runStage,
            new StringReader(answers),
            output ?? new StringWriter(),
            BuildIdentity,
            () => DateTimeOffset.Parse("2026-08-15T12:00:00Z"),
            writeArtifact,
            delay ?? ((_, _) => Task.CompletedTask),
            _ => NoopDisposable.Instance,
            () => null,
            runTidalStage);

    private static HostIdentityReadResult ExactIdentity() => new(
        new HostIdentityEvidence(
            "Razer",
            "Blade 16 - RZ09-0581",
            "RZ09-05819EN4",
            "3.01"),
        null,
        HostFirmwareCompatibility.Tested);

    private static string[] ValidArguments() =>
    [
        "--confirm-target",
        Rz090581LightingCadenceValidationCommand.ExactTargetConfirmation("3.01"),
        "--rates",
        "30,45,60",
        "--pattern",
        "tidal",
        "--brightness",
        "25",
        "--seconds",
        "5",
        "--output",
        @"C:\validation",
    ];

    private static LightingCadenceStageResult QualifiedStage(int rate = 30) => new(
        rate,
        1000.0 / rate,
        100,
        100,
        100,
        0,
        rate,
        new(1, 1, 1, 1, 1),
        new(1000.0 / rate, 1000.0 / rate, 1000.0 / rate, 1000.0 / rate, 1000.0 / rate),
        1000.0 / rate,
        new(500, 5000, 20, 10, 0.5),
        true,
        null,
        null,
        [])
    {
        WritePhases = new LightingCadenceWritePhaseSummary(
            100,
            0,
            new(0.1, 0.1, 0.1, 0.1, 0.1),
            new(0.4, 0.4, 0.4, 0.4, 0.4),
            new(0.3, 0.3, 0.3, 0.3, 0.3),
            new(0.1, 0.1, 0.1, 0.1, 0.1),
            new(0.1, 0.1, 0.1, 0.1, 0.1)),
        QualificationWriteDuration = new(0.6, 0.6, 0.6, 0.6, 0.6),
        QualificationExcludesControllerGuard = true,
    };

    private static LightingCadenceStageResult FailedStage(
        int rate,
        WriteSubmissionState submissionState) => new(
        rate,
        1000.0 / rate,
        100,
        1,
        0,
        0,
        0,
        new(null, null, null, null, null),
        new(null, null, null, null, null),
        0,
        null,
        false,
        submissionState,
        "matrixWriteFailed",
        []);

    private sealed class FakePort : IProductionLightingPort
    {
        public DeviceCapabilities Capabilities { get; } = new(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>
            {
                LightingEffect.Static,
                LightingEffect.AmbientAwareness,
            },
            new HashSet<int>(),
            false,
            false);

        public LightingRendererOwnershipState OwnershipState { get; private set; }

        internal List<LightingSettings> Settings { get; } = [];

        internal int PushedFrames { get; private set; }

        internal BladeWriteException? PushFailure { get; init; }

        internal bool CleanupCompletes { get; init; } = true;

        internal int? FailStaticWriteNumber { get; init; }

        private int staticWrites;

        public Task<LightingSettings?> ReadStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<LightingSettings?>(null);

        public Task SetLightingAsync(
            LightingSettings settings,
            CancellationToken cancellationToken)
        {
            Settings.Add(settings);
            if (settings.Effect == LightingEffect.Static
                && ++staticWrites == FailStaticWriteNumber)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "Injected restore write failure.");
            }
            OwnershipState = settings.Effect switch
            {
                LightingEffect.Static => LightingRendererOwnershipState.Static,
                LightingEffect.AmbientAwareness =>
                    LightingRendererOwnershipState.AmbientAwareness,
                _ => LightingRendererOwnershipState.None,
            };
            return Task.CompletedTask;
        }

        public Task<byte> StepBrightnessAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken) =>
            Task.FromException<byte>(new NotSupportedException());

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken)
        {
            PushedFrames++;
            return PushFailure is null
                ? Task.CompletedTask
                : Task.FromException(PushFailure);
        }

        public Task SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());

        public Task<LightingRendererOwnershipState> RelinquishRendererAsync(
            CancellationToken cancellationToken) => Task.FromResult(OwnershipState);

        public Task<LightingRendererOwnershipState> InvalidateAsync() =>
            Task.FromResult(OwnershipState);

        public Task<bool> DisposeWithinCleanupDeadlineAsync() =>
            Task.FromResult(CleanupCompletes);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
