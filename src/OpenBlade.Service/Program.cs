using System.Diagnostics;
using Microsoft.Extensions.Hosting.WindowsServices;
using OpenBlade.Contracts;
using OpenBlade.Core.Native;
using OpenBlade.Protocol;
using OpenBlade.Service;

using var startupTiming = new ServiceStartupTiming(Stopwatch.GetTimestamp(), WriteStartupTiming);
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "OpenBlade Service");
startupTiming.NativeCheckStartQpc = Stopwatch.GetTimestamp();
try
{
    ProductNativeRuntime.EnsureAvailable();
    startupTiming.NativeCheckEndQpc = Stopwatch.GetTimestamp();
}
catch (NativeComponentException exception)
{
    startupTiming.NativeCheckEndQpc = Stopwatch.GetTimestamp();
    startupTiming.HostTerminal = "native-check-failed";
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
builder.Services.AddSingleton(startupTiming);
builder.Services.AddHostedService<Worker>();

startupTiming.HostBuildStartQpc = Stopwatch.GetTimestamp();
using IHost host = builder.Build();
startupTiming.HostBuildEndQpc = Stopwatch.GetTimestamp();
var diagnosticTraceListener = new DiagnosticHistoryTraceListener(
    host.Services.GetRequiredService<DiagnosticHistory>());
Trace.Listeners.Add(diagnosticTraceListener);
try
{
    startupTiming.HostRunStartQpc = Stopwatch.GetTimestamp();
    host.Run();
    startupTiming.HostTerminal = "host-exited";
}
catch (NativeComponentException exception)
{
    startupTiming.HostTerminal = "host-native-failed";
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenBlade.Native")
        .LogCritical("{NativeComponentFailure}", exception.Message);
    Environment.ExitCode = 1;
}
finally
{
    Trace.Listeners.Remove(diagnosticTraceListener);
    diagnosticTraceListener.Dispose();
}

static void WriteStartupTiming(string message)
{
    if (WindowsServiceHelpers.IsWindowsService())
    {
        // Keep startup diagnostics independent of host initialization, disposal
        // and Event Log logger filters. The SCM service runs as LocalSystem.
        EventLog.WriteEntry("OpenBlade.Service", message, EventLogEntryType.Information, 4901);
    }
}
