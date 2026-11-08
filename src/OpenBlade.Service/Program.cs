using System.Diagnostics;
using OpenBlade.Contracts;
using OpenBlade.Core.Native;
using OpenBlade.Protocol;
using OpenBlade.Service;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "OpenBlade Service");
try { ProductNativeRuntime.EnsureAvailable(); }
catch (NativeComponentException exception)
{
    // Build only the logging/lifetime host on failure: no hardware providers,
    // runtime, worker or power/session monitors have been registered yet.
    using IHost failedHost = builder.Build();
    failedHost.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenBlade.Native")
        .LogCritical("{NativeComponentFailure}", exception.Message);
    Environment.ExitCode = 1;
    return;
}
string machineDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "OpenBlade");
builder.Services.AddSingleton(provider => new BatteryConfigurationStore(
    Path.Combine(machineDataDirectory, "profiles.json"),
    Path.Combine(machineDataDirectory, "battery-preferences.json"),
    Path.Combine(machineDataDirectory, "battery-configuration.commit.json"),
    provider.GetRequiredService<ProfileSetValidator>()));
builder.Services.AddSingleton(new PersistentOverrideStore(
    Path.Combine(machineDataDirectory, "manual-overrides.json")));
builder.Services.AddSingleton(new ServiceRunMarkerStore(
    Path.Combine(machineDataDirectory, "service-running.json")));
builder.Services.AddSingleton<IUserConfigurationRepository>(provider =>
    new UserConfigurationRepository(
        machineDataDirectory,
        provider.GetRequiredService<ProfileSetValidator>()));
builder.Services.AddSingleton<MachineProfileBootstrapper>();
builder.Services.AddSingleton<IRunningProcessSource, RunningProcessSource>();
builder.Services.AddSingleton<IWindowsServiceSource, WindowsServiceSource>();
builder.Services.AddSingleton<IControllerPresenceMonitor, WindowsControllerPresenceMonitor>();
builder.Services.AddSingleton<IHardwareAccessGuard>(provider =>
    new WindowsHardwareAccessGuard(
        provider.GetRequiredService<IControllerPresenceMonitor>()));
builder.Services.AddWindowsPowerState();
builder.Services.AddMultiUserSessionSupport();
builder.Services.AddVerifiedBladeDeviceProvider();
builder.Services.AddDiagnostics();
builder.Services.AddSingleton<IStatusSubscriptionHub>(
    _ => new StatusSubscriptionHub(TimeProvider.System));
builder.Services.AddSingleton<LowPowerGate>();
builder.Services.AddSingleton<IServiceLifecycleEventSource, WindowsServiceLifecycleEventSource>();
builder.Services.AddSingleton<IDisplayBrightnessHidController, DisplayBrightnessHidController>();
builder.Services.AddServiceControlRuntime(machineDataDirectory);
builder.Services.AddSingleton<IServiceShutdownSignal, HostServiceShutdownSignal>();
builder.Services.AddSingleton<ControlRequestHandler>();
builder.Services.AddHostedService<Worker>();

using IHost host = builder.Build();
var diagnosticTraceListener = new DiagnosticHistoryTraceListener(
    host.Services.GetRequiredService<DiagnosticHistory>());
Trace.Listeners.Add(diagnosticTraceListener);
try
{
    host.Run();
}
catch (NativeComponentException exception)
{
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenBlade.Native")
        .LogCritical("{NativeComponentFailure}", exception.Message);
    Environment.ExitCode = 1;
}
finally
{
    Trace.Listeners.Remove(diagnosticTraceListener);
    diagnosticTraceListener.Dispose();
}
