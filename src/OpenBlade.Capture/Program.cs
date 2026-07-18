using OpenBlade.Capture;
using OpenBlade.Display;
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

if (args.Length == 1 && args[0].Equals("scan-all", StringComparison.OrdinalIgnoreCase))
{
    IReadOnlyList<HidInterfaceInfo> interfaces = WindowsHidEnumerator.FindTargetCollections();
    Console.WriteLine($"Found {interfaces.Count} HID collection(s) for 1532:02E0.");
    foreach (HidInterfaceInfo device in interfaces)
    {
        Console.WriteLine(
            $"Usage {device.UsagePage:X4}:{device.Usage:X4}, feature bytes {device.FeatureReportByteLength}: {device.DevicePath}");
    }

    return interfaces.Count > 0 ? 0 : 3;
}

if (args.Length == 1 && args[0].Equals("query-display", StringComparison.OrdinalIgnoreCase))
{
    using var controller = new WindowsDisplaySettingsController();
    DisplaySettingsSnapshot state = await controller.QueryAsync();
    Console.WriteLine($"Available={state.IsAvailable}");
    Console.WriteLine($"Display={state.DisplayName ?? "Unavailable"}");
    Console.WriteLine($"Resolution={state.Width}x{state.Height}");
    Console.WriteLine($"Refresh={state.CurrentRefreshKey ?? "Unavailable"}");
    Console.WriteLine(
        $"RefreshOptions={string.Join(",", state.RefreshOptions.Select(option => option.Key))}");
    Console.WriteLine($"HdrSupported={state.HdrSupported}");
    Console.WriteLine($"HdrEnabled={state.HdrEnabled}");
    Console.WriteLine($"ActiveColorProfile={state.ActiveColorProfileId ?? "Unavailable"}");
    Console.WriteLine(
        $"ColorProfiles={string.Join(",", state.ColorProfiles.Select(profile => profile.Name))}");
    Console.WriteLine($"Message={state.Message ?? "None"}");
    return state.IsAvailable ? 0 : 3;
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

if (args.Length > 0 && args[0].Equals("query-temporary-full-charge", StringComparison.OrdinalIgnoreCase))
{
    var session = new TemporaryFullChargeQuerySession(
        BatteryValidationFactory.CreateWindowsTemporaryFullChargeQuery());
    var command = new TemporaryFullChargeQueryCommand(session.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals("query-fan-rpm", StringComparison.OrdinalIgnoreCase))
{
    var session = new FanRpmQuerySession(BatteryValidationFactory.CreateWindowsFanRpmQuery());
    var command = new FanRpmQueryCommand(session.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "query-lighting-brightness",
    StringComparison.OrdinalIgnoreCase))
{
    ILightingBrightnessQueryRunner runner =
        BatteryValidationFactory.CreateWindowsLightingBrightnessQuery();
    HidSessionResult<LightingBrightnessReading> result =
        await runner.RunAsync(CancellationToken.None);
    Console.WriteLine($"RawValue={result.Value?.RawValue.ToString() ?? "Unavailable"}");
    Console.WriteLine($"Percent={result.Value?.Percent.ToString() ?? "Unavailable"}");
    Console.WriteLine($"Failure={result.Failure?.ToString() ?? "None"}");
    Console.WriteLine($"FailurePhase={result.Diagnostic?.Phase.ToString() ?? "None"}");
    Console.WriteLine(
        $"ProtocolValidation={result.Diagnostic?.ProtocolValidationFailure?.ToString() ?? "None"}");
    Console.WriteLine($"Success={result.Failure is null && result.Value is not null}");
    return result.Failure is null && result.Value is not null ? 0 : 2;
}

if (args.Length > 0 && args[0].Equals(
    "query-performance-fan-discriminator",
    StringComparison.OrdinalIgnoreCase))
{
    IPerformanceFanDiscriminatorQueryRunner runner =
        BatteryValidationFactory.CreateWindowsPerformanceFanDiscriminatorQuery();
    var command = new PerformanceFanDiscriminatorQueryCommand(runner.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-performance-fan",
    StringComparison.OrdinalIgnoreCase))
{
    IPerformanceFanSetterValidationRunner runner =
        BatteryValidationFactory.CreateWindowsPerformanceFanSetterValidation();
    var command = new PerformanceFanSetterValidationCommand(runner.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-lighting",
    StringComparison.OrdinalIgnoreCase))
{
    var command = LightingValidationCommand.CreateWindows(Console.In, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals("probe-battery-sequence", StringComparison.OrdinalIgnoreCase))
{
    var session = new BatteryQuerySequenceSession(
        BatteryValidationFactory.CreateWindowsQuerySequence());
    var command = new BatteryQuerySequenceCommand(session.RunAsync, Console.Out);
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

if (args.Length > 0 && args[0].Equals("validate-protection-off", StringComparison.OrdinalIgnoreCase))
{
    var approval = new ConsoleBatteryApproval(Console.In, Console.Out);
    var session = new ProtectionOffValidationSession(
        BatteryValidationFactory.CreateWindowsProtectionOffValidation(),
        approval,
        TimeSpan.FromSeconds(5));
    var command = new ProtectionOffValidationCommand(session.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals("validate-temporary-full-charge", StringComparison.OrdinalIgnoreCase))
{
    var approval = new ConsoleTemporaryFullChargeApproval(Console.In, Console.Out);
    var session = new TemporaryFullChargeValidationSession(
        BatteryValidationFactory.CreateWindowsTemporaryFullChargeValidation(),
        approval,
        TimeSpan.FromSeconds(5));
    var command = new TemporaryFullChargeValidationCommand(session.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

Console.WriteLine("OpenBlade capture utility (no raw hardware command support)");
Console.WriteLine("  OpenBlade.Capture target");
Console.WriteLine("  OpenBlade.Capture scan");
Console.WriteLine("  OpenBlade.Capture scan-all");
Console.WriteLine("  OpenBlade.Capture query-display");
Console.WriteLine("  OpenBlade.Capture validate <fixtures.json>");
Console.WriteLine("  OpenBlade.Capture probe-battery");
Console.WriteLine("  OpenBlade.Capture query-battery");
Console.WriteLine("  OpenBlade.Capture query-temporary-full-charge");
Console.WriteLine("  OpenBlade.Capture query-fan-rpm");
Console.WriteLine("  OpenBlade.Capture query-lighting-brightness");
Console.WriteLine("  OpenBlade.Capture query-performance-fan-discriminator");
Console.WriteLine(
    "  OpenBlade.Capture validate-performance-fan " +
    "<balanced|silent|performance|fixed5400|battery-saver|battery-balanced> " +
    "--confirm-target " +
    PerformanceFanSetterValidationCommand.ExactTargetConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-lighting --confirm-target " +
    LightingValidationCommand.ExactTargetConfirmation +
    " --brightness <1-100>");
Console.WriteLine("  OpenBlade.Capture probe-battery-sequence");
Console.WriteLine("  OpenBlade.Capture validate-battery <off|50|55|60|65|70|75|80>");
Console.WriteLine("  OpenBlade.Capture validate-protection-off");
Console.WriteLine("  OpenBlade.Capture validate-temporary-full-charge");
Console.WriteLine("See captures/README.md for the gated USBPcap workflow.");
return args.Length == 0 ? 0 : 1;
