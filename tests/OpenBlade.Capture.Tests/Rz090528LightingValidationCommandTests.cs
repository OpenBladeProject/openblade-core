using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528LightingValidationCommandTests
{
    private static readonly ProductionLightingBuildIdentity BuildIdentity = new(
        "0.1.0+403aa01b11e969a6351595154569ef56f8878efa",
        "403aa01b11e969a6351595154569ef56f8878efa",
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");

    [Fact]
    public async Task RunAsync_AcknowledgesBothEffectsAndRestoresStaticGreen50()
    {
        var port = new FakePort();
        string? artifact = null;
        var command = Create(
            port,
            "YES\r\nYES\r\nYES\r\n",
            (_, json, _) =>
            {
                artifact = json;
                return Task.CompletedTask;
            });

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(artifact);
        Assert.Equal(
            [
                LightingEffect.Static,
                LightingEffect.Spectrum,
                LightingEffect.Static,
            ],
            port.Settings.Select(settings => settings.Effect));
        Assert.Equal(50, port.Settings[^1].Brightness);
        Assert.Equal(new RgbColor(0, byte.MaxValue, 0), port.Settings[^1].Color);
        using JsonDocument document = JsonDocument.Parse(artifact);
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("restoredStaticGreen50").GetBoolean());
        Assert.False(root.GetProperty("matrixReadbackAvailable").GetBoolean());
        Assert.Null(root.GetProperty("failure").GetString());
        Assert.Equal(2, root.GetProperty("actions").GetArrayLength());
    }

    [Fact]
    public async Task RunAsync_RestoresBaselineWhenSpectrumTransitionFails()
    {
        var port = new FakePort { FailEffect = LightingEffect.Spectrum };
        string? artifact = null;
        var command = Create(
            port,
            "YES\r\nYES\r\n",
            (_, json, _) =>
            {
                artifact = json;
                return Task.CompletedTask;
            });

        int exitCode = await command.RunAsync(
            ValidArguments(),
            CancellationToken.None);

        Assert.Equal(
            Rz090528LightingValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.NotNull(artifact);
        Assert.Equal(LightingEffect.Static, port.Settings[^1].Effect);
        using JsonDocument document = JsonDocument.Parse(artifact);
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("restoredStaticGreen50").GetBoolean());
        Assert.Contains(
            "actionFailed",
            root.GetProperty("failure").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RejectsWrongExactTargetWithoutOpeningHardware()
    {
        var port = new FakePort();
        bool opened = false;
        var command = new Rz090528LightingValidationCommand(
            _ =>
            {
                opened = true;
                return Task.FromResult(
                    new HidSessionResult<IProductionLightingPort>(port, null));
            },
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(128),
                    null)),
            new StringReader(string.Empty),
            new StringWriter(),
            BuildIdentity,
            @"C:\ProgramData\OpenBlade\rz090528-lighting.json",
            writeArtifact: (_, _, _) => Task.CompletedTask,
            registerCancellation: _ => NoopDisposable.Instance,
            checkEnvironment: () => null);

        int exitCode = await command.RunAsync(
            ["--confirm-target", "RZ09-0581:1532:02E0:3.01"],
            CancellationToken.None);

        Assert.Equal(Rz090528LightingValidationCommand.UsageExitCode, exitCode);
        Assert.False(opened);
    }

    private static Rz090528LightingValidationCommand Create(
        FakePort port,
        string answers,
        Func<string, string, CancellationToken, Task> writeArtifact) =>
        new(
            _ => Task.FromResult(
                new HidSessionResult<IProductionLightingPort>(port, null)),
            _ => Task.FromResult(
                new HidSessionResult<LightingBrightnessReading>(
                    new LightingBrightnessReading(
                        LightingBrightnessProtocol.PercentToRaw(
                            Rz090528LightingValidationCommand.ValidationBrightness)),
                    null)),
            new StringReader(answers),
            new StringWriter(),
            BuildIdentity,
            @"C:\ProgramData\OpenBlade\rz090528-lighting.json",
            () => DateTimeOffset.Parse("2026-07-30T12:00:00Z"),
            writeArtifact,
            _ => NoopDisposable.Instance,
            () => null);

    private static string[] ValidArguments() =>
    [
        "--confirm-target",
        Rz090528LightingValidationCommand.ExactTargetConfirmation,
    ];

    private sealed class FakePort : IProductionLightingPort
    {
        public DeviceCapabilities Capabilities { get; } = new(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Spectrum,
                LightingEffect.Static,
            },
            new HashSet<int>(),
            false,
            false);

        public LightingRendererOwnershipState OwnershipState { get; private set; }

        internal LightingEffect? FailEffect { get; init; }

        internal List<LightingSettings> Settings { get; } = [];

        public Task<LightingSettings?> ReadStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<LightingSettings?>(null);

        public Task SetLightingAsync(
            LightingSettings settings,
            CancellationToken cancellationToken)
        {
            Settings.Add(settings);
            if (settings.Effect == FailEffect)
            {
                throw new IOException("Injected failure.");
            }

            OwnershipState = settings.Effect switch
            {
                LightingEffect.Static => LightingRendererOwnershipState.Static,
                LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
                _ => LightingRendererOwnershipState.None,
            };
            return Task.CompletedTask;
        }

        public Task<byte> StepBrightnessAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken) =>
            Task.FromException<byte>(
                new CapabilityUnavailableException("Unavailable in test."));

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken) =>
            Task.FromException(
                new CapabilityUnavailableException("Unavailable in test."));

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken) =>
            Task.FromException(
                new CapabilityUnavailableException("Unavailable in test."));

        public Task SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken) =>
            Task.FromException(
                new CapabilityUnavailableException("Unavailable in test."));

        public Task<LightingRendererOwnershipState> InvalidateAsync() =>
            Task.FromResult(OwnershipState);

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
