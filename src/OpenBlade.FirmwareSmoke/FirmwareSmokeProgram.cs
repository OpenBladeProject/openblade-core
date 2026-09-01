using OpenBlade.Contracts;

namespace OpenBlade.FirmwareSmoke;

public static class FirmwareSmokeProgram
{
    public const string TargetConfirmationFormat = "<MODEL>-FIRMWARE-SMOKE";

    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0].ToLowerInvariant();
        string outputDirectory = ReadOption(args, "--output")
            ?? DefaultOutputDirectory(command);
        try
        {
            await using var client = new FirmwareSmokeClient();
            var collector = new FirmwareSmokeSnapshotCollector(client);
            return command switch
            {
                "baseline" => await CaptureBaselineAsync(
                    outputDirectory,
                    collector,
                    cancellationToken).ConfigureAwait(false),
                "post-update" => await CompareAsync(
                    args,
                    outputDirectory,
                    collector,
                    ReadExpectedFirmwareChanges(args),
                    cancellationToken).ConfigureAwait(false),
                "rehearsal" => await RunReversibleAsync(
                    args,
                    outputDirectory,
                    collector,
                    client,
                    ReadExpectedFirmwareChanges(args),
                    isRehearsal: true,
                    cancellationToken).ConfigureAwait(false),
                "reversible" => await RunReversibleAsync(
                    args,
                    outputDirectory,
                    collector,
                    client,
                    ReadExpectedFirmwareChanges(args),
                    isRehearsal: false,
                    cancellationToken).ConfigureAwait(false),
                "recover" => await RecoverAsync(
                    args,
                    outputDirectory,
                    client,
                    cancellationToken).ConfigureAwait(false),
                "hyperboost-lifecycle-arm" => await RunHyperBoostLifecycleArmAsync(
                    args,
                    outputDirectory,
                    collector,
                    client,
                    cancellationToken).ConfigureAwait(false),
                "hyperboost-lifecycle-verify" =>
                    await RunHyperBoostLifecycleVerifyAsync(
                        args,
                        outputDirectory,
                        collector,
                        client,
                        cancellationToken).ConfigureAwait(false),
                "thermal-external-drift" =>
                    await RunThermalExternalDriftAsync(
                        args,
                        outputDirectory,
                        client,
                        cancellationToken).ConfigureAwait(false),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"Firmware smoke test failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> CaptureBaselineAsync(
        string outputDirectory,
        FirmwareSmokeSnapshotCollector collector,
        CancellationToken cancellationToken)
    {
        FirmwareSmokeSnapshot snapshot = await collector.CaptureAsync(
            cancellationToken).ConfigureAwait(false);
        FirmwareSmokeReport report = FirmwareSmokeAssessment.ValidateBaseline(snapshot);
        Directory.CreateDirectory(outputDirectory);
        string snapshotPath = Path.Combine(outputDirectory, "baseline.json");
        await FirmwareSmokeFiles.WriteJsonAsync(
            snapshotPath,
            snapshot,
            cancellationToken).ConfigureAwait(false);
        await SaveReportAsync(outputDirectory, "baseline", report, cancellationToken)
            .ConfigureAwait(false);

        DiagnosticArchive diagnostics = await collector.ExportDiagnosticsAsync(
            cancellationToken).ConfigureAwait(false);
        await WriteBytesAtomicAsync(
            Path.Combine(outputDirectory, "diagnostics.zip"),
            diagnostics.ZipBytes,
            cancellationToken).ConfigureAwait(false);

        PrintSummary(report, outputDirectory, snapshotPath);
        return report.Succeeded ? 0 : 2;
    }

    private static async Task<int> CompareAsync(
        string[] args,
        string outputDirectory,
        FirmwareSmokeSnapshotCollector collector,
        IReadOnlySet<FirmwareComponent> expectedChangedComponents,
        CancellationToken cancellationToken)
    {
        FirmwareSmokeSnapshot baseline = await ReadBaselineAsync(
            args,
            cancellationToken).ConfigureAwait(false);
        FirmwareSmokeSnapshot current = await collector.CaptureAsync(
            cancellationToken).ConfigureAwait(false);
        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            expectedChangedComponents);

        Directory.CreateDirectory(outputDirectory);
        string snapshotPath = Path.Combine(outputDirectory, "post-update.json");
        await FirmwareSmokeFiles.WriteJsonAsync(
            snapshotPath,
            current,
            cancellationToken).ConfigureAwait(false);
        await SaveReportAsync(
            outputDirectory,
            "post-update",
            report,
            cancellationToken).ConfigureAwait(false);
        DiagnosticArchive diagnostics = await collector.ExportDiagnosticsAsync(
            cancellationToken).ConfigureAwait(false);
        await WriteBytesAtomicAsync(
            Path.Combine(outputDirectory, "diagnostics.zip"),
            diagnostics.ZipBytes,
            cancellationToken).ConfigureAwait(false);

        PrintSummary(report, outputDirectory, snapshotPath);
        return report.Succeeded ? 0 : 2;
    }

    private static async Task<int> RunReversibleAsync(
        string[] args,
        string outputDirectory,
        FirmwareSmokeSnapshotCollector collector,
        IFirmwareSmokeClient client,
        IReadOnlySet<FirmwareComponent> componentsUnderTest,
        bool isRehearsal,
        CancellationToken cancellationToken)
    {
        FirmwareSmokeSnapshot baseline = await ReadBaselineAsync(
            args,
            cancellationToken).ConfigureAwait(false);
        string expectedConfirmation = TargetConfirmation(baseline.TargetModel);
        string? confirmation = ReadOption(args, "--confirm-target");
        if (!string.Equals(
            confirmation,
            expectedConfirmation,
            StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Reversible writes require --confirm-target " + expectedConfirmation);
            return 1;
        }

        FirmwareSmokeSnapshot current = await collector.CaptureAsync(
            cancellationToken).ConfigureAwait(false);
        FirmwareSmokeReport preflight = isRehearsal
            ? FirmwareSmokeAssessment.CompareRehearsal(
                baseline,
                current,
                componentsUnderTest)
            : FirmwareSmokeAssessment.ComparePostUpdate(
                baseline,
                current,
                componentsUnderTest);
        if (!preflight.Succeeded)
        {
            Directory.CreateDirectory(outputDirectory);
            await SaveReportAsync(
                outputDirectory,
                "reversible-preflight",
                preflight,
                cancellationToken).ConfigureAwait(false);
            PrintSummary(preflight, outputDirectory, null);
            Console.Error.WriteLine("No hardware writes were attempted.");
            return 2;
        }

        IReadOnlyList<FirmwareSmokeCheck> mutationChecks =
            await new ReversibleSmokeRunner(client).RunAsync(cancellationToken)
                .ConfigureAwait(false);
        var report = new FirmwareSmokeReport(
            isRehearsal
                ? "pre-update rehearsal"
                : "post-update reversible writes",
            DateTimeOffset.UtcNow,
            preflight.Checks.Concat(mutationChecks).ToArray(),
            FirmwareSmokeAssessment.ManualChecks);
        Directory.CreateDirectory(outputDirectory);
        await FirmwareSmokeFiles.WriteJsonAsync(
            Path.Combine(outputDirectory, "reversible-results.json"),
            report,
            cancellationToken).ConfigureAwait(false);
        await FirmwareSmokeFiles.WriteReportAsync(
            Path.Combine(outputDirectory, "reversible-report.md"),
            report,
            cancellationToken).ConfigureAwait(false);
        PrintSummary(report, outputDirectory, null);
        return report.Succeeded ? 0 : 2;
    }

    private static async Task<int> RecoverAsync(
        string[] args,
        string outputDirectory,
        IFirmwareSmokeClient client,
        CancellationToken cancellationToken)
    {
        FirmwareSmokeSnapshot baseline = await ReadBaselineAsync(
            args,
            cancellationToken).ConfigureAwait(false);
        string expectedConfirmation = TargetConfirmation(baseline.TargetModel);
        string? confirmation = ReadOption(args, "--confirm-target");
        if (!string.Equals(
            confirmation,
            expectedConfirmation,
            StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Baseline recovery requires --confirm-target " +
                expectedConfirmation);
            return 1;
        }

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new FirmwareSmokeRecovery(client).RestoreAsync(
                baseline,
                cancellationToken).ConfigureAwait(false);
        var report = new FirmwareSmokeReport(
            "baseline recovery",
            DateTimeOffset.UtcNow,
            checks,
            []);
        Directory.CreateDirectory(outputDirectory);
        await SaveReportAsync(
            outputDirectory,
            "recovery",
            report,
            cancellationToken).ConfigureAwait(false);
        PrintSummary(report, outputDirectory, null);
        return report.Succeeded ? 0 : 2;
    }

    private static async Task<int> RunHyperBoostLifecycleArmAsync(
        string[] args,
        string outputDirectory,
        FirmwareSmokeSnapshotCollector collector,
        IFirmwareSmokeClient client,
        CancellationToken cancellationToken)
    {
        FirmwareSmokeSnapshot baseline = await ReadBaselineAsync(
            args,
            cancellationToken).ConfigureAwait(false);
        HyperBoostLifecycleScenario scenario = ReadHyperBoostLifecycleScenario(args);
        if (!HasHyperBoostLifecycleConfirmation(args, baseline, scenario))
        {
            Console.Error.WriteLine(
                "HyperBoost lifecycle arming requires --confirm-target " +
                HyperBoostLifecycleConfirmation(baseline.TargetModel, scenario));
            return 1;
        }

        string markerPath = ReadOption(args, "--marker")
            ?? throw new ArgumentException("--marker <private-marker.json> is required.");
        PadlessHyperBoostLifecycleValidationBuildManifest? validationBuild = null;
        string? validationBuildPath = ReadOption(args, "--validation-build-manifest");
        if (validationBuildPath is not null)
        {
            validationBuild = await FirmwareSmokeFiles.ReadJsonAsync<
                    PadlessHyperBoostLifecycleValidationBuildManifest>(
                    validationBuildPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!validationBuild.IsWellFormed)
            {
                throw new InvalidDataException(
                    "The pad-less HyperBoost lifecycle validation-build manifest is malformed.");
            }
        }
        var runner = new HyperBoostLifecycleValidationRunner(client, collector);
        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
                baseline,
                scenario,
                markerPath,
                validationBuild,
                cancellationToken)
            .ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory);
        await FirmwareSmokeFiles.WriteJsonAsync(
            Path.Combine(outputDirectory, "hyperboost-lifecycle-arm.json"),
            result,
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine(result.Message);
        Console.WriteLine(
            "FirmwareSmoke will not initiate sleep, stop, terminate, or restart any process.");
        return result.Success ? 0 : 2;
    }

    private static async Task<int> RunHyperBoostLifecycleVerifyAsync(
        string[] args,
        string outputDirectory,
        FirmwareSmokeSnapshotCollector collector,
        IFirmwareSmokeClient client,
        CancellationToken cancellationToken)
    {
        FirmwareSmokeSnapshot baseline = await ReadBaselineAsync(
            args,
            cancellationToken).ConfigureAwait(false);
        HyperBoostLifecycleScenario scenario = ReadHyperBoostLifecycleScenario(args);
        if (!HasHyperBoostLifecycleConfirmation(args, baseline, scenario))
        {
            Console.Error.WriteLine(
                "HyperBoost lifecycle verification requires --confirm-target " +
                HyperBoostLifecycleConfirmation(baseline.TargetModel, scenario));
            return 1;
        }

        string markerPath = ReadOption(args, "--marker")
            ?? throw new ArgumentException("--marker <private-marker.json> is required.");
        var runner = new HyperBoostLifecycleValidationRunner(client, collector);
        HyperBoostLifecycleVerifyResult result = await runner.VerifyAsync(
                baseline,
                scenario,
                markerPath,
                cancellationToken)
            .ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory);
        await FirmwareSmokeFiles.WriteJsonAsync(
            Path.Combine(outputDirectory, "hyperboost-lifecycle-verify.json"),
            result,
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine(result.Message);
        Console.WriteLine(
            "Verification is read-only; FirmwareSmoke did not issue a recovery write.");
        return result.Success ? 0 : 2;
    }

    private static async Task<int> RunThermalExternalDriftAsync(
        string[] args,
        string outputDirectory,
        IFirmwareSmokeClient client,
        CancellationToken cancellationToken)
    {
        string manifestPath = ReadOption(args, "--validation-build-manifest")
            ?? throw new ArgumentException(
                "--validation-build-manifest <manifest.json> is required.");
        ThermalExternalDriftValidationBuildManifest manifest =
            await FirmwareSmokeFiles.ReadJsonAsync<
                    ThermalExternalDriftValidationBuildManifest>(
                    manifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!manifest.IsWellFormed)
        {
            throw new InvalidDataException(
                "The thermal external-drift validation-build manifest is malformed.");
        }

        ServiceStatusSnapshot current = await client.GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        string targetModel = current.Firmware?.ModelNumber ?? "unknown";
        string confirmation = ThermalExternalDriftConfirmation(targetModel);
        if (!string.Equals(
            ReadOption(args, "--confirm-target"),
            confirmation,
            StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Thermal external-drift validation requires --confirm-target "
                + confirmation);
            return 1;
        }

        ThermalExternalDriftValidationResult result =
            await new ThermalExternalDriftValidationRunner(client)
                .RunAsync(manifest, cancellationToken)
                .ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory);
        await FirmwareSmokeFiles.WriteJsonAsync(
            Path.Combine(outputDirectory, "thermal-external-drift.json"),
            result,
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine(result.Message);
        Console.WriteLine(
            "The tool never opens HID directly and does not initiate sleep, shutdown, service restart, or process termination.");
        return result.Success ? 0 : 2;
    }

    private static async Task<FirmwareSmokeSnapshot> ReadBaselineAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        string path = ReadOption(args, "--baseline")
            ?? throw new ArgumentException("--baseline <baseline.json> is required.");
        return await FirmwareSmokeFiles.ReadJsonAsync<FirmwareSmokeSnapshot>(
            path,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveReportAsync(
        string outputDirectory,
        string name,
        FirmwareSmokeReport report,
        CancellationToken cancellationToken)
    {
        await FirmwareSmokeFiles.WriteJsonAsync(
            Path.Combine(outputDirectory, name + "-report.json"),
            report,
            cancellationToken).ConfigureAwait(false);
        await FirmwareSmokeFiles.WriteReportAsync(
            Path.Combine(outputDirectory, name + "-report.md"),
            report,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBytesAtomicAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath,
                bytes,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int index = 1; index < args.Length; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
                {
                    throw new ArgumentException($"{name} requires a value.");
                }

                return args[index + 1];
            }
        }

        return null;
    }

    private static string DefaultOutputDirectory(string command)
    {
        string timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "firmware-smoke",
            $"{timestamp}-{command}");
    }

    public static string TargetConfirmation(string targetModel)
    {
        string canonical = RazerBladeModelCatalog.Find(targetModel)?.ModelNumber
            ?? targetModel.Trim().ToUpperInvariant();
        return $"{canonical}-FIRMWARE-SMOKE";
    }

    internal static string HyperBoostLifecycleConfirmation(
        string targetModel,
        HyperBoostLifecycleScenario scenario)
    {
        string canonical = RazerBladeModelCatalog.Find(targetModel)?.ModelNumber
            ?? targetModel.Trim().ToUpperInvariant();
        string boundary = scenario == HyperBoostLifecycleScenario.LowPower
            ? "LOW-POWER"
            : "UNEXPECTED-EXIT";
        return $"{canonical}-HYPERBOOST-{boundary}-LIFECYCLE";
    }

    internal static string ThermalExternalDriftConfirmation(string targetModel)
    {
        string canonical = RazerBladeModelCatalog.Find(targetModel)?.ModelNumber
            ?? targetModel.Trim().ToUpperInvariant();
        return $"{canonical}-THERMAL-EXTERNAL-DRIFT";
    }

    private static bool HasHyperBoostLifecycleConfirmation(
        string[] args,
        FirmwareSmokeSnapshot baseline,
        HyperBoostLifecycleScenario scenario) => string.Equals(
        ReadOption(args, "--confirm-target"),
        HyperBoostLifecycleConfirmation(baseline.TargetModel, scenario),
        StringComparison.Ordinal);

    internal static HyperBoostLifecycleScenario ParseHyperBoostLifecycleScenario(
        string value) => value.ToLowerInvariant() switch
        {
            "low-power" => HyperBoostLifecycleScenario.LowPower,
            "crash" or "unexpected-exit" =>
                HyperBoostLifecycleScenario.UnexpectedExit,
            _ => throw new ArgumentException(
                "--scenario must be low-power or unexpected-exit."),
        };

    private static HyperBoostLifecycleScenario ReadHyperBoostLifecycleScenario(
        string[] args) => ParseHyperBoostLifecycleScenario(
        ReadOption(args, "--scenario")
            ?? throw new ArgumentException(
                "--scenario <low-power|unexpected-exit> is required."));

    public static IReadOnlySet<FirmwareComponent> ParseExpectedFirmwareChanges(
        string value)
    {
        var components = new HashSet<FirmwareComponent>();
        foreach (string item in value.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse(item, ignoreCase: true, out FirmwareComponent component))
            {
                throw new ArgumentException(
                    $"Unsupported firmware component '{item}'. " +
                    "Expected BIOS, EC, MCU, or VBIOS.");
            }

            components.Add(component);
        }

        if (components.Count == 0)
        {
            throw new ArgumentException(
                "--expect-firmware-changes requires at least one component.");
        }

        return components;
    }

    private static IReadOnlySet<FirmwareComponent> ReadExpectedFirmwareChanges(
        string[] args)
    {
        string value = ReadOption(args, "--expect-firmware-changes")
            ?? throw new ArgumentException(
                "--expect-firmware-changes <BIOS,EC,MCU,VBIOS> is required.");
        return ParseExpectedFirmwareChanges(value);
    }

    private static void PrintSummary(
        FirmwareSmokeReport report,
        string outputDirectory,
        string? snapshotPath)
    {
        int passed = report.Checks.Count(check =>
            check.Outcome == FirmwareSmokeOutcome.Passed);
        int warnings = report.Checks.Count(check =>
            check.Outcome == FirmwareSmokeOutcome.Warning);
        int failed = report.Checks.Count(check =>
            check.Outcome == FirmwareSmokeOutcome.Failed);
        int skipped = report.Checks.Count(check =>
            check.Outcome == FirmwareSmokeOutcome.Skipped);
        Console.WriteLine(
            $"{report.Phase}: {(report.Succeeded ? "PASS" : "FAIL")} " +
            $"({passed} passed, {warnings} warnings, {failed} failed, {skipped} skipped)");
        if (snapshotPath is not null)
        {
            Console.WriteLine($"Snapshot: {Path.GetFullPath(snapshotPath)}");
        }

        Console.WriteLine($"Report directory: {Path.GetFullPath(outputDirectory)}");
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("OpenBlade firmware smoke utility");
        Console.WriteLine("  baseline [--output <directory>]");
        Console.WriteLine(
            "  post-update --baseline <baseline.json> " +
            "--expect-firmware-changes <components> [--output <directory>]");
        Console.WriteLine(
            "  rehearsal --baseline <baseline.json> --confirm-target " +
            TargetConfirmationFormat + " --expect-firmware-changes <components> " +
            "[--output <directory>]");
        Console.WriteLine(
            "  reversible --baseline <baseline.json> --confirm-target " +
            TargetConfirmationFormat + " --expect-firmware-changes <components> " +
            "[--output <directory>]");
        Console.WriteLine(
            "  recover --baseline <baseline.json> --confirm-target " +
            TargetConfirmationFormat + " [--output <directory>]");
        Console.WriteLine(
            "  hyperboost-lifecycle-arm --baseline <baseline.json> " +
            "--scenario <low-power|unexpected-exit> --marker <private-marker.json> " +
            "--confirm-target <MODEL>-HYPERBOOST-<BOUNDARY>-LIFECYCLE " +
            "[--validation-build-manifest <manifest.json>] " +
            "[--output <directory>]");
        Console.WriteLine(
            "  hyperboost-lifecycle-verify --baseline <baseline.json> " +
            "--scenario <low-power|unexpected-exit> --marker <private-marker.json> " +
            "--confirm-target <MODEL>-HYPERBOOST-<BOUNDARY>-LIFECYCLE " +
            "[--output <directory>]");
        Console.WriteLine(
            "  thermal-external-drift --validation-build-manifest <manifest.json> " +
            "--confirm-target <MODEL>-THERMAL-EXTERNAL-DRIFT " +
            "[--output <directory>]");
        Console.WriteLine();
        Console.WriteLine(
            "Components are a comma-separated subset of BIOS, EC, MCU, and VBIOS. " +
            "Rehearsal requires declared components to be readable and all firmware " +
            "versions to remain unchanged.");
    }
}
