using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
string machineDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "OpenBlade");
builder.Services.AddWindowsService(options => options.ServiceName = "OpenBlade Service");
builder.Services.AddSingleton(new ProfileStore(Path.Combine(machineDataDirectory, "profiles.json")));
builder.Services.AddSingleton(ProfileDefaults.CreateSafeDefaults());
builder.Services.AddSingleton<MachineProfileBootstrapper>();
builder.Services.AddSingleton<IRunningProcessSource, RunningProcessSource>();
builder.Services.AddSingleton<IWindowsServiceSource, WindowsServiceSource>();
builder.Services.AddSingleton<IHardwareAccessGuard, WindowsHardwareAccessGuard>();
builder.Services.AddSingleton<IPowerTelemetryProvider, WindowsPowerTelemetryProvider>();
builder.Services.AddSingleton<IVerifiedBladeDeviceProvider, UnavailableBladeDeviceProvider>();
builder.Services.AddSingleton<IStatusSubscriptionHub>(
    _ => new StatusSubscriptionHub(TimeProvider.System));
builder.Services.AddSingleton<IServiceLifecycleEventSource, WindowsServiceLifecycleEventSource>();
builder.Services.AddSingleton<IServiceControlRuntime, ServiceControlRuntime>();
builder.Services.AddSingleton<ControlRequestHandler>();
builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();
host.Run();
