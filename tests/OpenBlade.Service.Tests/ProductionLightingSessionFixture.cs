using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

internal static class ProductionLightingSessionFixture
{
    internal sealed class FakeLightingFactory : IProductionLightingPortFactory
    {
        private readonly HidSessionResult<IProductionLightingPort>[] results;
        private int nextPort;

        public FakeLightingFactory(
            FakeLightingPort firstPort,
            params FakeLightingPort[] additionalPorts)
        {
            results = new[] { firstPort }
                .Concat(additionalPorts)
                .Select(port => new HidSessionResult<IProductionLightingPort>(port, null))
                .ToArray();
        }

        private FakeLightingFactory(HidSessionResult<IProductionLightingPort>[] results)
        {
            this.results = results;
        }

        public static FakeLightingFactory FromResults(
            HidSessionResult<IProductionLightingPort> firstResult,
            params HidSessionResult<IProductionLightingPort>[] additionalResults) =>
            new(new[] { firstResult }.Concat(additionalResults).ToArray());

        public DeviceCapabilities Capabilities { get; } = new(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
            new HashSet<int>(),
            false,
            false)
        {
            SupportsPlaybackMuteIndicator = true,
            SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite,
            WritableSpecialKeySlots =
                SpecialKeySlotSet.M3
                | SpecialKeySlotSet.M4
                | SpecialKeySlotSet.M5,
            LampArrayLightingAccess = DeviceFeatureAccess.ReadOnly,
            ObservedLightingEffects =
                new HashSet<LightingEffect> { LightingEffect.Ripple, LightingEffect.Wheel },
            KeyboardLightingLayoutId = KeyboardLightingLayoutIds.Rz09_0581,
        };

        public int OpenCount { get; private set; }

        public Func<bool>? DriverModeWriteAllowed { get; private set; }

        public Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
            Func<bool> driverModeWriteAllowed, CancellationToken cancellationToken)
        {
            DriverModeWriteAllowed = driverModeWriteAllowed;
            return OpenAsync(cancellationToken);
        }

