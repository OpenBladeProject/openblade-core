using OpenBlade.LocalDevDeploymentTool;

return await LocalDevDeploymentToolApplication.RunAsync(
    args,
    Console.Out,
    Console.Error,
    new WindowsPlatformCngSignerFactory(),
    CancellationToken.None);
