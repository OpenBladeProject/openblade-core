using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal static class Rz090528GpuModeMatrixCommand
{
    internal static async Task<int> RunAsync(
        IProductionBatteryPort port,
        IGpuClockOffsetValidationController gpu,
        IGpuClockOffsetProbe independent,
        Func<bool> isolated,
        TextWriter output)
    {
        var token = CancellationToken.None;
        ProductionPerformanceFanState baseline;

        async Task GuardAsync()
        {
            if (!isolated())
            {
                throw new InvalidOperationException("Exact-device isolation is required.");
            }
            AdapterPowerState adapter = await port.QueryAdapterPowerStateAsync(token);
            output.WriteLine($"MatrixAdapter={adapter}");
            if (adapter != AdapterPowerState.FullPower280W || !isolated())
            {
                throw new InvalidOperationException("Exact-device isolation and full-power 280 W AC are required.");
            }
        }

        static string Format(ProductionPerformanceFanState state) =>
            $"{state.Performance}/{state.Fans}/Cpu={state.CustomPerformance?.Cpu.ToString() ?? "N/A"}"
            + $"/Gpu={state.CustomPerformance?.Gpu.ToString() ?? "N/A"}/HyperBoost={state.HyperBoostActive}";

        static bool Matches(ProductionPerformanceFanState actual, ProductionPerformanceFanState expected) =>
            actual.Performance == expected.Performance
            && actual.Fans == expected.Fans
            && !actual.HyperBoostActive
            && (expected.Performance != PerformancePreset.Custom
                || actual.CustomPerformance == expected.CustomPerformance);

        void RequireZeroGpu()
        {
            foreach (GpuClockOffsetCapabilities state in new[] { gpu.Query(), independent.Query() })
            {
                if (state.DeviceName != GpuClockOffsetValidationPlan.Rz090528UiStep.ExpectedDeviceName
                    || state.Core.CurrentMegahertz != 0 || state.Memory.CurrentMegahertz != 0)
                {
                    throw new InvalidOperationException("Both GPU APIs must confirm the exact GPU at zero offsets.");
                }
            }
        }

        try
        {
            await GuardAsync();
            baseline = await port.QueryPerformanceFanStateAsync(token);
            if (baseline.Performance is not (PerformancePreset.Balanced or PerformancePreset.Silent or PerformancePreset.Performance)
                || baseline.Fans != FanMode.Automatic || baseline.HyperBoostActive)
            {
                throw new InvalidOperationException("Matrix requires a named non-Custom preset with Automatic fans and no HyperBoost.");
            }
            RequireZeroGpu();
            output.WriteLine($"MatrixBaseline={Format(baseline)}");
        }
        catch (Exception exception)
        {
            output.WriteLine($"MatrixFailure={exception.Message}");
            output.WriteLine("MatrixSuccess=False");
            return 2;
        }

        foreach (PerformancePreset preset in new[] { PerformancePreset.Balanced, PerformancePreset.Silent, PerformancePreset.Performance, PerformancePreset.Custom })
        {
            var expected = new ProductionPerformanceFanState(preset, FanMode.Automatic)
            {
                CustomPerformance = preset == PerformancePreset.Custom
                    ? new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low) : null,
            };
            bool thermalAttempted = false;
            bool success = false;
            bool restored = false;
            output.WriteLine($"MatrixCase={preset}");
            try
            {
                RequireZeroGpu();
                await GuardAsync();
                if (!Matches(await port.QueryPerformanceFanStateAsync(token), baseline))
                {
                    throw new InvalidOperationException("Thermal baseline changed before the case.");
                }
                await GuardAsync();
                thermalAttempted = true;
                if (expected.CustomPerformance is { } custom)
                {
                    await port.SetCustomPerformanceAsync(custom, token);
                }
                else
                {
                    await port.SetPerformanceAsync(preset, token);
                }
                ProductionPerformanceFanState applied = await port.QueryPerformanceFanStateAsync(token);
                output.WriteLine($"MatrixApplied={Format(applied)}");
                if (!Matches(applied, expected))
                {
                    throw new InvalidOperationException("Requested thermal mode was not confirmed.");
                }

                int gpuGuardCalls = 0;
                bool modeDrift = false;
                bool GuardGpu()
                {
                    GuardAsync().GetAwaiter().GetResult();
                    ProductionPerformanceFanState observed = port.QueryPerformanceFanStateAsync(token).GetAwaiter().GetResult();
                    output.WriteLine($"MatrixModeBeforeGpu={Format(observed)}");
                    bool matched = Matches(observed, expected);
                    modeDrift |= !matched;
                    output.WriteLine($"MatrixModeBeforeGpu{(++gpuGuardCalls == 1 ? "Apply" : "Restore")}Confirmed={matched}");
                    // A mode change invalidates the case, but must not prevent a guarded
                    // return to saved zero GPU offsets. No further test case will run.
                    GuardAsync().GetAwaiter().GetResult();
                    return gpuGuardCalls > 1 || matched;
                }

                int gpuResult = GpuClockOffsetValidationCommand.Run(
                    gpu, output, GpuClockOffsetValidationPlan.Rz090528UiStep,
                    GuardGpu, independent);
                ProductionPerformanceFanState afterGpu = await port.QueryPerformanceFanStateAsync(token);
                output.WriteLine($"MatrixModeAfterGpuRestore={Format(afterGpu)}");
                bool modeConfirmed = Matches(afterGpu, expected);
                output.WriteLine($"MatrixModeAfterGpuRestoreConfirmed={modeConfirmed}");
                RequireZeroGpu();
                if (gpuResult != 0 || modeDrift || !modeConfirmed)
                {
                    throw new InvalidOperationException("GPU round trip or thermal mode confirmation failed.");
                }
                success = true;
            }
            catch (Exception exception)
            {
                output.WriteLine($"MatrixCaseFailure={exception.Message}");
            }
            finally
            {
                if (thermalAttempted)
                {
                    try
                    {
                        await GuardAsync();
                        await port.SetPerformanceAsync(baseline.Performance!.Value, token);
                        restored = Matches(await port.QueryPerformanceFanStateAsync(token), baseline);
                    }
                    catch (Exception exception)
                    {
                        output.WriteLine($"MatrixRestorationFailure={exception.Message}");
                    }
                }
                try
                {
                    ProductionPerformanceFanState reconciled = await port.QueryPerformanceFanStateAsync(token);
                    output.WriteLine($"MatrixReconciled={Format(reconciled)}");
                    restored &= Matches(reconciled, baseline);
                    RequireZeroGpu();
                }
                catch (Exception exception)
                {
                    restored = false;
                    output.WriteLine($"MatrixReconciliationFailure={exception.Message}");
                }
                output.WriteLine($"MatrixThermalRestored={restored}");
            }
            if (!success || !restored)
            {
                output.WriteLine("MatrixSuccess=False");
                return 2;
            }
        }
        output.WriteLine("MatrixSuccess=True");
        return 0;
    }
}
