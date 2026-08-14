using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class WindowsPowerModeController : IWindowsPowerModeController
{
    private static readonly Guid BestPowerEfficiencyGuid =
        new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BestPerformanceGuid =
        new("ded574b5-45a0-4f42-8737-46345c09c238");

    public Task<WindowsPowerModeApplyResult> ApplyAsync(
        PowerSource powerSource,
        WindowsPowerMode mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        try
        {
            bool dc = powerSource switch
            {
                PowerSource.Battery => true,
                PowerSource.PluggedIn or PowerSource.UsbC => false,
                _ => throw new ArgumentOutOfRangeException(nameof(powerSource)),
            };
            uint readResult = dc
                ? NativeMethods.PowerGetUserConfiguredDCPowerMode(out Guid current)
                : NativeMethods.PowerGetUserConfiguredACPowerMode(out current);
            if (readResult != 0)
            {
                return Task.FromResult(Failure(readResult, "read"));
            }

            Guid requested = ToGuid(mode);
            if (current == requested)
            {
                return Task.FromResult(new WindowsPowerModeApplyResult(
                    Succeeded: true,
                    Changed: false,
                    mode,
                    Error: null));
            }

            cancellationToken.ThrowIfCancellationRequested();
            uint writeResult = dc
                ? NativeMethods.PowerSetUserConfiguredDCPowerMode(in requested)
                : NativeMethods.PowerSetUserConfiguredACPowerMode(in requested);
            if (writeResult != 0)
            {
                return Task.FromResult(Failure(writeResult, "set"));
            }

            uint confirmResult = dc
                ? NativeMethods.PowerGetUserConfiguredDCPowerMode(out Guid confirmed)
                : NativeMethods.PowerGetUserConfiguredACPowerMode(out confirmed);
            if (confirmResult != 0)
            {
                return Task.FromResult(Failure(confirmResult, "confirm"));
            }
            if (confirmed != requested)
            {
                return Task.FromResult(new WindowsPowerModeApplyResult(
                    Succeeded: false,
                    Changed: true,
                    FromGuid(confirmed),
                    "Windows did not retain the requested power mode."));
            }

            return Task.FromResult(new WindowsPowerModeApplyResult(
                Succeeded: true,
                Changed: true,
                mode,
                Error: null));
        }
        catch (Exception exception) when (exception is
            EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            return Task.FromResult(new WindowsPowerModeApplyResult(
                Succeeded: false,
                Changed: false,
                ConfiguredMode: null,
                $"Windows power mode APIs are unavailable: {exception.Message}"));
        }
    }

    internal static Guid ToGuid(WindowsPowerMode mode) => mode switch
    {
        WindowsPowerMode.BestPowerEfficiency => BestPowerEfficiencyGuid,
        WindowsPowerMode.Balanced => Guid.Empty,
        WindowsPowerMode.BestPerformance => BestPerformanceGuid,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static WindowsPowerMode? FromGuid(Guid value)
    {
        if (value == BestPowerEfficiencyGuid)
        {
            return WindowsPowerMode.BestPowerEfficiency;
        }
        if (value == Guid.Empty)
        {
            return WindowsPowerMode.Balanced;
        }
        if (value == BestPerformanceGuid)
        {
            return WindowsPowerMode.BestPerformance;
        }
        return null;
    }

    private static WindowsPowerModeApplyResult Failure(uint code, string operation) => new(
        Succeeded: false,
        Changed: false,
        ConfiguredMode: null,
        $"Windows power mode {operation} failed: {new Win32Exception((int)code).Message}");

    private static class NativeMethods
    {
        [DllImport("powrprof.dll", ExactSpelling = true)]
        internal static extern uint PowerGetUserConfiguredACPowerMode(out Guid powerModeGuid);

        [DllImport("powrprof.dll", ExactSpelling = true)]
        internal static extern uint PowerGetUserConfiguredDCPowerMode(out Guid powerModeGuid);

        [DllImport("powrprof.dll", ExactSpelling = true)]
        internal static extern uint PowerSetUserConfiguredACPowerMode(in Guid powerModeGuid);

        [DllImport("powrprof.dll", ExactSpelling = true)]
        internal static extern uint PowerSetUserConfiguredDCPowerMode(in Guid powerModeGuid);
    }
}
