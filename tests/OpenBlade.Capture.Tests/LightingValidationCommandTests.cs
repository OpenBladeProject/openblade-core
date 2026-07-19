using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class LightingValidationCommandTests
{
    private const string Commit = "403aa01b11e969a6351595154569ef56f8878efa";
    private const string Version = "0.1.0+403aa01b11e969a6351595154569ef56f8878efa";
    private const string AssemblySha =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task RunAsync_ValidatesEveryTypedEffectAndWritesAdmittingArtifact()
    {
        var port = new FakeLightingPort();
        var console = new FakeConsole(SuccessfulAnswers());
        var artifacts = new List<(string Path, string Json)>();
        var command = CreateCommand(port, console, artifacts);

        int exitCode = await command.RunAsync(ValidArguments(), default);

        Assert.Equal(0, exitCode);
        Assert.Equal(27, port.Settings.Count); // baseline, twenty-five actions, restore.
        Assert.Equal(
            [
                LightingEffect.Static,
                LightingEffect.Off,
                LightingEffect.Static,
                LightingEffect.Spectrum,
                LightingEffect.Breathing,
                LightingEffect.Breathing,
                LightingEffect.Breathing,
                LightingEffect.Reactive,
                LightingEffect.Reactive,
                LightingEffect.Reactive,
                LightingEffect.Ripple,
                LightingEffect.Tidal,
                LightingEffect.Tidal,
                LightingEffect.Tidal,
                LightingEffect.Starlight,
                LightingEffect.Starlight,
                LightingEffect.Starlight,
                LightingEffect.Starlight,
                LightingEffect.Starlight,
                LightingEffect.Wave,
                LightingEffect.Wave,
                LightingEffect.Wheel,
                LightingEffect.Wheel,
                LightingEffect.Static,
                LightingEffect.Static,
                LightingEffect.Static,
                LightingEffect.Static,
            ],
            port.Settings.Select(setting => setting.Effect));
        Assert.Equal([53, 53, 53, 53], port.Pulses.Select(pulse => (int)pulse.MatrixSlot));
        Assert.Contains("watch the A key", console.Text, StringComparison.Ordinal);
        Assert.Single(artifacts);
        Assert.Equal(@"C:\ProgramData\OpenBlade\lighting-validation.json", artifacts[0].Path);

        using JsonDocument document = JsonDocument.Parse(artifacts[0].Json);
        JsonElement root = document.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(AssemblySha, root.GetProperty("protocolAssemblySha256").GetString());
        Assert.Equal(25, root.GetProperty("actions").GetArrayLength());
        Assert.True(root.GetProperty("allSupportedEffectsApplied").GetBoolean());
        Assert.True(root.GetProperty("brightnessIndependentReadback").GetBoolean());
        Assert.True(root.GetProperty("baselineRestored").GetBoolean());
        Assert.False(root.GetProperty("unknownPreTestStateRestored").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("failure").ValueKind);
        Assert.DoesNotContain("devicePath", artifacts[0].Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transaction", artifacts[0].Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", artifacts[0].Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_TypedFailureBestEffortRestoresKnownBaselineWithoutReplacingAdmission()
    {
        var port = new FakeLightingPort { FailEffect = LightingEffect.Spectrum };
        var console = new FakeConsole(
            LightingValidationCommand.ExactTargetConfirmation,
            "yes",
            "yes",
            "yes",
            "yes");
        var artifacts = new List<(string Path, string Json)>();
        var command = CreateCommand(port, console, artifacts);

        int exitCode = await command.RunAsync(ValidArguments(), default);

        Assert.Equal(LightingValidationCommand.ValidationFailureExitCode, exitCode);
        Assert.Equal(LightingEffect.Static, port.Settings[^1].Effect);
        Assert.Single(artifacts);
        Assert.Contains("lighting-validation.failed-", artifacts[0].Path, StringComparison.Ordinal);
        Assert.NotEqual(@"C:\ProgramData\OpenBlade\lighting-validation.json", artifacts[0].Path);
        using JsonDocument document = JsonDocument.Parse(artifacts[0].Json);
        Assert.False(document.RootElement.GetProperty("allSupportedEffectsApplied").GetBoolean());
        Assert.False(
            document.RootElement.GetProperty("unknownPreTestStateRestored").GetBoolean());
        Assert.Equal(
            "validationFailed:action.spectrum.apply:io",
            document.RootElement.GetProperty("failure").GetString());
        Assert.Contains(
            "Validation diagnostic: validationFailed:action.spectrum.apply:io",
            console.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_BaselineWriteFailureRecordsStageAndSafeProtocolCategory()
    {
        var port = new FakeLightingPort
        {
            FailEffect = LightingEffect.Static,
            FailException = new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The matrix write failed (Timeout)."),
        };
        var console = new FakeConsole(
            LightingValidationCommand.ExactTargetConfirmation);
        var artifacts = new List<(string Path, string Json)>();
        LightingValidationCommand command = CreateCommand(port, console, artifacts);

        int exitCode = await command.RunAsync(ValidArguments(), default);

        Assert.Equal(LightingValidationCommand.ValidationFailureExitCode, exitCode);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(artifacts).Json);
        Assert.Equal(
            "validationFailed:knownBaselineApply:bladeWrite.Indeterminate." +
            "matrixWrite.Timeout|validationFailed:knownBaselineRestore:" +
            "bladeWrite.Indeterminate.matrixWrite.Timeout",
            document.RootElement.GetProperty("failure").GetString());
        Assert.Contains(
            "Validation diagnostic: validationFailed:knownBaselineApply:" +
            "bladeWrite.Indeterminate.matrixWrite.Timeout",
            console.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("payload", artifacts[0].Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", artifacts[0].Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_CommandFailureRecordsOnlySanitizedOperationAndValidationCategory()
    {
        var port = new FakeLightingPort
        {
            FailEffect = LightingEffect.Static,
            FailException = new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The lighting command 00/86 failed " +
                "(ProtocolFailure, PayloadValidator)."),
        };
        var console = new FakeConsole(
            LightingValidationCommand.ExactTargetConfirmation);
        var artifacts = new List<(string Path, string Json)>();
        LightingValidationCommand command = CreateCommand(port, console, artifacts);

        int exitCode = await command.RunAsync(ValidArguments(), default);

        Assert.Equal(LightingValidationCommand.ValidationFailureExitCode, exitCode);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(artifacts).Json);
        Assert.StartsWith(
            "validationFailed:knownBaselineApply:bladeWrite.Indeterminate." +
            "lightingCommand.00.86.ProtocolFailure.PayloadValidator",
            document.RootElement.GetProperty("failure").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MatrixFailureRecordsOnlyBoundedNativeDiagnostic()
    {
        var port = new FakeLightingPort
        {
            FailEffect = LightingEffect.Static,
            FailException = new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The matrix write failed " +
                "(NativeIoFailure, SetSubmit, -2146233079, None)."),
        };
        var console = new FakeConsole(
            LightingValidationCommand.ExactTargetConfirmation);
        var artifacts = new List<(string Path, string Json)>();
        LightingValidationCommand command = CreateCommand(port, console, artifacts);

        int exitCode = await command.RunAsync(ValidArguments(), default);

        Assert.Equal(LightingValidationCommand.ValidationFailureExitCode, exitCode);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(artifacts).Json);
        Assert.StartsWith(
            "validationFailed:knownBaselineApply:bladeWrite.Indeterminate." +
            "matrixWrite.NativeIoFailure.SetSubmit.error.-2146233079.bytes.None",
            document.RootElement.GetProperty("failure").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShortSuccessfulResponseIsRecordedAsBoundedHex()
    {
        var port = new FakeLightingPort
        {
            FailEffect = LightingEffect.Static,
            FailException = new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The lighting command 00/84 returned 0200."),
        };
        var console = new FakeConsole(
            LightingValidationCommand.ExactTargetConfirmation);
        var artifacts = new List<(string Path, string Json)>();
        LightingValidationCommand command = CreateCommand(port, console, artifacts);

        int exitCode = await command.RunAsync(ValidArguments(), default);

        Assert.Equal(LightingValidationCommand.ValidationFailureExitCode, exitCode);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(artifacts).Json);
        Assert.StartsWith(
            "validationFailed:knownBaselineApply:bladeWrite.Indeterminate." +
            "lightingCommand.00.84.response.0200",
            document.RootElement.GetProperty("failure").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_LostRendererOwnershipCannotProduceAdmittingArtifact()
    {
        var port = new FakeLightingPort
        {
            FaultOwnershipEffect = LightingEffect.Spectrum,
        };
        var console = new FakeConsole(
            LightingValidationCommand.ExactTargetConfirmation,
            "yes", // Initial baseline.
            "yes", // Off.
            "yes", // Static.
            "yes", // Spectrum appearance before the ownership check.
            "yes"); // Restored baseline.
        var artifacts = new List<(string Path, string Json)>();
        var command = CreateCommand(port, console, artifacts);

        int exitCode = await command.RunAsync(ValidArguments(), default);

        Assert.Equal(LightingValidationCommand.ValidationFailureExitCode, exitCode);
        Assert.Equal(LightingEffect.Static, port.Settings[^1].Effect);
        Assert.Contains("lighting-validation.failed-", Assert.Single(artifacts).Path);
        using JsonDocument document = JsonDocument.Parse(artifacts[0].Json);
        Assert.Equal(
            "rendererOwnershipLost",
            document.RootElement.GetProperty("failure").GetString());
        Assert.False(
            document.RootElement.GetProperty("allSupportedEffectsApplied").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_InteractiveTargetDeclinePerformsNoHardwareWrite()
    {
        var port = new FakeLightingPort();
        var console = new FakeConsole("no");
        var artifacts = new List<(string Path, string Json)>();
        int openCount = 0;
        LightingValidationCommand command = CreateCommand(
            port,
            console,
            artifacts,
            () => openCount++);

        int exitCode = await command.RunAsync(ValidArguments(), default);

        Assert.Equal(LightingValidationCommand.ValidationFailureExitCode, exitCode);
        Assert.Equal(0, openCount);
        Assert.Empty(port.Settings);
        Assert.Single(artifacts);
        Assert.Contains(".failed-", artifacts[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UnsafeEnvironmentPerformsNoHardwareWrite()
    {
        var port = new FakeLightingPort();
        var console = new FakeConsole();
        var artifacts = new List<(string Path, string Json)>();
        int openCount = 0;
        LightingValidationCommand command = CreateCommand(
            port,
            console,
            artifacts,
            () => openCount++,
            () => "Stop the OpenBlade service before validating lighting.");

        int exitCode = await command.RunAsync(ValidArguments(), default);

        Assert.Equal(LightingValidationCommand.ValidationFailureExitCode, exitCode);
        Assert.Equal(0, openCount);
        Assert.Empty(port.Settings);
        Assert.Contains("Stop the OpenBlade service", console.Text, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(artifacts).Json);
        Assert.Equal(
            "unsafeValidationEnvironment",
            document.RootElement.GetProperty("failure").GetString());
    }

    [Fact]
    public async Task RunAsync_CancellationAfterWriteBestEffortRestoresKnownBaseline()
    {
        using var cancellation = new CancellationTokenSource();
        var port = new FakeLightingPort
        {
            OnSet = setting =>
            {
                if (setting.Effect == LightingEffect.Static
                    && setting.Color == new RgbColor(0, 128, byte.MaxValue))
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
            },
        };
        var console = new FakeConsole(
            LightingValidationCommand.ExactTargetConfirmation,
            "yes",
            "yes",
            "yes");
        var artifacts = new List<(string Path, string Json)>();
        LightingValidationCommand command = CreateCommand(port, console, artifacts);

        int exitCode = await command.RunAsync(ValidArguments(), cancellation.Token);

        Assert.Equal(LightingValidationCommand.CanceledExitCode, exitCode);
        Assert.Equal(LightingEffect.Static, port.Settings[^1].Effect);
        Assert.Equal(new RgbColor(0, byte.MaxValue, 0), port.Settings[^1].Color);
        Assert.Single(artifacts);
        using JsonDocument document = JsonDocument.Parse(artifacts[0].Json);
        Assert.Equal("canceled", document.RootElement.GetProperty("failure").GetString());
        Assert.True(
            document.RootElement.GetProperty("baselineRestoreAttempted").GetBoolean());
        Assert.False(
            document.RootElement.GetProperty("unknownPreTestStateRestored").GetBoolean());
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        Array.Empty<string>(),
        new[] { "--confirm-target", "yes", "--brightness", "60" },
        new[]
        {
            "--confirm-target",
            "rz09-0581:1532:02e0:3.01",
            "--brightness",
            "60",
        },
        new[]
        {
            "--confirm-target",
            LightingValidationCommand.ExactTargetConfirmation,
            "--brightness",
            "0",
        },
        new[]
        {
            "--confirm-target",
            LightingValidationCommand.ExactTargetConfirmation,
            "--brightness",
            "101",
        },
        new[] { "03/0B", "--brightness", "60" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task RunAsync_RejectsInexactOrRawArguments(string[] arguments)
    {
        var port = new FakeLightingPort();
        var console = new FakeConsole();
        var artifacts = new List<(string Path, string Json)>();
        LightingValidationCommand command = CreateCommand(port, console, artifacts);

        int exitCode = await command.RunAsync(arguments, default);

        Assert.Equal(LightingValidationCommand.UsageExitCode, exitCode);
        Assert.Empty(port.Settings);
        Assert.Empty(artifacts);
        Assert.Contains(
            "--confirm-target RZ09-0581:1532:02E0:3.01",
            console.Text,
            StringComparison.Ordinal);
    }

    private static LightingValidationCommand CreateCommand(
        FakeLightingPort port,
        FakeConsole console,
        List<(string Path, string Json)> artifacts,
        Action? onOpen = null,
        Func<string?>? checkEnvironment = null) =>
        new(
            _ =>
            {
                onOpen?.Invoke();
                return Task.FromResult(
                    new HidSessionResult<IProductionLightingPort>(port, null));
            },
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(ToRaw(port.Current.Brightness)),
                    null)),
            console.Reader,
            console.Writer,
            Version,
            Commit,
            AssemblySha,
            @"C:\ProgramData\OpenBlade\lighting-validation.json",
            () => DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
            (path, json, _) =>
            {
                artifacts.Add((path, json));
                return Task.CompletedTask;
            },
            _ => NoopDisposable.Instance,
            checkEnvironment);

    private static string[] ValidArguments() =>
    [
        "--confirm-target",
        LightingValidationCommand.ExactTargetConfirmation,
        "--brightness",
        "60",
    ];

    private static string[] SuccessfulAnswers() =>
    [
        LightingValidationCommand.ExactTargetConfirmation,
        "yes", // Initial known baseline.
        "yes", // Off.
        "yes", // Static.
        "yes", // Spectrum.
        "yes", // Breathing single.
        "yes", // Breathing dual.
        "yes", // Breathing random.
        string.Empty,
        "yes", // Reactive short.
        string.Empty,
        "yes", // Reactive medium.
        string.Empty,
        "yes", // Reactive long.
        string.Empty,
        "yes", // Ripple.
        "yes", // Tidal fixed rightward.
        "yes", // Tidal fixed leftward.
        "yes", // Tidal random.
        "yes", // Starlight single short.
        "yes", // Starlight single medium.
        "yes", // Starlight single long.
        "yes", // Starlight dual.
        "yes", // Starlight random.
        "yes", // Wave rightward.
        "yes", // Wave leftward.
        "yes", // Wheel clockwise.
        "yes", // Wheel counterclockwise.
        "yes", // Logo Breathing.
        "yes", // Logo Off.
        "yes", // Logo Static restored.
        "yes", // Restored known baseline.
    ];

    private static int ToRaw(byte percent) => percent == 0
        ? 0
        : (int)Math.Ceiling(percent * byte.MaxValue / 100d);

    private sealed class FakeLightingPort : IProductionLightingPort
    {
        public LightingRendererOwnershipState OwnershipState { get; private set; }

        public DeviceCapabilities Capabilities { get; } = new(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
            new HashSet<int>(),
            false,
            false)
        {
            LogoLightingModes = new HashSet<LogoLightingMode>(
                Enum.GetValues<LogoLightingMode>()),
        };

        public List<LightingSettings> Settings { get; } = [];
        public List<ReactiveKeyPulse> Pulses { get; } = [];
        public LightingSettings Current { get; private set; } = LightingSettingsFactory.Off();
        public LightingEffect? FailEffect { get; init; }
        public Exception? FailException { get; init; }
        public LightingEffect? FaultOwnershipEffect { get; init; }
        public Action<LightingSettings>? OnSet { get; init; }

        public Task<LightingSettings?> ReadStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<LightingSettings?>(Current);

        public Task SetLightingAsync(
            LightingSettings settings,
            CancellationToken cancellationToken)
        {
            OwnershipState = settings.Effect switch
            {
                LightingEffect.Off => LightingRendererOwnershipState.None,
                LightingEffect.Static => LightingRendererOwnershipState.Static,
                LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
                LightingEffect.Breathing => LightingRendererOwnershipState.Breathing,
                LightingEffect.Reactive => LightingRendererOwnershipState.Reactive,
                LightingEffect.Tidal => LightingRendererOwnershipState.Tidal,
                LightingEffect.Starlight => LightingRendererOwnershipState.Starlight,
                LightingEffect.Wave => LightingRendererOwnershipState.Wave,
                LightingEffect.Wheel => LightingRendererOwnershipState.Wheel,
                LightingEffect.Ripple => LightingRendererOwnershipState.Ripple,
                _ => throw new ArgumentOutOfRangeException(),
            };
            Settings.Add(settings);
            OnSet?.Invoke(settings);
            if (settings.Effect == FailEffect)
            {
                throw FailException ?? new IOException("Fake typed failure");
            }

            Current = settings;
            if (settings.Effect == FaultOwnershipEffect)
            {
                OwnershipState = LightingRendererOwnershipState.Faulted;
            }
            return Task.CompletedTask;
        }

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken)
        {
            Pulses.Add(pulse);
            return Task.CompletedTask;
        }

        public Task<LightingRendererOwnershipState> InvalidateAsync()
        {
            LightingRendererOwnershipState observed = OwnershipState;
            OwnershipState = LightingRendererOwnershipState.None;
            return Task.FromResult(observed);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConsole(params string[] answers)
    {
        private readonly StringWriter writer = new();

        internal TextReader Reader { get; } =
            new StringReader(string.Join(Environment.NewLine, answers));

        internal TextWriter Writer => writer;
        internal string Text => writer.ToString();
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
