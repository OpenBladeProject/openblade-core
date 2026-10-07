using OpenBlade.DeploymentBroker;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
    options.ServiceName = "OpenBlade Local Dev Deployment Broker");
builder.Services.AddSingleton(BrokerPaths.CreateDefault());
builder.Services.AddSingleton<BrokerEnrollmentStore>();
builder.Services.AddSingleton<IActiveConsoleSessionSource, WindowsActiveConsoleSessionSource>();
builder.Services.AddSingleton<IBrokerClientContextResolver, WindowsBrokerClientContextResolver>();
builder.Services.AddSingleton<IFixedLocalDevInboxSource, FixedLocalDevInboxSource>();
builder.Services.AddSingleton<IBrokerAcceptedSequenceStore, BrokerAcceptedSequenceStore>();
builder.Services.AddSingleton<BrokerDurableRecordStore>();
builder.Services.AddSingleton<BrokerInFlightStore>();
builder.Services.AddSingleton<IBrokerServiceHealthProbe, WindowsBrokerServiceHealthProbe>();
builder.Services.AddSingleton<IBrokerInstalledStateVerifier, WindowsBrokerInstalledStateVerifier>();
builder.Services.AddSingleton<IBrokerInstallerProcessRunner, WindowsBrokerInstallerProcessRunner>();
builder.Services.AddSingleton<IBrokerRecordedInstallerProcessInspector, WindowsBrokerRecordedInstallerProcessInspector>();
builder.Services.AddSingleton<IBrokerStartupReconciler, BrokerStartupReconciler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IBrokerRequestHandler, ApplyFixedInboxBrokerRequestHandler>();
builder.Services.AddSingleton<IBrokerCpuVoltageDeployment, WindowsCpuVoltageDeployment>();
builder.Services.AddHostedService<Worker>();

using IHost host = builder.Build();
host.Run();
