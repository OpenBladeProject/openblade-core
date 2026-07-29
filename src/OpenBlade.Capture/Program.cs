using OpenBlade.Capture;
using OpenBlade.Contracts;
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

if (args.Length == 1 && args[0].Equals(
    "scan-rz09-0528-02c6",
    StringComparison.OrdinalIgnoreCase))
{
    const ushort vendorId = 0x1532;
    const ushort productId = 0x02C6;
    IReadOnlyList<HidInterfaceInfo> interfaces =
        WindowsHidEnumerator.FindCollections(vendorId, productId);
    Console.WriteLine(
        $"Found {interfaces.Count} HID collection(s) for {vendorId:X4}:{productId:X4}.");
    foreach (HidInterfaceInfo device in interfaces)
    {
        Console.WriteLine(
            $"Usage {device.UsagePage:X4}:{device.Usage:X4}, " +
            $"feature bytes {device.FeatureReportByteLength}.");
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

if (args.Length == 1 && args[0].Equals(
    "probe-gpu-clock-offsets",
    StringComparison.OrdinalIgnoreCase))
{
    return GpuClockOffsetProbeCommand.Run(
        new NvidiaNvmlGpuClockOffsetProbe(),
        Console.Out);
}

if (args.Length is >= 1 and <= 3 && args[0].Equals(
    "probe-special-keys",
    StringComparison.OrdinalIgnoreCase))
{
    int seconds = args.Length >= 2 && int.TryParse(args[1], out int parsedSeconds)
        ? parsedSeconds
        : 30;
    try
    {
        if (args.Length < 3)
        {
            return new SpecialKeyInputProbe(Console.Out).Run(
                TimeSpan.FromSeconds(seconds));
        }

        string outputPath = Path.GetFullPath(args[2]);
        await using var outputStream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        await using var outputWriter = new StreamWriter(outputStream)
        {
            AutoFlush = true,
        };
        return new SpecialKeyInputProbe(outputWriter).Run(
            TimeSpan.FromSeconds(seconds));
    }
    catch (Exception exception) when (
        exception is System.ComponentModel.Win32Exception
        or ArgumentOutOfRangeException)
    {
        Console.Error.WriteLine($"Special-key probe failed: {exception.Message}");
        return 2;
    }
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

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0528-02c6-battery",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    var session = new BatteryQuerySession(
        BatteryValidationFactory.CreateWindowsRz090528BatteryValidation());
    var command = new BatteryQueryCommand(session.RunAsync, Console.Out);
    return await command.RunAsync([], CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0528-02c6-battery-raw",
    StringComparison.OrdinalIgnoreCase))
{
    HidSessionResult<byte> result =
        await BatteryValidationFactory.CreateWindowsRz090528BatteryValidation()
            .RunRawAsync(CancellationToken.None);
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    Console.WriteLine(
        $"RawValue={(result.Failure is null ? $"0x{result.Value:X2}" : "Unavailable")}");
    Console.WriteLine($"Failure={result.Failure?.ToString() ?? "None"}");
    Console.WriteLine(
        $"ProtocolValidation={result.Diagnostic?.ProtocolValidationFailure?.ToString() ?? "None"}");
    Console.WriteLine($"Success={result.Failure is null}");
    return result.Failure is null ? 0 : 2;
}

if (args.Length > 0 && args[0].Equals(
    "validate-rz09-0528-02c6-battery",
    StringComparison.OrdinalIgnoreCase))
{
    var approval = new ConsoleBatteryApproval(Console.In, Console.Out);
    var session = new BatteryValidationSession(
        BatteryValidationFactory.CreateWindowsRz090528BatteryValidation(),
        approval,
        TimeSpan.FromSeconds(5));
    var command = new BatteryValidationCommand(session.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "probe-rz09-0528-02c6-production-battery",
    StringComparison.OrdinalIgnoreCase))
{
    IProductionBatteryPortFactory factory =
        BatteryValidationFactory.CreateWindowsRz090528ProductionBattery();
    Console.WriteLine(
        $"ChargeLimits={string.Join(",", factory.Capabilities.ChargeLimits.Order())}");
    Console.WriteLine(
        $"SupportsTemporaryFullCharge={factory.Capabilities.SupportsTemporaryFullCharge}");
    HidSessionResult<IProductionBatteryPort> opened =
        await factory.OpenAsync(CancellationToken.None);
    Console.WriteLine($"OpenFailure={opened.Failure?.ToString() ?? "None"}");
    if (opened.Value is not { } port)
    {
        return 2;
    }

    await using (port)
    {
        try
        {
            ConfirmedBatteryState state =
                await port.QueryProtectionAsync(CancellationToken.None);
            bool? temporaryFullCharge =
                await port.QueryTemporaryFullChargeAsync(CancellationToken.None);
            ProductionPerformanceFanState performance =
                await port.QueryPerformanceFanStateAsync(CancellationToken.None);
            Console.WriteLine($"Protection={state}");
            Console.WriteLine(
                $"TemporaryFullCharge={temporaryFullCharge?.ToString() ?? "Unavailable"}");
            Console.WriteLine($"Performance={performance.Performance}");
            Console.WriteLine($"Fans={performance.Fans}");
            bool success = factory.Capabilities.ChargeLimits.SequenceEqual([70])
                && factory.Capabilities.PerformancePresets.SetEquals(
                    [
                        PerformancePreset.Balanced,
                        PerformancePreset.Silent,
                        PerformancePreset.Performance,
                    ])
                && factory.Capabilities.FanModes.SetEquals([FanMode.Automatic])
                && factory.Capabilities.SupportsTemporaryFullCharge
                && temporaryFullCharge is false
                && performance == new ProductionPerformanceFanState(
                    PerformancePreset.Balanced,
                    FanMode.Automatic);
            Console.WriteLine($"Success={success}");
            return success ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"QueryFailure={exception.GetType().Name}");
            Console.WriteLine("Success=False");
            return 2;
        }
    }
}

