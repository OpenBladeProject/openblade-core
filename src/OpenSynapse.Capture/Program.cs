using OpenSynapse.Protocol;

if (args.Length == 1 && args[0].Equals("target", StringComparison.OrdinalIgnoreCase))
{
    DeviceIdentity target = CapturedFixtureFile.TargetDevice;
    Console.WriteLine($"Model: {target.Model}");
    Console.WriteLine($"USB: {target.VendorId:X4}:{target.ProductId:X4}");
    Console.WriteLine($"BIOS: {target.BiosVersion}");
    return 0;
}

if (args.Length == 2 && args[0].Equals("validate", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        _ = await CapturedFixtureFile.LoadCatalogAsync(args[1], CancellationToken.None);
        Console.WriteLine("Fixture file passed exact-device and five-gate validation.");
        return 0;
    }
    catch (Exception exception) when (
        exception is IOException
        or System.Text.Json.JsonException
        or UnvalidatedCommandException)
    {
        Console.Error.WriteLine($"Fixture rejected: {exception.Message}");
        return 2;
    }
}

Console.WriteLine("OpenSynapse capture utility (no raw hardware command support)");
Console.WriteLine("  OpenSynapse.Capture target");
Console.WriteLine("  OpenSynapse.Capture validate <fixtures.json>");
Console.WriteLine("See captures/README.md for the gated USBPcap workflow.");
return args.Length == 0 ? 0 : 1;
