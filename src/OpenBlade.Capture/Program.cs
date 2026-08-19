using OpenBlade.Capture;
using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Protocol;
using OpenBlade.Service;

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

if (args.Length == 1 && args[0].Equals(
    "probe-rz09-0528-02c6-hid-input-access",
    StringComparison.OrdinalIgnoreCase))
{
    IReadOnlyList<HidInputAccessObservation> observations =
        new HidInputAccessProbe().Run();
    Console.WriteLine($"CollectionCount={observations.Count}");
    foreach (HidInputAccessObservation observation in observations)
    {
        Console.WriteLine(
            $"Collection={observation.Collection};" +
            $"Usage={observation.UsagePage:X4}:{observation.Usage:X4};" +
            $"FeatureBytes={observation.FeatureReportByteLength};" +
            $"ReadHandleOpened={observation.ReadHandleOpened};" +
            $"IdentityReverified={observation.IdentityReverified};" +
            $"Win32Error={observation.Win32Error?.ToString() ?? "None"};" +
            $"Failure={observation.Failure ?? "None"}");
    }
    Console.WriteLine(
        $"ReadHandleOpenCount={observations.Count(item => item.ReadHandleOpened)}");
    Console.WriteLine(
        $"AccessDeniedCount={observations.Count(item => item.Win32Error == 5)}");
    Console.WriteLine("ProbeComplete=True");
    return observations.Count > 0 ? 0 : 3;
}

