using OpenSynapse.Service;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "OpenSynapse Service");
builder.Services.AddSingleton<IRunningProcessSource, RunningProcessSource>();
builder.Services.AddSingleton<IConflictDetector, WindowsConflictDetector>();
builder.Services.AddSingleton<StatusRequestHandler>();
builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();
host.Run();
