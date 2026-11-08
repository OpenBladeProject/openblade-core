using OpenBlade.Core.Native;
using OpenBlade.FirmwareSmoke;

try { ProductNativeRuntime.EnsureAvailable(); }
catch (NativeComponentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

return await FirmwareSmokeProgram.RunAsync(args, CancellationToken.None);