if (args.Length > 0 && args[0].Equals(
    "probe-rz09-0528-02c6-lamp-array",
    StringComparison.OrdinalIgnoreCase))
{
    var command = new LampArrayDiscoveryCommand(
        new WindowsLampArrayDiscoveryProbe(),
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
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

if (args.Length == 2
    && args[0].Equals("validate-display-hdr", StringComparison.OrdinalIgnoreCase))
{
    const string exactConfirmation = "WINDOWS-INTERNAL-DISPLAY:RESTORE";
    if (!args[1].Equals(exactConfirmation, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"Validation requires: validate-display-hdr {exactConfirmation}");
        return 2;
    }

    using var controller = new WindowsDisplaySettingsController();
    return await DisplayHdrValidationCommand.RunAsync(
        controller,
        Console.Out,
        CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "probe-gpu-clock-offsets",
    StringComparison.OrdinalIgnoreCase))
{
    return GpuClockOffsetProbeCommand.Run(
        new NvidiaNvmlGpuClockOffsetProbe(),
        Console.Out);
}

if (args.Length == 2 && args[0].Equals(
    "validate-rz09-0528-02c6-gpu-clock-offsets",
    StringComparison.OrdinalIgnoreCase))
{
    bool confirmationMatched = args[1].Equals(
        GpuClockOffsetValidationEnvironment.Confirmation,
        StringComparison.Ordinal);
    bool exactHost = GpuClockOffsetValidationEnvironment.IsExactHost();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactProtocolCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool serviceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine($"ConfirmationMatched={confirmationMatched}");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={serviceInactive}");
    if (!confirmationMatched
        || !exactHost
        || !exactDevice
        || !synapseInactive
        || !serviceInactive)
    {
        Console.WriteLine("MutationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    return GpuClockOffsetValidationCommand.Run(
        new NvidiaNvmlGpuClockOffsetValidationController(),
        Console.Out,
        static () =>
            GpuClockOffsetValidationEnvironment.IsExactHost()
            && GpuClockOffsetValidationEnvironment.HasOneExactProtocolCollection()
            && GpuClockOffsetValidationEnvironment.IsSynapseInactive()
            && GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive());
}

if (args.Length == 2 && args[0].Equals(
    "validate-rz09-0581-02e0-bios-4.00-gpu-clock-offsets",
    StringComparison.OrdinalIgnoreCase))
{
    bool confirmationMatched = args[1].Equals(
        GpuClockOffsetValidationEnvironment.Rz090581Bios400Confirmation,
        StringComparison.Ordinal);
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    bool gpuTuningToolsInactive =
        GpuClockOffsetValidationEnvironment.AreGpuTuningToolsInactive();
    Console.WriteLine($"ConfirmationMatched={confirmationMatched}");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    Console.WriteLine($"GpuTuningToolsInactive={gpuTuningToolsInactive}");
    if (!confirmationMatched
        || !exactHost
        || !exactDevice
        || !synapseInactive
        || !openBladeServiceInactive
        || !gpuTuningToolsInactive)
    {
        Console.WriteLine("MutationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    return GpuClockOffsetValidationCommand.Run(
        new NvidiaNvmlGpuClockOffsetValidationController(),
        Console.Out,
        GpuClockOffsetValidationPlan.Rz090581Bios400,
        static () =>
            GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host()
            && GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection()
            && GpuClockOffsetValidationEnvironment.IsSynapseInactive()
            && GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive()
            && GpuClockOffsetValidationEnvironment.AreGpuTuningToolsInactive());
}

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0581-02e0-bios-4.00-performance",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("ReadOnly=True");
    if (!GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host())
    {
        Console.WriteLine("ExactHost=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    Console.WriteLine("ExactHost=True");
    IProductionBatteryPortFactory factory =
        ProductionBatteryFactory.CreateWindowsReadOnly();
    HidSessionResult<IProductionBatteryPort> opened =
        await factory.OpenAsync(CancellationToken.None);
    Console.WriteLine($"OpenFailure={opened.Failure?.ToString() ?? "None"}");
    if (opened.Value is not { } port || opened.Failure is not null)
    {
        Console.WriteLine("Success=False");
        return 2;
    }

    await using (port)
    {
        try
        {
            ProductionPerformanceFanState state =
                await port.QueryPerformanceFanStateAsync(CancellationToken.None);
            Console.WriteLine(
                $"Performance={state.Performance?.ToString() ?? "Unavailable"}");
            Console.WriteLine($"Fans={state.Fans?.ToString() ?? "Unavailable"}");
            Console.WriteLine("Success=True");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Failure={exception.GetType().Name}");
            Console.WriteLine($"Message={exception.Message}");
            Console.WriteLine("Success=False");
            return 2;
        }
    }
}

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0581-02e0-bios-4.00-hyperboost-state",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("ReadOnly=True");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactDevice
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("QueryAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    IPerformanceFanDiscriminatorQueryRunner runner =
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400HyperBoostObservationQuery();
    var command = new PerformanceFanDiscriminatorQueryCommand(
        runner.RunAsync,
        Console.Out,
        new DeviceIdentity("RZ09-0581", 0x1532, 0x02E0, "4.00"));
    return await command.RunAsync([], CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0581-bios-4.00-cooling-pad-fan-getters",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactPad = GpuClockOffsetValidationEnvironment
        .HasOneExactCoolingPadControlCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("TargetHost=RZ09-0581 BIOS 4.00");
    Console.WriteLine("TargetPad=1532:0F43 report 05");
    Console.WriteLine("ReadOnly=True");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine("FanObservationSemantics=OpaqueByte");
    Console.WriteLine("FanObservationHasAssignedUnits=False");
    Console.WriteLine("ConfiguredTargetReadbackConfirmed=False");
    Console.WriteLine("TachometerReadbackConfirmed=False");
    Console.WriteLine("ActiveModeReadbackConfirmed=False");
    Console.WriteLine("CoolingModeQueryAttempted=True");
    Console.WriteLine("CoolingModeSelectors=Firmware(0),Synapse(1)");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("QueryAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    bool success = true;
    foreach (CoolingPadFanGetterKind kind in Enum.GetValues<CoolingPadFanGetterKind>())
    {
        IGetterCandidateProbeRunner runner =
            CoolingPadFanGetterProbeFactory.CreateWindows(kind);
        HidSessionResult<GetterCandidateProbeSnapshot> result =
            await runner.RunAsync(CancellationToken.None);
        string prefix = kind.ToString();
        Console.WriteLine(
            $"{prefix}Failure={result.Failure?.ToString() ?? "None"}");
        if (result.Diagnostic is { } diagnostic)
        {
            Console.WriteLine($"{prefix}FailurePhase={diagnostic.Phase}");
            Console.WriteLine(
                $"{prefix}NativeWin32Error=" +
                $"{diagnostic.NativeWin32Error?.ToString() ?? "None"}");
            Console.WriteLine(
                $"{prefix}TransferredBytes=" +
                $"{diagnostic.TransferredByteCount?.ToString() ?? "None"}");
            Console.WriteLine(
                $"{prefix}ProtocolValidation=" +
                $"{diagnostic.ProtocolValidationFailure?.ToString() ?? "None"}");
        }

        if (result.Value is { } snapshot && result.Failure is null)
        {
            Console.WriteLine($"{prefix}Response={snapshot.ResponsePayloadHex}");
        }
        else
        {
            success = false;
        }
    }

    Console.WriteLine($"Success={success}");
    return success ? 0 : 2;
}

if (args.Length > 0 && args[0].Equals(
    "correlate-cooling-pad-fan-button",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactPad = GpuClockOffsetValidationEnvironment
        .HasOneExactCoolingPadControlCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    bool usbCaptureInactive =
        GpuClockOffsetValidationEnvironment.IsUsbCaptureInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine(
        "Action=Keep one exact read-only pad session open; record opaque 0D/81 and stored configuration banks 0/1 at Low, Medium, High, and Low return");
    Console.WriteLine("SetterCommandsReachable=False");
    Console.WriteLine("ActiveModeReadbackConfirmed=False");
    Console.WriteLine("TachometerReadbackConfirmed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    Console.WriteLine($"UsbCaptureInactive={usbCaptureInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive
        || !usbCaptureInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    var command = new CoolingPadFanButtonCorrelationCommand(
        CoolingPadFanButtonCorrelationRunner.CreateWindows().RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

CoolingPadFanBoundaryKind? coolingPadFanBoundary = args.Length == 0
    ? null
    : args[0].Equals(
        "validate-cooling-pad-fan-disconnect-reconnect",
        StringComparison.OrdinalIgnoreCase)
        ? CoolingPadFanBoundaryKind.DisconnectReconnect
        : args[0].Equals(
            "validate-cooling-pad-fan-low-power-resume",
            StringComparison.OrdinalIgnoreCase)
            ? CoolingPadFanBoundaryKind.LowPowerResume
            : null;
if (coolingPadFanBoundary is { } fanBoundary)
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactPad = GpuClockOffsetValidationEnvironment
        .HasOneExactCoolingPadControlCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    bool usbCaptureInactive =
        GpuClockOffsetValidationEnvironment.IsUsbCaptureInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine($"Boundary={fanBoundary}");
    Console.WriteLine(
        "Action=Apply the exact 2200 RPM host-control plan once; confirm it physically; close the pad handle; cross the selected lifecycle boundary; reopen one exact handle; confirm retained 2200 before any new write; release once; confirm physically");
    Console.WriteLine(
        "RequiredRecovery=No retry. If any state is uncertain, restore Auto through Synapse; power-cycle pad USB and 12 V if it sleeps or flashes red");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine("ActiveModeReadbackConfirmed=False");
    Console.WriteLine("PhysicalRpmReadbackConfirmed=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    Console.WriteLine($"UsbCaptureInactive={usbCaptureInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive
        || !usbCaptureInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    var command = new CoolingPadFanBoundaryValidationCommand(
        CoolingPadFanBoundaryValidationRunner.CreateWindows().RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(
        fanBoundary,
        args[1..],
        CancellationToken.None);
}

CoolingPadLightingBoundaryKind? coolingPadLightingBoundary = args.Length == 0
    ? null
    : args[0].Equals(
        "validate-cooling-pad-lighting-disconnect-reconnect",
        StringComparison.OrdinalIgnoreCase)
        ? CoolingPadLightingBoundaryKind.DisconnectReconnect
        : args[0].Equals(
            "validate-cooling-pad-lighting-low-power-resume",
            StringComparison.OrdinalIgnoreCase)
            ? CoolingPadLightingBoundaryKind.LowPowerResume
            : args[0].Equals(
                "validate-cooling-pad-lighting-controller-handoff",
                StringComparison.OrdinalIgnoreCase)
                ? CoolingPadLightingBoundaryKind.ControllerHandoff
                : null;
if (coolingPadLightingBoundary is { } lightingBoundary)
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactPad = GpuClockOffsetValidationEnvironment
        .HasOneExactCoolingPadControlCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    bool usbCaptureInactive =
        GpuClockOffsetValidationEnvironment.IsUsbCaptureInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine($"Boundary={lightingBoundary}");
    Console.WriteLine(CoolingPadLightingBoundaryValidationCommand.PreflightAction);
    Console.WriteLine(CoolingPadLightingBoundaryValidationCommand.PreflightRecovery);
    Console.WriteLine("RetryAllowed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    Console.WriteLine($"UsbCaptureInactive={usbCaptureInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive
        || !usbCaptureInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    var command = new CoolingPadLightingBoundaryValidationCommand(
        CoolingPadLightingBoundaryValidationRunner.CreateWindows().RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(
        lightingBoundary,
        args[1..],
        CancellationToken.None);
}

if (args.Length is >= 1 and <= 3
    && (args[0].Equals(
            "probe-special-keys",
            StringComparison.OrdinalIgnoreCase)
        || args[0].Equals(
            "probe-rz09-0528-02c6-special-keys",
            StringComparison.OrdinalIgnoreCase)))
{
    string targetProductIdHex = args[0].Equals(
        "probe-rz09-0528-02c6-special-keys",
        StringComparison.OrdinalIgnoreCase)
            ? "02C6"
            : "02E0";
    int seconds = args.Length >= 2 && int.TryParse(args[1], out int parsedSeconds)
        ? parsedSeconds
        : 30;
    try
    {
        if (args.Length < 3)
        {
            return new SpecialKeyInputProbe(Console.Out, targetProductIdHex).Run(
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
        return new SpecialKeyInputProbe(outputWriter, targetProductIdHex).Run(
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
            string performancePresets = string.Join(
                ",",
                factory.Capabilities.PerformancePresets.Order());
            string fanModes = string.Join(",", factory.Capabilities.FanModes.Order());
            string observedPowerSources = string.Join(
                ",",
                factory.Capabilities.ObservedPowerSourceBehaviors
                    .Select(value => value.Source)
                    .Order());
            Console.WriteLine($"PerformancePresets={performancePresets}");
            Console.WriteLine($"FanModes={fanModes}");
            Console.WriteLine($"ObservedPowerSources={observedPowerSources}");
            bool success = factory.Capabilities.ChargeLimits.SequenceEqual(
                    [50, 55, 60, 65, 70, 75, 80])
                && factory.Capabilities.SupportsBatteryProtectionOff
                && factory.Capabilities.PerformancePresets.SetEquals(
                    [
                        PerformancePreset.Balanced,
                        PerformancePreset.Silent,
                        PerformancePreset.Performance,
                        PerformancePreset.Custom,
                    ])
                && factory.Capabilities.FanModes.SetEquals(
                    [
                        FanMode.Automatic,
                        FanMode.Fixed,
                        FanMode.ManualCurve,
                        FanMode.Maximum,
                    ])
                && factory.Capabilities.ObservedPowerSourceBehaviors
                    .Select(value => value.Source)
                    .ToHashSet()
                    .SetEquals([PowerSource.Battery, PowerSource.UsbC])
                && factory.Capabilities.SupportsTemporaryFullCharge
                && state is
                {
                    ProtectionEnabled: true,
                    ChargeLimitPercent: 80,
                }
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

if (args.Length == 2 && args[0].Equals(
    "probe-rz09-0528-02c6-logo",
    StringComparison.OrdinalIgnoreCase))
{
    const string exactConfirmation = "RZ09-0528:1532:02C6:2.02";
    if (!args[1].Equals(exactConfirmation, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"Validation requires: probe-rz09-0528-02c6-logo {exactConfirmation}");
        return 2;
    }

    IProductionLightingPortFactory factory =
        BatteryValidationFactory.CreateWindowsRz090528BrightnessLighting();
    HidSessionResult<IProductionLightingPort> opened =
        await factory.OpenAsync(CancellationToken.None);
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    Console.WriteLine($"OpenFailure={opened.Failure?.ToString() ?? "None"}");
    if (opened.Value is not Rz090528BrightnessLightingPort port)
    {
        Console.WriteLine("Success=False");
        return 2;
    }

    await using (port)
    {
        try
        {
            LogoLightingMode mode =
                await port.ReadLogoCandidateStateAsync(CancellationToken.None);
            Console.WriteLine($"LogoMode={mode}");
            Console.WriteLine("Success=True");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"QueryFailure={exception.GetType().Name}");
            Console.WriteLine("Success=False");
            return 2;
        }
    }
}

if (args.Length == 2 && args[0].Equals(
    "validate-rz09-0528-02c6-logo",
    StringComparison.OrdinalIgnoreCase))
{
    const string exactConfirmation = "RZ09-0528:1532:02C6:2.02";
    if (!args[1].Equals(exactConfirmation, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"Validation requires: validate-rz09-0528-02c6-logo {exactConfirmation}");
        return 2;
    }

    IProductionLightingPortFactory factory =
        BatteryValidationFactory.CreateWindowsRz090528BrightnessLighting();
    HidSessionResult<IProductionLightingPort> opened =
        await factory.OpenAsync(CancellationToken.None);
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    Console.WriteLine($"OpenFailure={opened.Failure?.ToString() ?? "None"}");
    if (opened.Value is not Rz090528BrightnessLightingPort port)
    {
        Console.WriteLine("Success=False");
        return 2;
    }

    await using (port)
    {
        LogoLightingMode baseline =
            await port.ReadLogoCandidateStateAsync(CancellationToken.None);
        Console.WriteLine($"Baseline={baseline}");
        if (baseline != LogoLightingMode.Static)
        {
            Console.WriteLine("Refused=Baseline must be Static");
            Console.WriteLine("Success=False");
            return 2;
        }

        LogoLightingMode? breathing = null;
        LogoLightingMode? off = null;
        LogoLightingMode? restored = null;
        Exception? primaryFailure = null;
        Exception? restorationFailure = null;
        try
        {
            await port.SetLightingAsync(
                LightingSettingsFactory.Off() with
                {
                    LogoMode = LogoLightingMode.Breathing,
                },
                CancellationToken.None);
            breathing = await port.ReadLogoStateAsync(CancellationToken.None);
            await port.SetLightingAsync(
                LightingSettingsFactory.Off() with
                {
                    LogoMode = LogoLightingMode.Off,
                },
                CancellationToken.None);
            off = await port.ReadLogoStateAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            try
            {
                await port.SetLightingAsync(
                    LightingSettingsFactory.Off() with
                    {
                        LogoMode = LogoLightingMode.Static,
                    },
                    CancellationToken.None);
                restored = await port.ReadLogoStateAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                restorationFailure = exception;
            }
        }

        bool success = primaryFailure is null
            && restorationFailure is null
            && breathing == LogoLightingMode.Breathing
            && off == LogoLightingMode.Off
            && restored == LogoLightingMode.Static;
        Console.WriteLine($"BreathingReadback={breathing?.ToString() ?? "Unavailable"}");
        Console.WriteLine($"OffReadback={off?.ToString() ?? "Unavailable"}");
        Console.WriteLine($"RestoredReadback={restored?.ToString() ?? "Unavailable"}");
        Console.WriteLine(
            $"PrimaryFailure={primaryFailure?.GetType().Name ?? "None"}");
        Console.WriteLine(
            $"RestorationFailure={restorationFailure?.GetType().Name ?? "None"}");
        Console.WriteLine($"Success={success}");
        return success ? 0 : 2;
    }
}

if (args.Length == 3 && args[0].Equals(
    "validate-rz09-0528-02c6-one-shot",
    StringComparison.OrdinalIgnoreCase))
{
    const string exactConfirmation = "RZ09-0528:1532:02C6:2.02";
    if (!args[2].Equals(exactConfirmation, StringComparison.Ordinal)
        || args[1] is not ("startup" or "function" or "gaming"))
    {
        Console.Error.WriteLine(
            "Validation requires: " +
            "validate-rz09-0528-02c6-one-shot <startup|function|gaming> " +
            exactConfirmation);
        return 2;
    }

    IProductionBatteryPortFactory factory =
        BatteryValidationFactory.CreateWindowsRz090528ProductionBattery();
    HidSessionResult<IProductionBatteryPort> opened =
        await factory.OpenAsync(CancellationToken.None);
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    Console.WriteLine($"Control={args[1]}");
    Console.WriteLine($"OpenFailure={opened.Failure?.ToString() ?? "None"}");
    if (opened.Value is not { } port)
    {
        Console.WriteLine("Success=False");
        return 2;
    }

    await using (port)
    {
        ApplyOneShotLaptopControl candidate;
        ApplyOneShotLaptopControl restoration;
        Func<Task<string>> query;
        string expectedCandidate;
        string expectedRestoration;
        switch (args[1])
        {
            case "startup":
                candidate = new(
                    OneShotLaptopControl.StartupAnimation,
                    Enabled: false);
                restoration = new(
                    OneShotLaptopControl.StartupAnimation,
                    Enabled: true);
                query = async () =>
                    (await port.QueryStartupAnimationAsync(CancellationToken.None))
                        ?.ToString() ?? "Unavailable";
                expectedCandidate = bool.FalseString;
                expectedRestoration = bool.TrueString;
                break;
            case "function":
                candidate = new(
                    OneShotLaptopControl.FunctionKeyPrimary,
                    FunctionKeyMode: FunctionKeyPrimaryMode.MultimediaKeys);
                restoration = new(
                    OneShotLaptopControl.FunctionKeyPrimary,
                    FunctionKeyMode: FunctionKeyPrimaryMode.FunctionKeys);
                query = async () =>
                    (await port.QueryFunctionKeyPrimaryAsync(CancellationToken.None))
                        ?.ToString() ?? "Unavailable";
                expectedCandidate = FunctionKeyPrimaryMode.MultimediaKeys.ToString();
                expectedRestoration = FunctionKeyPrimaryMode.FunctionKeys.ToString();
                break;
            default:
                candidate = new(
                    OneShotLaptopControl.GamingMode,
                    Enabled: true);
                restoration = new(
                    OneShotLaptopControl.GamingMode,
                    Enabled: false);
                query = async () =>
                    (await port.QueryGamingModeAsync(CancellationToken.None))
                        ?.ToString() ?? "Unavailable";
                expectedCandidate = bool.TrueString;
                expectedRestoration = bool.FalseString;
                break;
        }

        string baseline = await query();
        string candidateReadback = "Unavailable";
        string restorationReadback = "Unavailable";
        Exception? primaryFailure = null;
        Exception? restorationFailure = null;
        try
        {
            await port.ApplyOneShotLaptopControlAsync(
                candidate,
                CancellationToken.None);
            candidateReadback = await query();
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            try
            {
                await port.ApplyOneShotLaptopControlAsync(
                    restoration,
                    CancellationToken.None);
                restorationReadback = await query();
            }
            catch (Exception exception)
            {
                restorationFailure = exception;
            }
        }

        bool success = primaryFailure is null
            && restorationFailure is null
            && candidateReadback == expectedCandidate
            && restorationReadback == expectedRestoration;
        Console.WriteLine($"Baseline={baseline}");
        Console.WriteLine($"CandidateReadback={candidateReadback}");
        Console.WriteLine($"RestorationReadback={restorationReadback}");
        Console.WriteLine(
            $"PrimaryFailure={primaryFailure?.GetType().Name ?? "None"}");
        Console.WriteLine(
            $"RestorationFailure={restorationFailure?.GetType().Name ?? "None"}");
        Console.WriteLine($"Success={success}");
        return success ? 0 : 2;
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
    LogoLightingMode? logoMode = null;
    if (opened.Value is { } port && opened.Failure is null)
    {
        await using (port)
        {
            _ = await port.ReadStateAsync(CancellationToken.None);
            driverMode = await port.ReadDriverModeAsync(CancellationToken.None);
            logoMode = await port.ReadLogoStateAsync(CancellationToken.None);
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
    Console.WriteLine(
        $"ObservedLightingEffects={lighting.Capabilities.ObservedLightingEffects.Count}");
    Console.WriteLine(
        $"SpecialKeyBindingsAccess=" +
        $"{lighting.Capabilities.SpecialKeyBindingsAccess?.ToString() ?? "Unavailable"}");
    Console.WriteLine(
        $"WritableSpecialKeySlots={lighting.Capabilities.WritableSpecialKeySlots}");
    Console.WriteLine(
        $"LampArrayLightingAccess=" +
        $"{lighting.Capabilities.LampArrayLightingAccess?.ToString() ?? "Unavailable"}");
    Console.WriteLine(
        $"CpuPowerLimit=" +
        $"{battery.Capabilities.CpuPowerLimit?.Watts.Minimum.ToString() ?? "Unavailable"}-" +
        $"{battery.Capabilities.CpuPowerLimit?.Watts.Maximum.ToString() ?? "Unavailable"}/" +
        $"{battery.Capabilities.CpuPowerLimit?.Watts.Step?.ToString() ?? "Unavailable"}/" +
        $"{battery.Capabilities.CpuPowerLimit?.Access.ToString() ?? "Unavailable"}");
    Console.WriteLine(
        $"CpuVoltageOptimizer=" +
        $"{battery.Capabilities.CpuVoltageOptimizer?.Value.Minimum.ToString() ?? "Unavailable"}-" +
        $"{battery.Capabilities.CpuVoltageOptimizer?.Value.Maximum.ToString() ?? "Unavailable"}/" +
        $"{battery.Capabilities.CpuVoltageOptimizer?.Value.Step?.ToString() ?? "Unavailable"}/" +
        $"{battery.Capabilities.CpuVoltageOptimizer?.Access.ToString() ?? "Unavailable"}");
    Console.WriteLine(
        $"GpuOverclock=" +
        $"{battery.Capabilities.GpuOverclock?.CoreOffsetMegahertz.Minimum.ToString() ?? "Unavailable"}-" +
        $"{battery.Capabilities.GpuOverclock?.CoreOffsetMegahertz.Maximum.ToString() ?? "Unavailable"}/" +
        $"{battery.Capabilities.GpuOverclock?.MemoryOffsetMegahertz.Minimum.ToString() ?? "Unavailable"}-" +
        $"{battery.Capabilities.GpuOverclock?.MemoryOffsetMegahertz.Maximum.ToString() ?? "Unavailable"}/" +
        $"{battery.Capabilities.GpuOverclock?.Access.ToString() ?? "Unavailable"}");
    Console.WriteLine(
        $"PowerSourceObservations={battery.Capabilities.ObservedPowerSourceBehaviors.Count}");
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
    Console.WriteLine($"LogoMode={logoMode?.ToString() ?? "Unavailable"}");
    bool success = probe.Classification is
            BatteryValidationProbeClassification.Ready
            or BatteryValidationProbeClassification.ReadyWithAccessFallback
        && Rz090528ProductionAdmissionCapabilityGate.Matches(
            battery.Capabilities,
            lighting.Capabilities)
        && fanRpm.Success
        && startupAnimation is not null
        && !string.IsNullOrWhiteSpace(mcuVersion)
        && adapterPower == AdapterPowerState.FullPower280W
        && functionKeyPrimary is not null
        && gamingMode is not null
        && driverMode is not null
        && logoMode == LogoLightingMode.Static
        && openedBattery.Failure is null
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

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0528-02c6-power-source",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    IPerformanceFanDiscriminatorQueryRunner runner =
        BatteryValidationFactory.CreateWindowsRz090528PowerSourceQuery();
    var command = new PerformanceFanDiscriminatorQueryCommand(
        runner.RunAsync,
        Console.Out,
        new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02"));
    return await command.RunAsync([], CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0528-02c6-cpu-power-service",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    Console.WriteLine("MutationPolicy=GetterExportsOnly");
    return await CpuPowerServiceProbeCommand.RunAsync(
        new WindowsRzAmdOverclockProbe(),
        Console.Out,
        CancellationToken.None);
}

if (args.Length == 3 && args[0].Equals(
    "query-rz09-0528-02c6-cpu-power-service-preconnected",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    Console.WriteLine("Backend=RzAMDOverClock/RzDLLService");
    using var handshake = new CpuPowerNamedEventIsolationHandshake(
        args[1],
        args[2]);
    return await CpuPowerConnectedLifecycleProbeCommand.RunAsync(
        new WindowsRzAmdOverclockProbe(),
        handshake.WaitForIsolationAsync,
        Console.Out,
        CancellationToken.None);
}

if (args.Length == 1 && args[0].Equals(
    "query-rz09-0528-02c6-production-performance-fan",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
    IProductionBatteryPortFactory factory =
        BatteryValidationFactory.CreateWindowsRz090528ProductionBattery();
    HidSessionResult<IProductionBatteryPort> opened =
        await factory.OpenAsync(CancellationToken.None);
    if (opened.Value is not { } port || opened.Failure is not null)
    {
        Console.WriteLine($"OpenFailure={opened.Failure?.ToString() ?? "Unavailable"}");
        Console.WriteLine("Success=False");
        return 2;
    }

    await using (port)
    {
        try
        {
            ProductionPerformanceFanState state =
                await port.QueryPerformanceFanStateAsync(CancellationToken.None);
            Console.WriteLine(
                $"Performance={state.Performance?.ToString() ?? "Unavailable"}");
            Console.WriteLine($"Fans={state.Fans?.ToString() ?? "Unavailable"}");
            Console.WriteLine(
                $"CustomCpu={state.CustomPerformance?.Cpu.ToString() ?? "Unavailable"}");
            Console.WriteLine(
                $"CustomGpu={state.CustomPerformance?.Gpu.ToString() ?? "Unavailable"}");
            Console.WriteLine("Success=True");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Failure={exception.GetType().Name}");
            Console.WriteLine($"Message={exception.Message}");
            Console.WriteLine("Success=False");
            return 2;
        }
    }
}

if (args.Length > 0 && args[0].Equals(
    "validate-rz09-0528-02c6-custom-performance",
    StringComparison.OrdinalIgnoreCase))
{
    const string exactConfirmation = "RZ09-0528:1532:02C6:2.02";
    if (args.Length != 4
        || !args[1].Equals(exactConfirmation, StringComparison.Ordinal)
        || !Enum.TryParse(args[2], ignoreCase: true, out CustomPerformanceLevel cpu)
        || !Enum.IsDefined(cpu)
        || !Enum.TryParse(args[3], ignoreCase: true, out CustomPerformanceLevel gpu)
        || !Enum.IsDefined(gpu))
    {
        Console.Error.WriteLine(
            $"Usage: OpenBlade.Capture validate-rz09-0528-02c6-custom-performance " +
            $"{exactConfirmation} <low|medium|high> <low|medium|high>");
        return 2;
    }

    CustomPerformanceSettings target;
    try
    {
        target = new CustomPerformanceSettings(cpu, gpu);
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine($"Target rejected: {exception.Message}");
        return 2;
    }

    IProductionBatteryPortFactory factory =
        BatteryValidationFactory.CreateWindowsRz090528ProductionBattery();
    if (factory.Capabilities.CustomPerformance?.Supports(target) != true)
    {
        Console.Error.WriteLine(
            "The requested CPU/GPU pair is not admitted for this exact device.");
        return 2;
    }

    HidSessionResult<IProductionBatteryPort> opened =
        await factory.OpenAsync(CancellationToken.None);
    if (opened.Value is not { } port || opened.Failure is not null)
    {
        if (opened.Value is not null)
        {
            await opened.Value.DisposeAsync();
        }

        Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
        Console.WriteLine($"OpenFailure={opened.Failure?.ToString() ?? "Unavailable"}");
        Console.WriteLine("RestorationConfirmed=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    await using (port)
    {
        ProductionPerformanceFanState? baselineState = null;
        bool applyStarted = false;
        try
        {
            ProductionPerformanceFanState baseline =
                await port.QueryPerformanceFanStateAsync(CancellationToken.None);
            baselineState = baseline;
            if (baseline.Performance is null || baseline.Fans != FanMode.Automatic)
            {
                throw new InvalidOperationException(
                    "Validation requires a confirmed named performance preset with Automatic fans.");
            }

            applyStarted = true;
            await port.SetCustomPerformanceAsync(target, CancellationToken.None);
            ProductionPerformanceFanState applied =
                await port.QueryPerformanceFanStateAsync(CancellationToken.None);
            if (applied.Performance != PerformancePreset.Custom
                || applied.Fans != FanMode.Automatic
                || applied.CustomPerformance != target)
            {
                throw new InvalidOperationException(
                    "The applied Custom state did not match the requested CPU/GPU pair.");
            }

            if (baseline.Performance == PerformancePreset.Custom)
            {
                await port.SetCustomPerformanceAsync(
                    baseline.CustomPerformance
                        ?? throw new InvalidOperationException(
                            "The baseline Custom state did not include component readback."),
                    CancellationToken.None);
            }
            else
            {
                await port.SetPerformanceAsync(
                    baseline.Performance.Value,
                    CancellationToken.None);
            }

            ProductionPerformanceFanState restored =
                await port.QueryPerformanceFanStateAsync(CancellationToken.None);
            bool restorationConfirmed =
                restored.Performance == baseline.Performance
                && restored.Fans == baseline.Fans
                && (baseline.Performance != PerformancePreset.Custom
                    || restored.CustomPerformance == baseline.CustomPerformance);
            Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
            Console.WriteLine(
                $"Baseline={baseline.Performance}/{baseline.Fans}/" +
                $"{baseline.CustomPerformance?.Cpu.ToString() ?? "N/A"}/" +
                $"{baseline.CustomPerformance?.Gpu.ToString() ?? "N/A"}");
            Console.WriteLine($"Applied=Custom/Automatic/{cpu}/{gpu}");
            Console.WriteLine(
                $"Restored={restored.Performance}/{restored.Fans}/" +
                $"{restored.CustomPerformance?.Cpu.ToString() ?? "N/A"}/" +
                $"{restored.CustomPerformance?.Gpu.ToString() ?? "N/A"}");
            Console.WriteLine($"RestorationConfirmed={restorationConfirmed}");
            Console.WriteLine($"Success={restorationConfirmed}");
            return restorationConfirmed ? 0 : 2;
        }
        catch (Exception exception)
        {
            bool restorationConfirmed = false;
            if (applyStarted
                && baselineState is { } baseline
                && baseline.Performance is { } baselinePreset)
            {
                try
                {
                    if (baselinePreset == PerformancePreset.Custom
                        && baseline.CustomPerformance is { } baselineCustom)
                    {
                        await port.SetCustomPerformanceAsync(
                            baselineCustom,
                            CancellationToken.None);
                    }
                    else
                    {
                        await port.SetPerformanceAsync(
                            baselinePreset,
                            CancellationToken.None);
                    }

                    ProductionPerformanceFanState restored =
                        await port.QueryPerformanceFanStateAsync(CancellationToken.None);
                    restorationConfirmed =
                        restored.Performance == baselinePreset
                        && restored.Fans == baseline.Fans
                        && (baselinePreset != PerformancePreset.Custom
                            || restored.CustomPerformance == baseline.CustomPerformance);
                }
                catch
                {
                    restorationConfirmed = false;
                }
            }

            Console.WriteLine("Target=RZ09-0528 1532:02C6 BIOS 2.02");
            Console.WriteLine($"Failure={exception.GetType().Name}");
            Console.WriteLine($"Message={exception.Message}");
            Console.WriteLine($"RestorationConfirmed={restorationConfirmed}");
            Console.WriteLine("Success=False");
            return 2;
        }
    }
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
    "validate-rz09-0528-02c6-device-mode",
    StringComparison.OrdinalIgnoreCase))
{
    IDeviceModeValidationRunner runner =
        GetterCandidateProbeFactory.CreateDeviceModeValidationRz090528Windows();
    var command = new DeviceModeValidationCommand(
        runner.RunAsync,
        Console.In,
        Console.Out,
        target: DeviceModeValidationTarget.Rz090528);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "query-rz09-0581-02e0-bios-4.00-hyperboost-recovery",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("ReadOnly=True");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost || !exactDevice || !synapseInactive || !openBladeServiceInactive)
    {
        Console.WriteLine("QueryAttempted=False");
        return 2;
    }

    var runner = new HyperBoostRecoveryRunner(ProductionBatteryFactory.CreateWindows());
    var command = new HyperBoostRecoveryCommand(
        runner.QueryAsync,
        runner.RestoreBalancedAsync,
        Console.In,
        Console.Out);
    return await command.RunQueryAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "restore-rz09-0581-02e0-bios-4.00-hyperboost-balanced",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("Action=Restore Balanced only from freshly confirmed HyperBoost");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost || !exactDevice || !synapseInactive || !openBladeServiceInactive)
    {
        Console.WriteLine("RestoreAttempted=False");
        return 2;
    }

    var runner = new HyperBoostRecoveryRunner(ProductionBatteryFactory.CreateWindows());
    var command = new HyperBoostRecoveryCommand(
        runner.QueryAsync,
        runner.RestoreBalancedAsync,
        Console.In,
        Console.Out);
    return await command.RunRestoreAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent-adapter-transition",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    ReviewedHyperBoostAdapterTransitionPlan plan =
        ReviewedHyperBoostAdapterTransitionPlan.LoadTrusted();
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("RequiredPad=physically absent; USB and external power disconnected");
    Console.WriteLine(
        "Action=Confirm HyperBoost on an admitted full-power adapter, observe " +
        "one fresh typed transition to reviewed USB-C 100 W, then restore Balanced immediately");
    Console.WriteLine("Workload=None");
    Console.WriteLine($"Plan={plan.PlanId}");
    Console.WriteLine($"PlanSha256={plan.SourceSha256}");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine("PadlessProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactDevice
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    ICoolingPadPhysicalPresenceSource physicalPresence =
        WindowsHidEnumerator.CreateCoolingPadPhysicalPresenceSource();
    IHyperBoostAdapterTransitionValidationRunner runner =
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400PadAbsentHyperBoostAdapterTransitionValidation(
                plan);
    var command = new PadAbsentHyperBoostAdapterTransitionValidationCommand(
        plan,
        physicalPresence.Observe,
        runner.RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

bool powerSourceBoundaryUsbC = args.Length > 0 && args[0].Equals(
    "validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent-auto-exit-usb-c",
    StringComparison.OrdinalIgnoreCase);
bool powerSourceBoundaryBattery = args.Length > 0 && args[0].Equals(
    "validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent-auto-exit-battery",
    StringComparison.OrdinalIgnoreCase);
if (powerSourceBoundaryUsbC || powerSourceBoundaryBattery)
{
    AdapterPowerState destinationAdapterState = powerSourceBoundaryUsbC
        ? AdapterPowerState.UsbCPower100W
        : AdapterPowerState.Battery;
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    ReviewedHyperBoostPowerSourceBoundaryPlan plan =
        ReviewedHyperBoostPowerSourceBoundaryPlan.LoadTrusted();
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("RequiredPad=physically absent; USB and external power disconnected");
    Console.WriteLine($"DestinationPower={destinationAdapterState}");
    Console.WriteLine(
        "Action=Confirm HyperBoost on admitted full power, change power source, " +
        "observe paired firmware state without an OpenBlade disable, reconnect full " +
        "power, and perform at most one exact Balanced cleanup if still required");
    Console.WriteLine("Workload=None");
    Console.WriteLine($"Plan={plan.PlanId}");
    Console.WriteLine($"PlanSha256={plan.SourceSha256}");
    Console.WriteLine("ProductionBehaviorChanged=False");
    Console.WriteLine("HyperBoostPowerTransitionPolicyChanged=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactDevice
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    ICoolingPadPhysicalPresenceSource physicalPresence =
        WindowsHidEnumerator.CreateCoolingPadPhysicalPresenceSource();
    IHyperBoostPowerSourceBoundaryValidationRunner runner =
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400PadAbsentHyperBoostPowerSourceBoundaryValidation(
                plan);
    var command = new PadAbsentHyperBoostPowerSourceBoundaryValidationCommand(
        plan,
        destinationAdapterState,
        physicalPresence.Observe,
        runner.RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent-reviewed-load",
    StringComparison.OrdinalIgnoreCase))
{
    ReviewedHyperBoostThermalPlan plan;
    try
    {
        plan = ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentExternalLoad();
    }
    catch (UnvalidatedCommandException exception)
    {
        Console.WriteLine($"TrustedPlanAvailable=False Reason={exception.Message}");
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    if (args.Length != 5
        || !args[3].Equals("--workload-pid", StringComparison.Ordinal)
        || !int.TryParse(args[4], out int workloadPid)
        || workloadPid <= 0)
    {
        Console.WriteLine(
            "Usage: OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-" +
            "hyperboost-pad-absent-reviewed-load --confirm-target " +
            PadAbsentHyperBoostSustainedValidationCommand
                .ReviewedLoadTargetConfirmation + " --workload-pid <pid>");
        return 1;
    }

    var workloadSource = new ExternallyLaunchedHyperBoostWorkloadSource(workloadPid);
    HyperBoostExternalWorkloadIdentity? workloadIdentity =
        workloadSource.CaptureIdentity();
    if (workloadIdentity is not { } exactWorkload)
    {
        Console.WriteLine("WorkloadIdentityConfirmed=False");
        Console.WriteLine("ValidationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    try
    {
        plan = plan.BindRuntimeExactExecutable(exactWorkload);
    }
    catch (UnvalidatedCommandException exception)
    {
        Console.WriteLine($"WorkloadIdentityConfirmed=False Reason={exception.Message}");
        Console.WriteLine("ValidationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("RequiredPad=physically absent; USB and external power disconnected");
    Console.WriteLine(
        $"Action={plan.DurationSeconds}-second monitored exact-hash external workload, then exact Balanced restoration");
    Console.WriteLine($"Plan={plan.PlanId}");
    Console.WriteLine($"PlanSha256={plan.SourceSha256}");
    Console.WriteLine($"Workload={plan.WorkloadName}");
    Console.WriteLine($"WorkloadSha256={plan.WorkloadSha256}");
    Console.WriteLine("WorkloadIdentityConfirmed=True");
    Console.WriteLine("WorkloadOwnership=ExternallyLaunchedAndOperatorOwned");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine("PadlessProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactDevice
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    ICoolingPadPhysicalPresenceSource physicalPresence =
        WindowsHidEnumerator.CreateCoolingPadPhysicalPresenceSource();
    using var temperatureSource = new SafeWindowsFanTemperatureSource();
    var telemetrySource = new HyperBoostThermalTelemetrySource(temperatureSource);
    IPerformanceFanSetterValidationRunner runner =
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400PadAbsentHyperBoostReviewedLoadValidation(
                plan,
                telemetrySource,
                workloadSource);
    var command = new PadAbsentHyperBoostSustainedValidationCommand(
        plan,
        physicalPresence.Observe,
        runner.RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent-sustained",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    ReviewedHyperBoostThermalPlan plan =
        ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentNoLoad();
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("RequiredPad=physically absent; USB and external power disconnected");
    Console.WriteLine(
        $"Action={plan.DurationSeconds}-second monitored HyperBoost no-load dwell, then exact Balanced restoration");
    Console.WriteLine($"Plan={plan.PlanId}");
    Console.WriteLine($"PlanSha256={plan.SourceSha256}");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine("PadlessProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactDevice
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    ICoolingPadPhysicalPresenceSource physicalPresence =
        WindowsHidEnumerator.CreateCoolingPadPhysicalPresenceSource();
    using var temperatureSource = new SafeWindowsFanTemperatureSource();
    var telemetrySource = new HyperBoostThermalTelemetrySource(temperatureSource);
    IPerformanceFanSetterValidationRunner runner =
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400PadAbsentHyperBoostSustainedValidation(
                plan,
                telemetrySource);
    var command = new PadAbsentHyperBoostSustainedValidationCommand(
        plan,
        physicalPresence.Observe,
        runner.RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("RequiredPad=physically absent; USB and external power disconnected");
    Console.WriteLine("Action=One immediate HyperBoost apply/readback/restore; no dwell or load");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine("PadlessProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactDevice
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    ICoolingPadPhysicalPresenceSource physicalPresence =
        WindowsHidEnumerator.CreateCoolingPadPhysicalPresenceSource();
    IPerformanceFanSetterValidationRunner runner =
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400PadAbsentHyperBoostSetterValidation();
    var command = new PadAbsentHyperBoostValidationCommand(
        physicalPresence.Observe,
        runner.RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-rz09-0581-02e0-bios-4.00-hyperboost",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("RequiredPad=1532:0F43 rev 0200 Auto");
    Console.WriteLine("RequiredAdapter=FullPower280W");
    Console.WriteLine(
        "PadObservation=Fresh opaque zero required as a conservative compatibility gate; it does not confirm Auto, active mode, target, or RPM");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactDevice
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    IPerformanceFanSetterValidationRunner runner =
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400GuardedHyperBoostSetterValidation();
    var command = new PerformanceFanSetterValidationCommand(
        runner.RunAsync,
        Console.Out,
        allowHyperBoost: true);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0
    && (args[0].Equals(
            "validate-rz09-0581-02e0-bios-4.00-hyperboost-from-quiet",
            StringComparison.OrdinalIgnoreCase)
        || args[0].Equals(
            "validate-rz09-0581-02e0-bios-4.00-hyperboost-from-performance",
            StringComparison.OrdinalIgnoreCase)))
{
    bool quiet = args[0].EndsWith("from-quiet", StringComparison.OrdinalIgnoreCase);
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    string baseMode = quiet ? "Quiet" : "Performance";
    Console.WriteLine("Target=RZ09-0581 1532:02E0 BIOS 4.00");
    Console.WriteLine("RequiredPad=1532:0F43 rev 0200 Auto");
    Console.WriteLine("RequiredAdapter=FullPower280W");
    Console.WriteLine(
        "PadObservation=Fresh opaque zero required as a conservative compatibility gate; it does not confirm Auto, active mode, target, or RPM");
    Console.WriteLine(
        $"Action=Require exact {baseMode} + Automatic baseline; write one HyperBoost pair; read back mode 7; restore {baseMode} directly; read back the exact baseline");
    Console.WriteLine("BalancedIntermediaryAllowed=False");
    Console.WriteLine("RetryAllowed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactDevice
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        Console.WriteLine("Success=False");
        return 2;
    }

    IPerformanceFanSetterValidationRunner runner =
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400GuardedHyperBoostSetterValidation();
    var command = new PerformanceFanSetterValidationCommand(
        runner.RunAsync,
        Console.Out,
        allowHyperBoost: true,
        allowDirectHyperBoostBaseModes: true);
    string target = quiet
        ? "hyperboost-from-quiet"
        : "hyperboost-from-performance";
    return await command.RunAsync(
        [target, .. args[1..]],
        CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-cooling-pad-fixed-write-once",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine("Action=One Fixed 2200 write; no automatic cleanup write");
    Console.WriteLine("RequiredRecovery=Restore Auto through Synapse");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    var command = new CoolingPadFixedWriteTrialCommand(
        CoolingPadFixedWriteTrialRunner.CreateWindows().RunAsync,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-cooling-pad-host-control-once",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine("Action=One Synapse-Auto-state write immediately followed by one host-demand 2200 write");
    Console.WriteLine("RequiredRecovery=Physically assess, then restore the Auto UI state through Synapse");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    var command = new CoolingPadHostControlWriteTrialCommand(
        CoolingPadHostControlWriteTrialRunner.CreateWindows().RunAsync,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-cooling-pad-lighting-lifecycle",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine(
        "Action=Query brightness; submit one uniform 123456 frame; set brightness 0 once; restore the exact baseline brightness once");
    Console.WriteLine(
        "RequiredRecovery=Restore the prior lighting preset through Synapse or the physical lighting button");
    Console.WriteLine("RetryAllowed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    var command = new CoolingPadLightingLifecycleValidationCommand(
        CoolingPadLightingLifecycleValidationRunner.CreateWindows().RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-cooling-pad-lighting-cadence",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine(
        "Action=Query brightness; submit 200 identical whole-strip 123456 frames at no more than 20 fps; restore the exact baseline brightness once");
    Console.WriteLine(
        "RequiredRecovery=Restore the prior lighting preset through Synapse or the physical lighting button");
    Console.WriteLine("RetryAllowed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    var command = new CoolingPadLightingCadenceValidationCommand(
        CoolingPadLightingCadenceValidationRunner.CreateWindows().RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-cooling-pad-lighting-handback",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine(
        "Action=Confirm a distinctive physical preset; query brightness; submit one uniform 123456 frame; restore the exact baseline brightness once; close the pad handle; check whether the prior preset resumes");
    Console.WriteLine(
        "RequiredRecovery=If the prior preset does not resume, restore it through Synapse or the physical lighting button");
    Console.WriteLine("RetryAllowed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    var command = new CoolingPadLightingHandbackValidationCommand(
        CoolingPadLightingHandbackValidationRunner.CreateWindows().RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-cooling-pad-fan-cadence",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine(
        "Action=Acquire host control at 500 RPM; submit seven 1 Hz target updates; physically confirm cadence; submit one release request");
    Console.WriteLine(
        "ReadbackBoundary=Physical confirmation only; active mode and physical RPM cannot be read independently");
    Console.WriteLine("RetryAllowed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    var command = new CoolingPadFanCadenceValidationCommand(
        CoolingPadFanCadenceValidationRunner.CreateWindows().RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-cooling-pad-fan-lifecycle",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine(
        "Action=Acquire host control at 500 RPM; update once to 3200 RPM; update once to 2200 RPM; submit one release request");
    Console.WriteLine(
        "ReadbackBoundary=Physical confirmation only; active mode and physical RPM cannot be read independently");
    Console.WriteLine("RetryAllowed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    var command = new CoolingPadFanLifecycleValidationCommand(
        CoolingPadFanLifecycleValidationRunner.CreateWindows().RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && (
    args[0].Equals(
        "validate-cooling-pad-fan-crash-arm",
        StringComparison.OrdinalIgnoreCase)
    || args[0].Equals(
        "validate-cooling-pad-fan-crash-recover",
        StringComparison.OrdinalIgnoreCase)))
{
    bool recoverPhase = args[0].EndsWith(
        "-recover",
        StringComparison.OrdinalIgnoreCase);
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine($"Phase={(recoverPhase ? "Recover" : "Arm")}");
    Console.WriteLine(
        "Action=Apply Fixed 2200 and leave an exact-build marker after the " +
        "handle closes, or confirm the originating process exited and submit " +
        "one physically confirmed release request");
    Console.WriteLine(
        "ReadbackBoundary=Physical confirmation only; active mode and physical RPM cannot be read independently");
    Console.WriteLine("RetryAllowed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    CoolingPadFanCrashValidationRunner runner =
        CoolingPadFanCrashValidationRunner.CreateWindows();
    var command = new CoolingPadFanCrashValidationCommand(
        runner.ArmAsync,
        runner.RecoverAsync,
        Console.In,
        Console.Out);
    return recoverPhase
        ? await command.RunRecoveryAsync(args[1..], CancellationToken.None)
        : await command.RunArmAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-cooling-pad-fan-hyperboost-interlock",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    bool exactDevice =
        GpuClockOffsetValidationEnvironment.HasOneExactRz090581ProtocolCollection();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine(
        "Target=RZ09-0581 1532:02E0 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine(
        "Action=Acquire Fixed 2200; physically confirm; release once; physically confirm handback; then run guarded HyperBoost apply/readback/Balanced restoration");
    Console.WriteLine(
        "ReadbackBoundary=Pad control is physically confirmed; laptop HyperBoost and Balanced restoration use paired typed readback");
    Console.WriteLine("RetryAllowed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactDevice={exactDevice}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactDevice
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    CoolingPadFanCrashValidationRunner fanRunner =
        CoolingPadFanCrashValidationRunner.CreateWindows();
    IPerformanceFanSetterValidationRunner hyperBoostRunner =
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400GuardedHyperBoostSetterValidation();
    var command = new CoolingPadHyperBoostInterlockValidationCommand(
        fanRunner.ArmAsync,
        fanRunner.RecoverAsync,
        hyperBoostRunner.RunAsync,
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-cooling-pad-fan-physical-button-ownership",
    StringComparison.OrdinalIgnoreCase))
{
    bool exactHost =
        GpuClockOffsetValidationEnvironment.IsExactRz090581Bios400Host();
    IReadOnlyList<HidInterfaceInfo> padCollections =
        WindowsHidEnumerator.FindCollections(0x1532, 0x0F43);
    bool exactPad = padCollections.Count(collection =>
        collection.UsagePage == 0x000C
        && collection.Usage == 0x0001
        && collection.FeatureReportByteLength == 91
        && collection.VersionNumber == 0x0200) == 1;
    bool synapseInactive =
        GpuClockOffsetValidationEnvironment.IsSynapseInactive();
    bool openBladeServiceInactive =
        GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive();
    Console.WriteLine("Target=RZ09-0581 BIOS 4.00; pad 1532:0F43 rev 0200");
    Console.WriteLine(
        "Action=Acquire Fixed 2200; physically confirm; press the pad fan button once; confirm whether host ownership retains 2200; release once only from the retained state");
    Console.WriteLine(
        "ReadbackBoundary=Physical confirmation only; active mode and physical RPM cannot be read independently");
    Console.WriteLine("RetryAllowed=False");
    Console.WriteLine("ProductionAdmitted=False");
    Console.WriteLine($"ExactHost={exactHost}");
    Console.WriteLine($"ExactPad={exactPad}");
    Console.WriteLine($"SynapseInactive={synapseInactive}");
    Console.WriteLine($"OpenBladeServiceInactive={openBladeServiceInactive}");
    if (!exactHost
        || !exactPad
        || !synapseInactive
        || !openBladeServiceInactive)
    {
        Console.WriteLine("ValidationAttempted=False");
        return 2;
    }

    CoolingPadFanCrashValidationRunner runner =
        CoolingPadFanCrashValidationRunner.CreateWindows();
    var command = new CoolingPadFanPhysicalButtonOwnershipValidationCommand(
        runner.ArmAsync,
        runner.RecoverAsync,
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

if (args.Length > 0 && args[0].Equals(
    "validate-rz09-0528-02c6-lighting",
    StringComparison.OrdinalIgnoreCase))
{
    var command = Rz090528LightingValidationCommand.CreateWindows(
        Console.In,
        Console.Out);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

if (args.Length > 0 && args[0].Equals(
    "validate-rz09-0581-lighting-cadence",
    StringComparison.OrdinalIgnoreCase))
{
    var command = Rz090581LightingCadenceValidationCommand.CreateWindows(
        Console.In,
        Console.Out);
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
Console.WriteLine(
    "  OpenBlade.Capture validate-display-hdr WINDOWS-INTERNAL-DISPLAY:RESTORE");
Console.WriteLine("  OpenBlade.Capture probe-gpu-clock-offsets");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0528-02c6-gpu-clock-offsets " +
    GpuClockOffsetValidationEnvironment.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-gpu-clock-offsets " +
    GpuClockOffsetValidationEnvironment.Rz090581Bios400Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture query-rz09-0581-02e0-bios-4.00-performance");
Console.WriteLine(
    "  OpenBlade.Capture query-rz09-0581-02e0-bios-4.00-hyperboost-state");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-hyperboost-from-quiet " +
    "--confirm-target " +
    PerformanceFanSetterValidationCommand.HyperBoostFromQuietConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-hyperboost-from-performance " +
    "--confirm-target " +
    PerformanceFanSetterValidationCommand.HyperBoostFromPerformanceConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent " +
    "--confirm-target " +
    PadAbsentHyperBoostValidationCommand.ExactTargetConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent-sustained " +
    "--confirm-target " +
    PadAbsentHyperBoostSustainedValidationCommand.ExactTargetConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent-reviewed-load " +
    "--confirm-target " +
    PadAbsentHyperBoostSustainedValidationCommand.ReviewedLoadTargetConfirmation +
    " --workload-pid <pid> (binds and continuously rechecks the running executable name and SHA-256)");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent-adapter-transition " +
    "--confirm-target " +
    PadAbsentHyperBoostAdapterTransitionValidationCommand.ExactTargetConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent-auto-exit-usb-c " +
    "--confirm-target " +
    PadAbsentHyperBoostPowerSourceBoundaryValidationCommand.UsbCTargetConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent-auto-exit-battery " +
    "--confirm-target " +
    PadAbsentHyperBoostPowerSourceBoundaryValidationCommand.BatteryTargetConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-lighting-lifecycle --confirm-target " +
    CoolingPadLightingLifecycleValidationCommand.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-lighting-cadence --confirm-target " +
    CoolingPadLightingCadenceValidationCommand.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-lighting-handback --confirm-target " +
    CoolingPadLightingHandbackValidationCommand.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-fan-cadence --confirm-target " +
    CoolingPadFanCadenceValidationCommand.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-fan-lifecycle --confirm-target " +
    CoolingPadFanLifecycleValidationCommand.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-fan-crash-arm --confirm-target " +
    CoolingPadFanCrashValidationCommand.ArmConfirmation +
    " --marker <private-marker-path>");
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-fan-crash-recover --confirm-target " +
    CoolingPadFanCrashValidationCommand.RecoveryConfirmation +
    " --marker <private-marker-path>");
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-fan-hyperboost-interlock " +
    "--confirm-target " +
    CoolingPadHyperBoostInterlockValidationCommand.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-fan-physical-button-ownership " +
    "--confirm-target " +
    CoolingPadFanPhysicalButtonOwnershipValidationCommand.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture correlate-cooling-pad-fan-button --confirm-target " +
    CoolingPadFanButtonCorrelationCommand.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-fan-disconnect-reconnect " +
    "--confirm-target " +
    CoolingPadFanBoundaryValidationCommand.DisconnectReconnectConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-fan-low-power-resume " +
    "--confirm-target " +
    CoolingPadFanBoundaryValidationCommand.LowPowerResumeConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-lighting-disconnect-reconnect " +
    "--confirm-target " +
    CoolingPadLightingBoundaryValidationCommand.DisconnectReconnectConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-lighting-low-power-resume " +
    "--confirm-target " +
    CoolingPadLightingBoundaryValidationCommand.LowPowerResumeConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-lighting-controller-handoff " +
    "--confirm-target " +
    CoolingPadLightingBoundaryValidationCommand.ControllerHandoffConfirmation);
Console.WriteLine("  OpenBlade.Capture probe-special-keys [seconds] [output-file]");
Console.WriteLine(
    "  OpenBlade.Capture probe-rz09-0528-02c6-special-keys [seconds] [output-file]");
Console.WriteLine(
    "  OpenBlade.Capture probe-rz09-0528-02c6-hid-input-access");
Console.WriteLine(
    "  OpenBlade.Capture probe-rz09-0528-02c6-lamp-array");
Console.WriteLine("  OpenBlade.Capture validate <fixtures.json>");
Console.WriteLine("  OpenBlade.Capture probe-battery");
Console.WriteLine("  OpenBlade.Capture query-battery");
Console.WriteLine("  OpenBlade.Capture query-rz09-0528-02c6-battery");
Console.WriteLine("  OpenBlade.Capture query-rz09-0528-02c6-battery-raw");
Console.WriteLine("  OpenBlade.Capture validate-rz09-0528-02c6-battery <50|55|60|65|70|75|80>");
Console.WriteLine("  OpenBlade.Capture probe-rz09-0528-02c6-production-battery");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0528-02c6-one-shot " +
    "<startup|function|gaming> RZ09-0528:1532:02C6:2.02");
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
Console.WriteLine(
    "  OpenBlade.Capture query-rz09-0528-02c6-power-source");
Console.WriteLine(
    "  OpenBlade.Capture query-rz09-0528-02c6-cpu-power-service");
Console.WriteLine(
    "  OpenBlade.Capture query-rz09-0528-02c6-cpu-power-service-preconnected " +
    "<ready-event> <isolated-event>");
Console.WriteLine(
    "  OpenBlade.Capture query-rz09-0528-02c6-production-performance-fan");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0528-02c6-custom-performance " +
    "RZ09-0528:1532:02C6:2.02 <low|medium|high> <low|medium|high>");
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
    "  OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-hyperboost " +
    "hyperboost --confirm-target " +
    PerformanceFanSetterValidationCommand.HyperBoostExactTargetConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-fixed-write-once " +
    "--confirm-target " + CoolingPadFixedWriteTrialCommand.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-cooling-pad-host-control-once " +
    "--confirm-target " + CoolingPadHostControlWriteTrialCommand.Confirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-lighting --confirm-target " +
    LightingValidationCommand.ExactTargetConfirmation +
    " --brightness <1-100>");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0528-02c6-lighting --confirm-target " +
    Rz090528LightingValidationCommand.ExactTargetConfirmation);
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0581-lighting-cadence " +
    "--confirm-target RZ09-0581:1532:02E0:<BIOS>:000C:0001:375 " +
    "--rates 30,45,60 --pattern <marker|tidal> " +
    "--brightness <1-100> --seconds <5-60> " +
    "--output <directory>");
Console.WriteLine("  OpenBlade.Capture probe-battery-sequence");
Console.WriteLine("  OpenBlade.Capture validate-battery <off|50|55|60|65|70|75|80>");
Console.WriteLine("  OpenBlade.Capture validate-protection-off");
Console.WriteLine("  OpenBlade.Capture validate-temporary-full-charge");
Console.WriteLine(
    "  OpenBlade.Capture validate-rz09-0528-02c6-temporary-full-charge" +
    " RZ09-0528:1532:02C6:2.02");
Console.WriteLine("See captures/README.md for the gated USBPcap workflow.");
return args.Length == 0 ? 0 : 1;
