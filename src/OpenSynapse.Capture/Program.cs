using OpenSynapse.Protocol;

if (args.Length == 1 && args[0].Equals("target", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"Model: {TargetBladeDevice.Model}");
    Console.WriteLine($"USB: {TargetBladeDevice.VendorId:X4}:{TargetBladeDevice.ProductId:X4}");
    Console.WriteLine($"BIOS: {TargetBladeDevice.BiosVersion}");
    return 0;
}

if (args.Length == 2 && args[0].Equals("validate", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        await CapturedFixtureFile.ValidateCapturedFixtureAsync(args[1], CancellationToken.None);
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

if (args.Length == 1 && args[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
{
    IReadOnlyList<HidInterfaceInfo> interfaces = WindowsHidEnumerator.FindTargetInterfaces();
    Console.WriteLine($"Found {interfaces.Count} HID interface(s) for 1532:02E0.");
    foreach (HidInterfaceInfo device in interfaces)
    {
        Console.WriteLine(
            $"Usage {device.UsagePage:X4}:{device.Usage:X4}, feature bytes {device.FeatureReportByteLength}: {device.DevicePath}");
    }

    return interfaces.Count > 0 ? 0 : 3;
}

Console.WriteLine("OpenSynapse capture utility (no raw hardware command support)");
Console.WriteLine("  OpenSynapse.Capture target");
Console.WriteLine("  OpenSynapse.Capture scan");
Console.WriteLine("  OpenSynapse.Capture validate <fixtures.json>");
Console.WriteLine("See captures/README.md for the gated USBPcap workflow.");
return args.Length == 0 ? 0 : 1;
