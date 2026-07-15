using OpenBlade.Capture;
using OpenBlade.Protocol;

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

if (args.Length > 0 && args[0].Equals("probe-battery", StringComparison.OrdinalIgnoreCase))
{
    var command = new BatteryProbeCommand(BatteryValidationFactory.ProbeWindows, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals("query-battery", StringComparison.OrdinalIgnoreCase))
{
    var session = new BatteryQuerySession(BatteryValidationFactory.CreateWindows());
    var command = new BatteryQueryCommand(session.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals("validate-battery", StringComparison.OrdinalIgnoreCase))
{
    var approval = new ConsoleBatteryApproval(Console.In, Console.Out);
    var session = new BatteryValidationSession(
        BatteryValidationFactory.CreateWindows(),
        approval,
        TimeSpan.FromSeconds(5));
    var command = new BatteryValidationCommand(session.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

Console.WriteLine("OpenBlade capture utility (no raw hardware command support)");
Console.WriteLine("  OpenBlade.Capture target");
Console.WriteLine("  OpenBlade.Capture scan");
Console.WriteLine("  OpenBlade.Capture validate <fixtures.json>");
Console.WriteLine("  OpenBlade.Capture probe-battery");
Console.WriteLine("  OpenBlade.Capture query-battery");
Console.WriteLine("  OpenBlade.Capture validate-battery <off|50|55|60|65|70|75|80>");
Console.WriteLine("See captures/README.md for the gated USBPcap workflow.");
return args.Length == 0 ? 0 : 1;