if (args.Length == 2 && (
    args[0].Equals(
        "validate-rz09-0528-02c6-performance-silent",
        StringComparison.OrdinalIgnoreCase)
    || args[0].Equals(
        "validate-rz09-0528-02c6-performance-performance",
        StringComparison.OrdinalIgnoreCase)))
{
    const string exactConfirmation = "RZ09-0528:1532:02C6:2.02";
    PerformancePreset targetPreset = args[0].EndsWith(
        "-silent",
        StringComparison.OrdinalIgnoreCase)
        ? PerformancePreset.Silent
        : PerformancePreset.Performance;
    if (!args[1].Equals(exactConfirmation, StringComparison.Ordinal))
    {
        Console.WriteLine(
            $"Usage: OpenBlade.Capture {args[0]} {exactConfirmation}");
        return 1;
    }

    IProductionBatteryPortFactory factory =
        BatteryValidationFactory.CreateWindowsRz090528ProductionBattery();
    HidSessionResult<IProductionBatteryPort> opened =
        await factory.OpenAsync(CancellationToken.None);
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    Console.WriteLine($"OpenFailure={opened.Failure?.ToString() ?? "None"}");
    if (opened.Value is not { } port)
    {
        Console.WriteLine("Success=False");
        return 2;
    }

    await using (port)
    {
        ProductionPerformanceFanState baseline =
            await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        Console.WriteLine($"Baseline={baseline.Performance}/{baseline.Fans}");
        if (baseline != new ProductionPerformanceFanState(
                PerformancePreset.Balanced,
                FanMode.Automatic))
        {
            Console.WriteLine("Refused=Baseline must be Balanced/Automatic");
            Console.WriteLine("Success=False");
            return 2;
        }

        ProductionPerformanceFanState? applied = null;
        ProductionPerformanceFanState? restored = null;
        Exception? failure = null;
        try
        {
            await port.SetPerformanceAsync(
                targetPreset,
                CancellationToken.None);
            applied = await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                await port.SetPerformanceAsync(
                    PerformancePreset.Balanced,
                    CancellationToken.None);
                restored = await port.QueryPerformanceFanStateAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        Console.WriteLine(
            $"Applied={applied?.Performance.ToString() ?? "Unavailable"}/" +
            $"{applied?.Fans.ToString() ?? "Unavailable"}");
        Console.WriteLine(
            $"Restored={restored?.Performance.ToString() ?? "Unavailable"}/" +
            $"{restored?.Fans.ToString() ?? "Unavailable"}");
        Console.WriteLine($"Failure={failure?.GetType().Name ?? "None"}");
        bool success = failure is null
            && applied == new ProductionPerformanceFanState(
                targetPreset,
                FanMode.Automatic)
            && restored == baseline;
        Console.WriteLine($"Success={success}");
        return success ? 0 : 2;
    }
}

if (args.Length > 0 && args[0].Equals("query-temporary-full-charge", StringComparison.OrdinalIgnoreCase))
{
    var session = new TemporaryFullChargeQuerySession(
        BatteryValidationFactory.CreateWindowsTemporaryFullChargeQuery());
    var command = new TemporaryFullChargeQueryCommand(session.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0528-02c6-temporary-full-charge",
    StringComparison.OrdinalIgnoreCase))
{
    var session = new TemporaryFullChargeQuerySession(
        BatteryValidationFactory.CreateWindowsRz090528TemporaryFullChargeQuery());
    var command = new TemporaryFullChargeQueryCommand(session.RunAsync, Console.Out);
    return await command.RunAsync([], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals("query-fan-rpm", StringComparison.OrdinalIgnoreCase))
{
    var session = new FanRpmQuerySession(BatteryValidationFactory.CreateWindowsFanRpmQuery());
    var command = new FanRpmQueryCommand(session.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0528-02c6-fan-rpm",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    var session = new FanRpmQuerySession(
        BatteryValidationFactory.CreateWindowsRz090528FanRpmQuery());
    var command = new FanRpmQueryCommand(session.RunAsync, Console.Out);
    return await command.RunAsync([], CancellationToken.None);
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

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0528-02c6-lighting-brightness",
    StringComparison.OrdinalIgnoreCase))
{
    ILightingBrightnessQueryRunner runner =
        BatteryValidationFactory.CreateWindowsRz090528LightingBrightnessQuery();
    HidSessionResult<LightingBrightnessReading> result =
        await runner.RunAsync(CancellationToken.None);
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    Console.WriteLine($"RawValue={result.Value?.RawValue.ToString() ?? "Unavailable"}");
    Console.WriteLine($"Percent={result.Value?.Percent.ToString() ?? "Unavailable"}");
    Console.WriteLine($"Failure={result.Failure?.ToString() ?? "None"}");
    Console.WriteLine($"FailurePhase={result.Diagnostic?.Phase.ToString() ?? "None"}");
    Console.WriteLine(
        $"ProtocolValidation={result.Diagnostic?.ProtocolValidationFailure?.ToString() ?? "None"}");
    Console.WriteLine($"Success={result.Failure is null && result.Value is not null}");
    return result.Failure is null && result.Value is not null ? 0 : 2;
}

if (args.Length == 3 && args[0].Equals(
    "validate-rz09-0528-02c6-lighting-brightness",
    StringComparison.OrdinalIgnoreCase))
{
    const string exactConfirmation = "RZ09-0528:1532:02C6:2.02";
    if (!args[1].Equals(exactConfirmation, StringComparison.Ordinal)
        || !byte.TryParse(args[2], out byte targetPercent)
        || targetPercent != 60)
    {
        Console.Error.WriteLine(
            $"Validation requires: {exactConfirmation} 60");
        return 2;
    }

    IProductionLightingPortFactory factory =
        BatteryValidationFactory.CreateWindowsRz090528BrightnessLighting();
    HidSessionResult<IProductionLightingPort> opened =
        await factory.OpenAsync(CancellationToken.None);
    if (opened.Value is not Rz090528BrightnessLightingPort port
        || opened.Failure is not null)
    {
        if (opened.Value is not null)
        {
            await opened.Value.DisposeAsync();
        }

        Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
        Console.WriteLine($"Failure={opened.Failure?.ToString() ?? "NativeIoFailure"}");
        Console.WriteLine("RestorationConfirmed=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    await using (port)
    {
        try
        {
            BrightnessRoundTripResult result =
                await port.ValidateRoundTripAsync(60, CancellationToken.None);
            Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
            Console.WriteLine($"BaselineRaw={result.BaselineRaw}");
            Console.WriteLine($"TargetRaw={result.TargetRaw}");
            Console.WriteLine($"AppliedRaw={result.AppliedRaw}");
            Console.WriteLine($"RestoredRaw={result.RestoredRaw}");
            Console.WriteLine(
                $"RestorationConfirmed={result.RestoredRaw == result.BaselineRaw}");
            Console.WriteLine("Success=True");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
            Console.WriteLine($"Failure={exception.GetType().Name}");
            Console.WriteLine($"Message={exception.Message}");
            Console.WriteLine("Success=False");
            return 2;
        }
    }
}

if (args.Length == 2 && args[0].Equals(
    "validate-rz09-0528-02c6-lighting-brightness-step",
    StringComparison.OrdinalIgnoreCase))
{
    const string exactConfirmation = "RZ09-0528:1532:02C6:2.02";
    if (!args[1].Equals(exactConfirmation, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"Validation requires: {exactConfirmation}");
        return 2;
    }

    IProductionLightingPortFactory factory =
        BatteryValidationFactory.CreateWindowsRz090528BrightnessLighting();
    HidSessionResult<IProductionLightingPort> opened =
        await factory.OpenAsync(CancellationToken.None);
    if (opened.Value is not Rz090528BrightnessLightingPort port
        || opened.Failure is not null)
    {
        if (opened.Value is not null)
        {
            await opened.Value.DisposeAsync();
        }

        Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
        Console.WriteLine($"Failure={opened.Failure?.ToString() ?? "NativeIoFailure"}");
        Console.WriteLine("RestorationConfirmed=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    await using (port)
    {
        try
        {
            BrightnessRoundTripResult result =
                await port.ValidateStepRoundTripAsync(
                    KeyboardBrightnessDirection.Increase,
                    CancellationToken.None);
            Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
            Console.WriteLine("Direction=Increase");
            Console.WriteLine($"BaselineRaw={result.BaselineRaw}");
            Console.WriteLine($"TargetRaw={result.TargetRaw}");
            Console.WriteLine($"AppliedRaw={result.AppliedRaw}");
            Console.WriteLine($"RestoredRaw={result.RestoredRaw}");
            Console.WriteLine(
                $"RestorationConfirmed={result.RestoredRaw == result.BaselineRaw}");
            Console.WriteLine("Success=True");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
            Console.WriteLine($"Failure={exception.GetType().Name}");
            Console.WriteLine($"Message={exception.Message}");
            Console.WriteLine("Success=False");
            return 2;
        }
    }
}

if (args.Length == 1 && args[0].Equals(
    "probe-rz09-0528-02c6-production-admission",
    StringComparison.OrdinalIgnoreCase))
{
    BatteryValidationProbeResult probe =
        BatteryValidationFactory.ProbeWindowsForService();
    IProductionBatteryPortFactory battery =
        BatteryValidationFactory.CreateWindowsProductionBatteryForService();
    IProductionLightingPortFactory lighting =
        BatteryValidationFactory.CreateWindowsProductionLightingForService();
    var fanSession = new FanRpmQuerySession(
        BatteryValidationFactory.CreateWindowsFanRpmForService());
    FanRpmQueryResult fanRpm = await fanSession.RunAsync(CancellationToken.None);
    HidSessionResult<IProductionBatteryPort> openedBattery =
        await battery.OpenAsync(CancellationToken.None);
    bool? startupAnimation = null;
    string? mcuVersion = null;
    AdapterPowerState adapterPower = AdapterPowerState.Unknown;
    FunctionKeyPrimaryMode? functionKeyPrimary = null;
    bool? gamingMode = null;
    if (openedBattery.Value is { } batteryPort && openedBattery.Failure is null)
    {
        await using (batteryPort)
        {
            adapterPower =
                await batteryPort.QueryAdapterPowerStateAsync(CancellationToken.None);
            mcuVersion = await batteryPort.QueryMcuVersionAsync(CancellationToken.None);
            startupAnimation =
                await batteryPort.QueryStartupAnimationAsync(CancellationToken.None);
            functionKeyPrimary =
                await batteryPort.QueryFunctionKeyPrimaryAsync(CancellationToken.None);
            gamingMode =
                await batteryPort.QueryGamingModeAsync(CancellationToken.None);
        }
    }
    HidSessionResult<IProductionLightingPort> opened =
        await lighting.OpenAsync(CancellationToken.None);
    bool? driverMode = null;
    if (opened.Value is { } port && opened.Failure is null)
    {
        await using (port)
        {
            _ = await port.ReadStateAsync(CancellationToken.None);
            driverMode = await port.ReadDriverModeAsync(CancellationToken.None);
        }
    }

    ILightingBrightnessQueryRunner query =
        BatteryValidationFactory.CreateWindowsRz090528LightingBrightnessQuery();
    HidSessionResult<LightingBrightnessReading> brightness =
        await query.RunAsync(CancellationToken.None);

    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    Console.WriteLine($"Probe={probe.Classification}");
    Console.WriteLine(
        $"BatteryWriteCapabilities=" +
        $"{battery.Capabilities.PerformancePresets.Count}/" +
        $"{battery.Capabilities.FanModes.Count}/" +
        $"{battery.Capabilities.ChargeLimits.Count}/" +
        $"{battery.Capabilities.SupportsBatteryProtectionOff}/" +
        $"{battery.Capabilities.SupportsTemporaryFullCharge}");
    Console.WriteLine(
        $"LightingEffects={lighting.Capabilities.LightingEffects.Count}");
    Console.WriteLine(
        $"LogoModes={lighting.Capabilities.LogoLightingModes.Count}");
    Console.WriteLine(
        $"KeyboardBrightness={lighting.Capabilities.SupportsKeyboardBrightness}");
    Console.WriteLine($"OpenFailure={opened.Failure?.ToString() ?? "None"}");
    Console.WriteLine(
        $"BrightnessRaw={brightness.Value?.RawValue.ToString() ?? "Unavailable"}");
    Console.WriteLine(
        $"BrightnessPercent={brightness.Value?.Percent.ToString() ?? "Unavailable"}");
    Console.WriteLine(
        $"BrightnessQueryFailure={brightness.Failure?.ToString() ?? "None"}");
    Console.WriteLine(
        $"FanRpm={fanRpm.Readings?.Selector1Rpm.ToString() ?? "Unavailable"}/" +
        $"{fanRpm.Readings?.Selector2Rpm.ToString() ?? "Unavailable"}");
    Console.WriteLine($"FanRpmFailure={fanRpm.Failure}");
    Console.WriteLine(
        $"StartupAnimation={startupAnimation?.ToString() ?? "Unavailable"}");
    Console.WriteLine($"McuVersion={mcuVersion ?? "Unavailable"}");
    Console.WriteLine($"AdapterPower={adapterPower}");
    Console.WriteLine(
        $"BatteryOpenFailure={openedBattery.Failure?.ToString() ?? "None"}");
    Console.WriteLine(
        $"FunctionKeyPrimary={functionKeyPrimary?.ToString() ?? "Unavailable"}");
    Console.WriteLine($"GamingMode={gamingMode?.ToString() ?? "Unavailable"}");
    Console.WriteLine($"DriverMode={driverMode?.ToString() ?? "Unavailable"}");
    bool success = probe.Classification is
            BatteryValidationProbeClassification.Ready
            or BatteryValidationProbeClassification.ReadyWithAccessFallback
        && battery.Capabilities.PerformancePresets.SetEquals(
            [
                PerformancePreset.Balanced,
                PerformancePreset.Silent,
                PerformancePreset.Performance,
            ])
        && battery.Capabilities.FanModes.SetEquals([FanMode.Automatic])
        && battery.Capabilities.ChargeLimits.SetEquals([70])
        && !battery.Capabilities.SupportsBatteryProtectionOff
        && fanRpm.Success
        && startupAnimation is not null
        && !string.IsNullOrWhiteSpace(mcuVersion)
        && adapterPower == AdapterPowerState.FullPower280W
        && functionKeyPrimary is not null
        && gamingMode is not null
        && driverMode is not null
        && openedBattery.Failure is null
        && battery.Capabilities.SupportsTemporaryFullCharge
        && lighting.Capabilities.LightingEffects.Count == 0
        && lighting.Capabilities.LogoLightingModes.Count == 0
        && lighting.Capabilities.SupportsKeyboardBrightness
        && opened.Value is not null
        && opened.Failure is null
        && brightness.Value is not null
        && brightness.Failure is null;
    Console.WriteLine($"Success={success}");
    return success ? 0 : 2;
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

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0528-02c6-performance-fan-discriminator",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    IPerformanceFanDiscriminatorQueryRunner runner =
        BatteryValidationFactory.CreateWindowsRz090528PerformanceFanDiscriminatorQuery();
    var command = new PerformanceFanDiscriminatorQueryCommand(
        runner.RunAsync,
        Console.Out,
        new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02"));
    return await command.RunAsync([], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "probe-function-key-primary-getter",
    StringComparison.OrdinalIgnoreCase))
{
    IGetterCandidateProbeRunner runner =
        GetterCandidateProbeFactory.CreateFunctionKeyPrimaryWindows();
    var command = new FunctionKeyPrimaryGetterCandidateProbeCommand(
        runner.RunAsync,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "probe-rz09-0528-02c6-function-key-primary-getter",
    StringComparison.OrdinalIgnoreCase))
{
    IGetterCandidateProbeRunner runner =
        GetterCandidateProbeFactory.CreateFunctionKeyPrimaryRz090528Windows();
    var command = new FunctionKeyPrimaryGetterCandidateProbeCommand(
        runner.RunAsync,
        Console.Out,
        device: new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02"));
    return await command.RunAsync([], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "probe-gaming-mode-getter",
    StringComparison.OrdinalIgnoreCase))
{
    IGetterCandidateProbeRunner runner =
        GetterCandidateProbeFactory.CreateGamingModeWindows();
    var command = new GamingModeGetterCandidateProbeCommand(runner.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "probe-rz09-0528-02c6-gaming-mode-getter",
    StringComparison.OrdinalIgnoreCase))
{
    IGetterCandidateProbeRunner runner =
        GetterCandidateProbeFactory.CreateGamingModeRz090528Windows();
    var command = new GamingModeGetterCandidateProbeCommand(
        runner.RunAsync,
        Console.Out,
        device: new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02"));
    return await command.RunAsync([], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "query-device-mode",
    StringComparison.OrdinalIgnoreCase))
{
    IGetterCandidateProbeRunner runner =
        GetterCandidateProbeFactory.CreateDeviceModeWindows();
    var command = new DeviceModeQueryCommand(runner.RunAsync, Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0528-02c6-device-mode",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    IGetterCandidateProbeRunner runner =
        GetterCandidateProbeFactory.CreateDeviceModeRz090528Windows();
    var command = new DeviceModeQueryCommand(runner.RunAsync, Console.Out);
    return await command.RunAsync([], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-device-mode",
    StringComparison.OrdinalIgnoreCase))
{
    IDeviceModeValidationRunner runner =
        GetterCandidateProbeFactory.CreateDeviceModeValidationWindows();
    var command = new DeviceModeValidationCommand(
        runner.RunAsync,
        Console.In,
        Console.Out);
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

if (args.Length == 2 && args[0].Equals(
    "validate-rz09-0528-02c6-temporary-full-charge",
    StringComparison.OrdinalIgnoreCase))
{
    const string exactConfirmation = "RZ09-0528:1532:02C6:2.02";
    if (!args[1].Equals(exactConfirmation, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"Validation requires: {exactConfirmation}");
        return 2;
    }

    var approval = new ConsoleTemporaryFullChargeApproval(Console.In, Console.Out);
    var session = new TemporaryFullChargeValidationSession(
        BatteryValidationFactory.CreateWindowsRz090528TemporaryFullChargeValidation(),
        approval,
        TimeSpan.FromSeconds(5));
    var command = new TemporaryFullChargeValidationCommand(session.RunAsync, Console.Out);
    return await command.RunAsync([], CancellationToken.None);
}

Console.WriteLine("OpenBlade capture utility (no raw hardware command support)");
Console.WriteLine("  OpenBlade.Capture target");
Console.WriteLine("  OpenBlade.Capture scan");
Console.WriteLine("  OpenBlade.Capture scan-all");
Console.WriteLine("  OpenBlade.Capture scan-rz09-0528-02c6");
Console.WriteLine("  OpenBlade.Capture query-display");
Console.WriteLine("  OpenBlade.Capture probe-gpu-clock-offsets");
Console.WriteLine("  OpenBlade.Capture probe-special-keys [seconds] [output-file]");
Console.WriteLine("  OpenBlade.Capture validate <fixtures.json>");
Console.WriteLine("  OpenBlade.Capture probe-battery");
Console.WriteLine("  OpenBlade.Capture query-battery");
Console.WriteLine("  OpenBlade.Capture query-rz09-0528-02c6-battery");
Console.WriteLine("  OpenBlade.Capture query-rz09-0528-02c6-battery-raw");
Console.WriteLine("  OpenBlade.Capture validate-rz09-0528-02c6-battery <50|55|60|65|70|75|80>");
Console.WriteLine("  OpenBlade.Capture probe-rz09-0528-02c6-production-battery");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0528-02c6-performance-silent " +
    "RZ09-0528:1532:02C6:2.02");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0528-02c6-performance-performance " +
    "RZ09-0528:1532:02C6:2.02");
Console.WriteLine("  OpenBlade.Capture query-temporary-full-charge");
Console.WriteLine("  OpenBlade.Capture query-rz09-0528-02c6-temporary-full-charge");
Console.WriteLine("  OpenBlade.Capture query-fan-rpm");
Console.WriteLine("  OpenBlade.Capture query-rz09-0528-02c6-fan-rpm");
Console.WriteLine("  OpenBlade.Capture query-lighting-brightness");
Console.WriteLine("  OpenBlade.Capture query-rz09-0528-02c6-lighting-brightness");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0528-02c6-lighting-brightness " +
    "RZ09-0528:1532:02C6:2.02 60");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0528-02c6-lighting-brightness-step " +
    "RZ09-0528:1532:02C6:2.02");
Console.WriteLine(
    "  OpenBlade.Capture probe-rz09-0528-02c6-production-admission");
Console.WriteLine("  OpenBlade.Capture query-performance-fan-discriminator");
Console.WriteLine(
    "  OpenBlade.Capture query-rz09-0528-02c6-performance-fan-discriminator");
Console.WriteLine("  OpenBlade.Capture probe-function-key-primary-getter");
Console.WriteLine("  OpenBlade.Capture probe-gaming-mode-getter");
Console.WriteLine(
    "  OpenBlade.Capture probe-rz09-0528-02c6-function-key-primary-getter");
Console.WriteLine(
    "  OpenBlade.Capture probe-rz09-0528-02c6-gaming-mode-getter");
Console.WriteLine("  OpenBlade.Capture query-rz09-0528-02c6-device-mode");
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
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0528-02c6-temporary-full-charge" +
    " RZ09-0528:1532:02C6:2.02");
Console.WriteLine("See captures/README.md for the gated USBPcap workflow.");
return args.Length == 0 ? 0 : 1;