        public Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            OpenCount++;
            HidSessionResult<IProductionLightingPort> result =
                results[Math.Min(nextPort, results.Length - 1)];
            nextPort++;
            return Task.FromResult(result);
        }
    }

    internal sealed class ThrowingLightingFactory(Exception exception)
        : IProductionLightingPortFactory
    {
        public DeviceCapabilities Capabilities { get; } = DeviceCapabilities.Unavailable();

        public int OpenCount { get; private set; }

        public Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromException<HidSessionResult<IProductionLightingPort>>(exception);
        }
    }

    internal sealed class ForwardingLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    internal sealed class FakeLightingPort : IProductionLightingPort
    {
        public FakeLightingPort(
            bool supportsKeyboardBrightness = true,
            string keyboardLightingLayoutId = KeyboardLightingLayoutIds.Rz09_0581,
            IReadOnlySet<LightingRefreshRate>? supportedLightingRefreshRates = null)
        {
            Capabilities = new DeviceCapabilities(
                new HashSet<PerformancePreset>(),
                new HashSet<FanMode>(),
                new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
                new HashSet<int>(),
                false,
                false)
            {
                SupportsKeyboardBrightness = supportsKeyboardBrightness,
                SupportsPlaybackMuteIndicator = true,
                SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite,
                WritableSpecialKeySlots =
                    SpecialKeySlotSet.M3
                    | SpecialKeySlotSet.M4
                    | SpecialKeySlotSet.M5,
                LampArrayLightingAccess = DeviceFeatureAccess.ReadOnly,
                KeyboardLightingLayoutId = keyboardLightingLayoutId,
                ObservedLightingEffects =
                    new HashSet<LightingEffect> { LightingEffect.Ripple, LightingEffect.Wheel },
                SupportedLightingRefreshRates = supportedLightingRefreshRates
                    ?? new HashSet<LightingRefreshRate>(),
            };
        }

        public DeviceCapabilities Capabilities { get; }

        public LightingRendererOwnershipState OwnershipState { get; set; }

        public DriverModeRestoreOutcome DriverModeRestoreOutcome { get; set; }

        public List<LightingSettings> Writes { get; } = [];

        public List<ReactiveKeyPulse> Pulses { get; } = [];

        public List<AmbientLightingFrame> AmbientFrames { get; } = [];

        public List<bool> PlaybackMuteIndicatorStates { get; } = [];

        public List<KeyboardBrightnessDirection> BrightnessSteps { get; } = [];

        public List<LightingRefreshRate> LightingRefreshRates { get; } = [];

        public byte BrightnessStepResult { get; init; } = 50;

        public BladeWriteException? PlaybackMuteFailure { get; init; }

        public int DisposeCount { get; private set; }

        public int RelinquishCount { get; private set; }

        public bool BlockLowPowerClose { get; init; }
        public TaskCompletionSource LowPowerCloseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LowPowerCloseRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LowPowerOffCount { get; private set; }

        public int DrainCount { get; private set; }

        public bool DrainResult { get; init; } = true;

        public TaskCompletionSource DrainStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DriverModeEnsureCount { get; private set; }

        public int DriverModeReadCount { get; private set; }

        public int DriverModeReleaseCount { get; private set; }

        public TaskCompletionSource LightingRefreshRateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource? lightingRefreshRateRelease;
        private int blockNextLightingRefreshRate;

        public Exception? ReadFailure { get; set; }

        public LightingSettings? StateReadResult { get; init; }

        public List<string>? ReadOrder { get; init; }

        public int ReadStateCalls { get; private set; }

        public LogoLightingMode? LogoState { get; init; }

        public int ReadLogoStateCalls { get; private set; }

        public Task EnsureDriverModeAsync(CancellationToken cancellationToken)
        {
            DriverModeEnsureCount++;
            return Task.CompletedTask;
        }

        public Task<bool?> ReadDriverModeAsync(CancellationToken cancellationToken)
        {
            DriverModeReadCount++;
            return Task.FromResult<bool?>(true);
        }

        public Task<bool?> ReleaseDriverModeAsync(CancellationToken cancellationToken)
        {
            DriverModeReleaseCount++;
            return Task.FromResult<bool?>(false);
        }

        public Task<bool?> ReleaseDriverModeForLowPowerAsync(
            Func<bool> stillSuspending,
            CancellationToken cancellationToken) => stillSuspending()
                ? ReleaseDriverModeAsync(cancellationToken)
                : Task.FromResult<bool?>(null);

        public Task<LightingSettings?> ReadStateAsync(CancellationToken cancellationToken)
        {
            ReadStateCalls++;
            ReadOrder?.Add("Lighting");
            return ReadFailure is null
                ? Task.FromResult<LightingSettings?>(
                    StateReadResult ?? Writes.LastOrDefault())
                : Task.FromException<LightingSettings?>(ReadFailure);
        }

        public Task<LogoLightingMode?> ReadLogoStateAsync(
            CancellationToken cancellationToken)
        {
            ReadLogoStateCalls++;
            return Task.FromResult(LogoState);
        }

        public Task SetLightingAsync(
            LightingSettings settings,
            CancellationToken cancellationToken)
        {
            Writes.Add(settings);
            OwnershipState = settings.Effect switch
            {
                LightingEffect.Off => LightingRendererOwnershipState.None,
                LightingEffect.Static => LightingRendererOwnershipState.Static,
                LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
                LightingEffect.Breathing => LightingRendererOwnershipState.Breathing,
                LightingEffect.Reactive => LightingRendererOwnershipState.Reactive,
                LightingEffect.AmbientAwareness =>
                    LightingRendererOwnershipState.AmbientAwareness,
                LightingEffect.Sequence => LightingRendererOwnershipState.Sequence,
                _ => throw new ArgumentOutOfRangeException(),
            };
            return Task.CompletedTask;
        }

        public Task<LightingLowPowerOffResult> TurnOffForLowPowerAsync(
            Func<bool> stillSuspending,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LowPowerOffCount++;
            LightingRendererOwnershipState observed = OwnershipState;
            if (!stillSuspending())
            {
                return Task.FromResult(new LightingLowPowerOffResult(
                    observed, LightingLowPowerOffOutcome.KnownNotApplied));
            }

            OwnershipState = LightingRendererOwnershipState.None;
            return Task.FromResult(new LightingLowPowerOffResult(
                observed, LightingLowPowerOffOutcome.ConfirmedOff));
        }

        public void BlockNextLightingRefreshRate()
        {
            lightingRefreshRateRelease = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref blockNextLightingRefreshRate, 1);
        }

        public async Task SetLightingRefreshRateAsync(
            LightingRefreshRate refreshRate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref blockNextLightingRefreshRate, 0) != 0)
            {
                LightingRefreshRateStarted.TrySetResult();
                await lightingRefreshRateRelease!.Task.WaitAsync(cancellationToken);
            }

            LightingRefreshRates.Add(refreshRate);
        }

        public Task<byte> StepBrightnessAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken)
        {
            BrightnessSteps.Add(direction);
            return Task.FromResult(BrightnessStepResult);
        }

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken)
        {
            Pulses.Add(pulse);
            return Task.CompletedTask;
        }

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken)
        {
            AmbientFrames.Add(frame);
            return Task.CompletedTask;
        }

        public Task SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken)
        {
            PlaybackMuteIndicatorStates.Add(muted);
            if (PlaybackMuteFailure is { } failure)
            {
                OwnershipState = LightingRendererOwnershipState.Faulted;
                return Task.FromException(failure);
            }
            return Task.CompletedTask;
        }

        public Task<LightingRendererOwnershipState> RelinquishRendererAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RelinquishCount++;
            LightingRendererOwnershipState observed = OwnershipState;
            OwnershipState = LightingRendererOwnershipState.None;
            return Task.FromResult(observed);
        }

        public async Task<LightingRendererOwnershipState> InvalidateAsync()
        {
            LightingRendererOwnershipState observed = OwnershipState;
            await DisposeAsync();
            return observed;
        }

        public async Task<LightingRendererOwnershipState> InvalidateForLowPowerAsync()
        {
            LightingRendererOwnershipState observed = OwnershipState;
            LowPowerCloseStarted.TrySetResult();
            if (BlockLowPowerClose)
            {
                await LowPowerCloseRelease.Task;
            }
            await DisposeAsync();
            return observed;
        }

        public async Task<bool> DisposeWithinCleanupDeadlineAsync()
        {
            DrainCount++;
            DrainStarted.TrySetResult();
            await DisposeAsync();
            return DrainResult;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            OwnershipState = LightingRendererOwnershipState.None;
            return ValueTask.CompletedTask;
        }
    }
}
