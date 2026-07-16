using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
string machineDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "OpenBlade");
builder.Services.AddWindowsService(options => options.ServiceName = "OpenBlade Service");
builder.Services.AddSingleton(new BatteryConfigurationStore(
    Path.Combine(machineDataDirectory, "profiles.json"),
    Path.Combine(machineDataDirectory, "battery-preferences.json"),
    Path.Combine(machineDataDirectory, "battery-configuration.commit.json")));
builder.Services.AddSingleton<MachineProfileBootstrapper>();
builder.Services.AddSingleton<IRunningProcessSource, RunningProcessSource>();
builder.Services.AddSingleton<IWindowsServiceSource, WindowsServiceSource>();
builder.Services.AddSingleton<IControllerPresenceMonitor, WindowsControllerPresenceMonitor>();
builder.Services.AddSingleton<IHardwareAccessGuard>(provider =>
    new WindowsHardwareAccessGuard(
        provider.GetRequiredService<IControllerPresenceMonitor>()));
builder.Services.AddWindowsTelemetry();
builder.Services.AddVerifiedBladeDeviceProvider();
builder.Services.AddDiagnostics();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IStatusSubscriptionHub>(
    _ => new StatusSubscriptionHub(TimeProvider.System));
builder.Services.AddSingleton<IServiceLifecycleEventSource, WindowsServiceLifecycleEventSource>();
builder.Services.AddSingleton<IServiceControlRuntime, ServiceControlRuntime>();
builder.Services.AddSingleton<ControlRequestHandler>();
builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();
host.Run();
