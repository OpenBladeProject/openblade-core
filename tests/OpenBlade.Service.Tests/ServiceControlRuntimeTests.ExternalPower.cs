using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.UsbC)]
    public async Task ExternalPower_ExplicitHyperBoostRecoveryRestoresExactSupportedIntent(PowerSource source)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore
        {
            Journal = CreateRestorePendingJournal(FanControlSettings.Default,
                new HyperBoostRecoveryBaseline(PerformancePreset.Balanced,
                    FanMode.Automatic, HyperBoostActive: false)) with
            {
                RestorePowerSource = source,
            },
        };
        TestContext context = CreateContext(true, initialPowerSource: source,
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(
                CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult result = await context.Runtime.RecoverHyperBoostRestoreAsync(
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(source, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Null(journal.Journal);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalPower_HyperBoostExitUsesDestinationAfterSourceChange(bool changesDuringSubmission)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = CreateContext(true,
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with { Performance = PerformancePreset.Silent },
            UsbC = profiles.UsbC with { Performance = PerformancePreset.Performance },
        };
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None)).Status);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetPerformanceOverrideAsync(null, CancellationToken.None)).Status);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None)).Status);
        Assert.Equal(PowerSource.PluggedIn, journal.Journal?.RestorePowerSource);
        Assert.Equal(PerformancePreset.Silent, journal.Journal?.RestoreIntent.Performance);

        if (changesDuringSubmission)
        {
            context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
                WriteSubmissionState.Indeterminate, "Injected disable with a source boundary."));
            context.Device.AfterHyperBoostFailure = () =>
            {
                context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Performance = PerformancePreset.Balanced,
                    Fans = FanMode.Automatic,
                    HyperBoostActive = false,
                };
            };
        }
        else
        {
            context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
            await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
            Assert.True(context.Device.ConfirmedState.HyperBoostActive);
        }

        ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
        Assert.Equal(PerformancePreset.Performance, context.Runtime.GetStatus().DesiredState.Performance.Value);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(2, context.Device.HyperBoostSetCalls);
        Assert.Null(journal.Journal);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, "suspend")]
    [InlineData(PowerSource.UsbC, "suspend")]
    [InlineData(PowerSource.PluggedIn, "shutdown")]
    [InlineData(PowerSource.UsbC, "shutdown")]
    [InlineData(PowerSource.PluggedIn, "pad")]
    [InlineData(PowerSource.UsbC, "pad")]
    public async Task ExternalPower_UnchangedSourceLifecycleRetainsEntryIntent(PowerSource source, string boundary)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        var pad = new FixedCoolingPadDiscoverySource(boundary == "pad"
            ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent);
        TestContext context = CreateContext(true, initialPowerSource: source,
            hyperBoostPadDiscoverySource: pad, hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        if (boundary == "pad")
        {
            context.Device.Capabilities = context.Device.Capabilities with
            {
                HyperBoostBaseModes = context.Device.Capabilities.HyperBoostBaseModes! with
                {
                    CoolingPadEntryBaseMode = PerformancePreset.Balanced,
                },
            };
        }
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with { Performance = PerformancePreset.Silent },
            UsbC = profiles.UsbC with { Performance = PerformancePreset.Silent },
        };
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None)).Status);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetPerformanceOverrideAsync(null, CancellationToken.None)).Status);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None)).Status);
        Assert.Equal(source, journal.Journal?.RestorePowerSource);
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with { Performance = PerformancePreset.Performance },
            UsbC = profiles.UsbC with { Performance = PerformancePreset.Performance },
        };
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None)).Status);
        Assert.Equal(PerformancePreset.Performance, context.Runtime.GetStatus().DesiredState.Performance.Value);

        if (boundary == "pad")
        {
            pad.State = CoolingPadConnectionState.Absent;
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }
        else if (boundary == "shutdown")
        {
            await context.Runtime.PrepareForShutdownAsync(CancellationToken.None);
        }
        else
        {
            await context.Runtime.PrepareForLowPowerAsync(CancellationToken.None);
        }

        Assert.Equal(source, context.Runtime.GetStatus().ActivePowerSource);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
        Assert.Null(journal.Journal);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalPower_PendingRestoreUsesNewSourceDestination(bool deferred)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore
        {
            Journal = CreateRestorePendingJournal(FanControlSettings.Default,
                new HyperBoostRecoveryBaseline(PerformancePreset.Balanced,
                    FanMode.Automatic, HyperBoostActive: false)) with
            {
                RestoreIntent = new HyperBoostRestoreIntent(PerformancePreset.Silent,
                    null, null, FanControlSettings.Default),
                RestorePowerSource = PowerSource.PluggedIn,
                RestoreRetryDisposition = deferred
                    ? HyperBoostRestoreRetryDisposition.DeferredPreSubmission
                    : HyperBoostRestoreRetryDisposition.None,
                RestoreRetryTarget = deferred ? HyperBoostRestoreRetryTarget.ExactJournalIntent
                    : HyperBoostRestoreRetryTarget.None,
            },
        };
        var pendingRestore = journal.Journal;
        journal.Journal = null;
        TestContext context = CreateContext(true, initialPowerSource: PowerSource.UsbC,
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        journal.Journal = pendingRestore;
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        var update = await context.Runtime.UpdateProfilesAsync(profiles with
        {
            UsbC = profiles.UsbC with { Performance = PerformancePreset.Performance },
        }, CancellationToken.None);
        Assert.True(update.Status == ControlOperationStatus.Stored, System.Text.Json.JsonSerializer.Serialize(update));

        await context.Runtime.ObserveAsync(CancellationToken.None);
        if (deferred)
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }
        else
        {
            Assert.Equal(ControlOperationStatus.Applied,
                (await context.Runtime.RecoverHyperBoostRestoreAsync(CancellationToken.None)).Status);
        }

        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
        Assert.Null(journal.Journal);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }


    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExternalPower_HyperBoostSourceAbaNeverRevivesExpiredTemporaryIntent(
        bool restart, bool unavailableBeforeBoundary)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = CreateContext(true, hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.UpdateProfilesAsync(profiles with
            {
                PluggedIn = profiles.PluggedIn with { Performance = PerformancePreset.Performance },
            }, CancellationToken.None)).Status);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetPerformanceOverrideAsync(new ManualOverride<PerformancePreset>(
                PerformancePreset.Silent, OverrideLifetime.UntilPowerSourceChange), CancellationToken.None)).Status);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None)).Status);
        Assert.Equal(PerformancePreset.Silent, journal.Journal?.RestoreIntent.Performance);

        if (unavailableBeforeBoundary)
        {
            int writes = context.Device.TotalSetterCalls;
            context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
            context.Device.AdapterPowerState = AdapterPowerState.Unknown;
            await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);
            Assert.Equal(PowerSource.PluggedIn, context.Runtime.GetStatus().ActivePowerSource);
            Assert.Equal(DeviceObservationReason.RetainedPowerSource, context.Runtime.GetStatus().ObservationReason);
            Assert.False(journal.Journal!.RestoreUsesCurrentPowerDestination);
            Assert.Equal(writes, context.Device.TotalSetterCalls);
            context.Device.AdapterPowerState = AdapterPowerState.FullPower280W;
        }

        context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.True(journal.Journal?.RestoreUsesCurrentPowerDestination);
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.True(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(PerformancePreset.Performance, context.Runtime.GetStatus().DesiredState.Performance.Value);
        Assert.Null(context.Runtime.GetStatus().PerformanceOverride);

        if (restart)
        {
            BatteryConfiguration configuration = await context.Store.LoadAsync(CancellationToken.None);
            context = CreateContext(true, initialize: false,
                hyperBoostOwnershipJournalStore: journal);
            ConfigurePadlessHyperBoost(context, active: true);
            context.Runtime.Initialize(configuration, PowerSource.PluggedIn,
                PersistentOverrideState.Empty, recoveringUnexpectedExit: true);
            await context.Runtime.ObserveAsync(CancellationToken.None);
            await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        }
        else
        {
            Assert.Equal(ControlOperationStatus.Applied,
                (await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None)).Status);
        }

        Assert.Equal(PowerSource.PluggedIn, context.Runtime.GetStatus().ActivePowerSource);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
        Assert.Equal(PerformancePreset.Performance, context.Runtime.GetStatus().DesiredState.Performance.Value);
        Assert.Null(journal.Journal);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalPower_PendingHyperBoostRestoreRetiresEntryIntentAcrossAba(bool restart)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore
        {
            Journal = CreateRestorePendingJournal(FanControlSettings.Default,
                new HyperBoostRecoveryBaseline(PerformancePreset.Balanced,
                    FanMode.Automatic, HyperBoostActive: false)) with
            {
                RestoreIntent = new HyperBoostRestoreIntent(PerformancePreset.Silent,
                    null, null, FanControlSettings.Default),
            },
        };
        var pendingRestore = journal.Journal;
        journal.Journal = null;
        TestContext context = CreateContext(true, hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        journal.Journal = pendingRestore;
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.UpdateProfilesAsync(profiles with
            {
                PluggedIn = profiles.PluggedIn with { Performance = PerformancePreset.Performance },
            }, CancellationToken.None)).Status);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().HyperBoostRestorePending);
        int settersBeforeBoundaries = context.Device.TotalSetterCalls;

        context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.True(journal.Journal?.RestoreUsesCurrentPowerDestination);
        Assert.Equal(settersBeforeBoundaries, context.Device.TotalSetterCalls);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        if (restart)
        {
            BatteryConfiguration configuration = await context.Store.LoadAsync(CancellationToken.None);
            context = CreateContext(true, initialize: false, hyperBoostOwnershipJournalStore: journal);
            ConfigurePadlessHyperBoost(context);
            context.Runtime.Initialize(configuration, PowerSource.PluggedIn, PersistentOverrideState.Empty);
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.RecoverHyperBoostRestoreAsync(CancellationToken.None)).Status);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
        Assert.Equal(0, context.Device.HyperBoostSetCalls);
        Assert.Null(journal.Journal);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.UsbC)]
    public async Task ExternalPower_PerformanceShortcutUsesSelectedSourceCustomLevels(PowerSource source)
    {
        TestContext context = CreateContext(true, initialPowerSource: source);
        var acLevels = CustomPerformanceSettings.Default;
        var usbLevels = new CustomPerformanceSettings(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            PerformancePresets = new HashSet<PerformancePreset>
            {
                PerformancePreset.Balanced, PerformancePreset.Performance, PerformancePreset.Custom,
            },
            CustomPerformance = new CustomPerformanceCapabilities(new HashSet<CustomPerformanceCombination>
            {
                new(acLevels.Cpu, acLevels.Gpu), new(usbLevels.Cpu, usbLevels.Gpu),
            }),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                Performance = PerformancePreset.Performance,
                CustomPerformance = acLevels,
            },
            UsbC = profiles.UsbC with
            {
                Performance = PerformancePreset.Performance,
                CustomPerformance = usbLevels,
            },
        }, CancellationToken.None);
        Assert.True(stored.Status == ControlOperationStatus.Stored, stored.Message);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult result = await context.Runtime.CyclePerformanceShortcutAsync(
            PerformanceShortcutSource.FunctionP, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(source, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(PerformancePreset.Custom, context.Device.ConfirmedState.Performance);
        Assert.Equal(source == PowerSource.UsbC ? usbLevels : acLevels,
            context.Device.ConfirmedState.CustomPerformance);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.BatterySaver)]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.BatteryBalanced)]
    [InlineData(PowerSource.UsbC, PerformancePreset.BatterySaver)]
    [InlineData(PowerSource.UsbC, PerformancePreset.BatteryBalanced)]
    public async Task ExternalPower_AdmitsModelSupportedBatteryPresetOverride(PowerSource source, PerformancePreset preset)
    {
        TestContext context = CreateWritableContext(initialPowerSource: source);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(preset, OverrideLifetime.Persistent), CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(source, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(preset, context.Device.ConfirmedState.Performance);
        Assert.Equal(preset, context.Runtime.GetStatus().PerformanceOverride?.Value);
    }


    [Fact]
    public async Task ExternalPower_PendingAbaCannotRestoreWhileBoundaryMarkerFails()
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore
        {
            Journal = CreateRestorePendingJournal(FanControlSettings.Default,
                new HyperBoostRecoveryBaseline(PerformancePreset.Balanced,
                    FanMode.Automatic, HyperBoostActive: false)) with
            {
                RestoreIntent = new HyperBoostRestoreIntent(PerformancePreset.Silent,
                    null, null, FanControlSettings.Default),
            },
        };
        var pendingRestore = journal.Journal;
        journal.Journal = null;
        TestContext context = CreateContext(true, hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        journal.Journal = pendingRestore;
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.UpdateProfilesAsync(profiles with
            {
                PluggedIn = profiles.PluggedIn with { Performance = PerformancePreset.Performance },
            }, CancellationToken.None)).Status);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int settersBeforeBoundary = context.Device.TotalSetterCalls;
        journal.FailWrite = true;
        context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.RecoverHyperBoostRestoreAsync(CancellationToken.None)).Status);
        Assert.Equal(settersBeforeBoundary, context.Device.TotalSetterCalls);
        Assert.False(journal.Journal?.RestoreUsesCurrentPowerDestination);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);

        journal.FailWrite = false;
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.RecoverHyperBoostRestoreAsync(CancellationToken.None)).Status);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
        Assert.Null(journal.Journal);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }


    [Theory]
    [InlineData("suspend")]
    [InlineData("shutdown")]
    public async Task ExternalPower_UnclassifiedLifecyclePreservesOwnedJournal(string boundary)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = CreateContext(true, hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None)).Status);
        HyperBoostOwnershipJournal? owned = journal.Journal;
        Assert.NotNull(owned);
        int journalWrites = journal.Writes.Count;
        int hyperBoostWrites = context.Device.HyperBoostSetCalls;
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;

        if (boundary == "shutdown")
        {
            await context.Runtime.PrepareForShutdownAsync(CancellationToken.None);
        }
        else
        {
            await context.Runtime.PrepareForLowPowerAsync(CancellationToken.None);
        }

        Assert.Equal(owned, journal.Journal);
        Assert.True(journal.Journal?.IsWellFormed);
        Assert.Equal(journalWrites, journal.Writes.Count);
        Assert.Equal(hyperBoostWrites, context.Device.HyperBoostSetCalls);
        Assert.True(context.Device.ConfirmedState.HyperBoostActive);
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    public async Task ExternalPower_LegacyPendingRestoreRebasesBeforeAnySetter(PerformancePreset observed)
    {
        string path = Path.Combine(directory, "legacy-destination-hyperboost.json");
        var store = new HyperBoostOwnershipJournalStore(path);
        TestContext context = CreateContext(true, hyperBoostOwnershipJournalStore: store);
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        var saved = await context.Runtime.UpdateProfilesAsync(profiles with
        {
            PluggedIn = profiles.PluggedIn with { Performance = PerformancePreset.Performance },
        }, CancellationToken.None);
        Assert.True(saved.Status == ControlOperationStatus.Stored, saved.Message);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = observed,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        var legacy = new HyperBoostOwnershipJournalDocument(7,
            HyperBoostOwnershipJournalState.RestorePending, PerformancePreset.Balanced,
            DateTimeOffset.UtcNow, RequiresCoolingPad: false,
            RestorePerformance: PerformancePreset.BatteryBalanced,
            RestoreFanControl: FanControlSettings.Default,
            RecoveryBaseline: new HyperBoostRecoveryBaseline(observed, FanMode.Automatic, HyperBoostActive: false));
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(legacy,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().HyperBoostRestorePending);
        Assert.NotNull((await store.LoadAsync(CancellationToken.None)).Journal);
        int writesBefore = context.Device.PerformanceWrites.Count;
        bool durableBeforeWrite = false;
        context.Device.AfterSetter = _ =>
        {
            if (context.Device.PerformanceWrites.Count <= writesBefore)
            {
                return;
            }
            HyperBoostOwnershipJournalLoadResult pending = store.LoadAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.False(pending.Invalid);
            Assert.Equal(HyperBoostOwnershipJournalState.RestorePending, pending.Journal?.State);
            Assert.Equal(PerformancePreset.Performance, pending.Journal?.RestoreIntent.Performance);
            Assert.Equal(PowerSource.PluggedIn, pending.Journal?.RestorePowerSource);
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(8, document.RootElement.GetProperty("schemaVersion").GetInt32());
            durableBeforeWrite = true;
        };

        ControlOperationResult restored = await context.Runtime.RecoverHyperBoostRestoreAsync(CancellationToken.None);

        Assert.True(restored.Status == ControlOperationStatus.Applied, restored.Message);
        Assert.True(durableBeforeWrite);
        Assert.All(context.Device.PerformanceWrites.Skip(writesBefore),
            performance => Assert.Equal(PerformancePreset.Performance, performance));
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
        Assert.Equal(PerformancePreset.Performance, context.Runtime.GetStatus().DesiredState.Performance.Value);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Null((await store.LoadAsync(CancellationToken.None)).Journal);
    }
}
