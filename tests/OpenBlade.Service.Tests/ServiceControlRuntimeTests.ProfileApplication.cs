using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData("readmission", ControlOperationStatus.Failed)]
    [InlineData("uncertain", ControlOperationStatus.Failed)]
    [InlineData("preflight", ControlOperationStatus.Blocked)]
    public async Task ProfileTransition_HyperBoostBoundaryRetainsSubmissionFacts(string failure, ControlOperationStatus expected)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = await CreateOwnedHyperBoostGpuContextAsync(journal);
        using ServiceControlRuntime runtime = context.Runtime;
        int hyperBoostWrites = context.Device.HyperBoostSetCalls;
        if (failure == "readmission")
        {
            journal.BeforeConditionalClear = () => context.Provider.Available = false;
        }
        else if (failure == "uncertain")
        {
            context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
                WriteSubmissionState.Indeterminate, "The boundary command may have been submitted."));
        }
        else
        {
            context.Provider.Available = false;
        }
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        ControlOperationResult result = await runtime.SetFullChargeOverrideAsync(false, CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Equal(hyperBoostWrites + (failure == "preflight" ? 0 : 1), context.Device.HyperBoostSetCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationProfile_AttemptedLaptopWriteIsFailedRatherThanRefused(bool earlierConfirmedWrite)
    {
        TestContext context = await CreateApplicationProfileContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        ApplicationProfileDefinition application = Assert.Single(runtime.GetStatus().Profiles.ApplicationProfiles);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.FunctionKeyPrimary, OneShotLaptopControl.GamingMode,
            },
        };
        context.Device.FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys;
        await runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = runtime.GetStatus().Profiles;
        await runtime.UpdateProfilesAsync(profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                LaptopBehavior = earlierConfirmedWrite ? new(null, FunctionKeyPrimaryMode.FunctionKeys, null) : null,
            },
            ApplicationProfiles = [application with
            {
                Overrides = application.Overrides with
                {
                    PluggedIn = application.Overrides.PluggedIn! with { GamingModeEnabled = true },
                },
            }],
        }, CancellationToken.None);
        context.Device.AfterOneShotSet = requested =>
        {
            if (requested.Control == OneShotLaptopControl.GamingMode)
            {
                if (earlierConfirmedWrite)
                {
                    context.Device.OneShotFailure = new BladeWriteException(WriteSubmissionState.KnownNotApplied, "Not submitted");
                }
                else
                {
                    context.Device.GamingModeEnabled = false;
                }
            }
        };

        ControlOperationResult result = await ReportTestApplicationAsync(context, application);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.False(runtime.GetStatus().WritesBlocked);
        if (earlierConfirmedWrite)
        {
            Assert.Equal(FunctionKeyPrimaryMode.FunctionKeys, runtime.GetStatus().FunctionKeyPrimary);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationProfile_SuppressionRemovalRestoresLightingAndAppliesRemainingIntent(bool activeApplication)
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl> { OneShotLaptopControl.GamingMode },
        };
        context.Device.GamingModeEnabled = false;
        await runtime.ObserveAsync(CancellationToken.None);
        var application = new ApplicationProfileDefinition(Guid.NewGuid(), "Game", @"C:\Games\suppression-test.exe",
            ApplicationActivationMode.Foreground, new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride { GamingModeEnabled = false },
            });
        ProfileSet initial = runtime.GetStatus().Profiles;
        await runtime.UpdateProfilesAsync(initial with
        {
            PluggedIn = initial.PluggedIn with { LightingPowerPolicy = new(true, null) },
            ApplicationProfiles = [application],
        }, CancellationToken.None);
        if (activeApplication)
        {
            await ReportTestApplicationAsync(context, application);
        }
        await runtime.SetLightingSuppressionAsync(LightingSuppressionReason.DisplayOff, true, CancellationToken.None);
        int lightingBefore = context.Device.LightingSetCalls;
        int laptopBefore = context.Device.OneShotSetCalls;
        ProfileSet current = runtime.GetStatus().Profiles;
        ProfileSet changed = current with
        {
            PluggedIn = current.PluggedIn with { LightingPowerPolicy = new(false, null) },
            ApplicationProfiles = [application with
            {
                Overrides = application.Overrides with
                {
                    PluggedIn = application.Overrides.PluggedIn! with { GamingModeEnabled = true },
                },
            }],
        };

        await runtime.UpdateProfilesAsync(changed, CancellationToken.None);

        Assert.Equal(changed, (await context.Store.LoadAsync(CancellationToken.None)).Profiles);
        Assert.Empty(runtime.GetStatus().LightingSuppressions ?? []);
        Assert.Equal(lightingBefore + 1, context.Device.LightingSetCalls);
        Assert.Equal(runtime.GetStatus().DesiredState.Lighting.Value, context.Device.ConfirmedState.Lighting);
        Assert.Equal(laptopBefore + (activeApplication ? 1 : 0), context.Device.OneShotSetCalls);
        Assert.Equal(activeApplication, context.Device.GamingModeEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationProfile_AdmissionFailureDoesNotReportApplied(bool readFails)
    {
        TestContext context = await CreateApplicationProfileContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        ApplicationProfileDefinition application = Assert.Single(runtime.GetStatus().Profiles.ApplicationProfiles);
        context.Provider.Available = readFails;
        context.Device.ReadStateFailure = readFails ? new IOException("Initial state is unavailable.") : null;
        int settersBefore = context.Device.TotalSetterCalls;

        ControlOperationResult result = await ReportTestApplicationAsync(context, application);

        Assert.NotEqual(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(application.Id, runtime.GetStatus().ActiveApplicationProfileId);
        Assert.Equal(settersBefore, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task ApplicationProfile_EditPersistsWithoutClaimingFailedApplication()
    {
        TestContext context = await CreateApplicationProfileContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        ApplicationProfileDefinition application = Assert.Single(runtime.GetStatus().Profiles.ApplicationProfiles);
        await ReportTestApplicationAsync(context, application);
        ProfileSet updated = runtime.GetStatus().Profiles with
        {
            ApplicationProfiles =
            [
                application with
                {
                    Overrides = application.Overrides with
                    {
                        PluggedIn = application.Overrides.PluggedIn! with
                        {
                            Performance = PerformancePreset.Silent,
                        },
                    },
                },
            ],
        };
        context.Provider.Available = false;
        int settersBefore = context.Device.TotalSetterCalls;

        ControlOperationResult result = await runtime.UpdateProfilesAsync(updated, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(updated, runtime.GetStatus().Profiles);
        Assert.Equal(updated, (await context.Store.LoadAsync(CancellationToken.None)).Profiles);
        Assert.Equal(settersBefore, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task ApplicationProfile_GamingOnlyEditReconcilesFirmware()
    {
        TestContext context = await CreateApplicationProfileContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        ApplicationProfileDefinition application = Assert.Single(runtime.GetStatus().Profiles.ApplicationProfiles);
        await ReportTestApplicationAsync(context, application);
        int priorOneShotWrites = context.Device.OneShotSetCalls;
        ProfileSet updated = runtime.GetStatus().Profiles with
        {
            ApplicationProfiles =
            [
                application with
                {
                    Overrides = application.Overrides with
                    {
                        PluggedIn = application.Overrides.PluggedIn! with
                        {
                            GamingModeEnabled = true,
                        },
                    },
                },
            ],
        };

        await runtime.UpdateProfilesAsync(updated, CancellationToken.None);

        Assert.Equal(priorOneShotWrites + 1, context.Device.OneShotSetCalls);
        Assert.True(context.Device.GamingModeEnabled);
        Assert.True(runtime.GetStatus().GamingModeEnabled);
        Assert.Equal(updated, (await context.Store.LoadAsync(CancellationToken.None)).Profiles);
    }

    [Fact]
    public async Task ProfileLaptopControls_InitialStateFailureDoesNotSubmitControls()
    {
        TestContext context = await CreateLaptopBehaviorTransitionContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.ReadStateFailure = new IOException("Initial state is unavailable.");
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.OneShotSetCalls);
        Assert.False(context.Device.StartupAnimationEnabled);
        Assert.False(context.Device.GamingModeEnabled);
    }

    [Theory]
    [InlineData("indeterminate")]
    [InlineData("unknown-readback")]
    [InlineData("failed-readback")]
    public async Task ProfileLaptopControls_UncertainFirstControlStopsRemainingWrites(string failure)
    {
        TestContext context = await CreateLaptopBehaviorTransitionContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        if (failure == "indeterminate")
        {
            context.Device.OneShotFailure = new BladeWriteException(
                WriteSubmissionState.Indeterminate, "The setter may have reached firmware.");
        }
        else
        {
            context.Device.AfterOneShotSet = _ =>
            {
                if (failure == "unknown-readback")
                {
                    context.Device.FunctionKeyPrimary = null;
                }
                else
                {
                    context.Device.FunctionKeyReadFailure = new IOException("Readback is unavailable.");
                }
            };
        }
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.OneShotSetCalls);
        Assert.Equal(OneShotLaptopControl.FunctionKeyPrimary, context.Device.LastOneShotLaptopControl!.Control);
        Assert.False(context.Device.GamingModeEnabled);
        Assert.False(context.Device.StartupAnimationEnabled);
        Assert.True(runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task LaptopControl_UnconfirmedReadbackBlocksNextControlRequest()
    {
        TestContext context = await CreateLaptopBehaviorTransitionContextAsync();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.AfterOneShotSet = _ => context.Device.FunctionKeyPrimary = null;

        ControlOperationResult first = await runtime.ApplyOneShotLaptopControlAsync(
            new ApplyOneShotLaptopControl(OneShotLaptopControl.FunctionKeyPrimary,
                FunctionKeyMode: FunctionKeyPrimaryMode.FunctionKeys),
            CancellationToken.None);
        ControlOperationResult second = await runtime.ApplyOneShotLaptopControlAsync(
            new ApplyOneShotLaptopControl(OneShotLaptopControl.GamingMode, Enabled: true),
            CancellationToken.None);

        Assert.NotEqual(ControlOperationStatus.Applied, first.Status);
        Assert.Equal(ControlOperationStatus.Blocked, second.Status);
        Assert.Equal(1, context.Device.OneShotSetCalls);
        Assert.False(context.Device.GamingModeEnabled);
        Assert.True(runtime.GetStatus().WritesBlocked);
    }

    private async Task<TestContext> CreateApplicationProfileContextAsync()
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl> { OneShotLaptopControl.GamingMode },
        };
        context.Device.GamingModeEnabled = false;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var application = new ApplicationProfileDefinition(
            Guid.NewGuid(), "Game", @"C:\Games\profile-test.exe", ApplicationActivationMode.Foreground,
            new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride
                {
                    Performance = PerformancePreset.Balanced,
                    GamingModeEnabled = false,
                },
            });
        await context.Runtime.UpdateProfilesAsync(
            context.Runtime.GetStatus().Profiles with { ApplicationProfiles = [application] },
            CancellationToken.None);
        return context;
    }

    private static Task<ControlOperationResult> ReportTestApplicationAsync(
        TestContext context, ApplicationProfileDefinition application) =>
        context.Runtime.ReportApplicationContextAsync(
            new ReportApplicationContextRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
                Guid.NewGuid(), 1, application.ExecutablePath),
            static () => true, CancellationToken.None);

    private async Task<TestContext> CreateLaptopBehaviorTransitionContextAsync()
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.FunctionKeyPrimary,
                OneShotLaptopControl.GamingMode,
                OneShotLaptopControl.StartupAnimation,
            },
        };
        context.Device.FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys;
        context.Device.GamingModeEnabled = false;
        context.Device.StartupAnimationEnabled = false;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        await context.Runtime.UpdateProfilesAsync(profiles with
        {
            Battery = profiles.Battery with
            {
                LaptopBehavior = new LaptopBehaviorProfileSettings(true, FunctionKeyPrimaryMode.FunctionKeys, true),
            },
        }, CancellationToken.None);
        return context;
    }
}
