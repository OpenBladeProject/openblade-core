using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // Products 695 (RZ09-0510) and 696 (RZ09-0509) expose a local dimming toggle
    // only for mini-LED panels. Pinned sources, reviewed read-only:
    // 695 rzDevice25.60bfcef58690c8bb715d.js
    // (SHA-256 2D69449422DAC50C23A0EC81A5421CAB178BD04610725273FA67497574195491)
    // decodes getSKUHardwareConfiguration 0D/8F [1,13,143] at char 64925;
    // 696 rzDevice25.6339454cb5b2fe587300.js
    // (SHA-256 8780AA380D11665A2D551174D162C7278404465772219D0530A29B09E297BC81)
    // defines the same getter at char 66696. UI main 695 (char 6936465) and 696
    // (char 7201418) show the toggle only when miniLedResolution is
    // Mini_LED_Supported (0x02). Power Mode Control is the shared 07/8F/07/0F mask.
    // Only the developer-guide Clear is admitted; turning local dimming on is not.
    internal static bool SupportsLocalDimmingClear(ExperimentalBladeModel model) =>
        model.ProductId is 0x02B7 or 0x02B8 && model.ThermalPolicy is not null;

    internal static DeviceCapabilities WithLocalDimmingAdmission(
        DeviceCapabilities capabilities, ExperimentalBladeModel model, bool writesAllowed, bool? miniLedPanel) =>
        SupportsLocalDimmingClear(model) && miniLedPanel == true
            ? capabilities with
            {
                LocalDimming = new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: writesAllowed),
            }
            : capabilities;

    public bool SupportsLocalDimmingQuery => SupportsLocalDimmingClear(model) && policy.MiniLedPanel == true;

    // Optional panel metadata read while a port opens. A failed read leaves the
    // panel unknown, so the control is not offered; it never guesses mini-LED.
    internal async Task InitializeLocalDimmingPanelAsync(CancellationToken cancellationToken)
    {
        if (!SupportsLocalDimmingClear(model))
        {
            return;
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!exactHostStillPresent())
            {
                policy.ConfirmMiniLedPanel(null);
                throw new CapabilityUnavailableException("The exact laptop is no longer verified.");
            }
            policy.ConfirmMiniLedPanel(await ReadMiniLedPanelAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (IOException) when (session.Disposition == RazerCommandSessionDisposition.Healthy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            policy.ConfirmMiniLedPanel(null);
            RecordQuery("LocalDimmingPanel", "Unavailable", failure: "MiniLedPanelReadUnavailable");
        }
        catch (IOException)
        {
            policy.ConfirmMiniLedPanel(null);
            throw;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<bool?> QueryLocalDimmingAsync(CancellationToken cancellationToken)
    {
        if (!SupportsLocalDimmingQuery)
        {
            return null;
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte flags = await ReadLocalDimmingFlagsAsync(cancellationToken).ConfigureAwait(false);
            return (flags & TemporaryFullChargeProtocol.LocalDimmingBit) != 0;
        }
        catch (IOException) when (session.Disposition == RazerCommandSessionDisposition.Healthy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordQuery("LocalDimming", "Unavailable", failure: "LocalDimmingReadbackUnavailable");
            return null;
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>
    /// Turns local dimming off once: fresh mini-LED panel read, fresh Power Mode
    /// Control read, whole-byte write of <c>current &amp; 0x04</c>, then an exact
    /// readback. Any possible submission without a matching readback pauses
    /// experimental writes; the write is never replayed.
    /// </summary>
    public async Task ClearLocalDimmingAsync(CancellationToken cancellationToken)
    {
        if (!SupportsLocalDimmingClear(model))
        {
            throw new CapabilityUnavailableException("Local dimming has no admitted Clear for this exact model.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            RequireWriteAdmission();
            bool miniLedPanel = await ReadMiniLedPanelAsync(cancellationToken).ConfigureAwait(false);
            policy.ConfirmMiniLedPanel(miniLedPanel);
            if (!miniLedPanel)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "This laptop doesn't report a mini-LED display, so OpenBlade won't change local dimming.");
            }
            byte flags = await ReadLocalDimmingFlagsAsync(cancellationToken).ConfigureAwait(false);
            LocalDimmingClearPlan plan = LocalDimmingProtocol.PlanClear(flags);
            if (plan.Kind == LocalDimmingClearKind.AlreadyClear)
            {
                policy.Record("LocalDimming", "Baseline", "AlreadyClear", confirmed: $"PowerModeControl=0x{flags:X2}");
                return;
            }
            if (plan.Kind == LocalDimmingClearKind.Blocked)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied, plan.Refusal!);
            }
            policy.Record("LocalDimming", "Baseline", "Confirmed",
                requested: $"PowerModeControl=0x{plan.Value:X2}", confirmed: $"PowerModeControl=0x{flags:X2}");
            RequireWriteAdmission();
            await ExchangeAsync("LocalDimming", 0x07, 0x0F, [plan.Value], true,
                cancellationToken, variableResponse: true,
                payloadValidator: static payload => payload.Length <= 1,
                requireEcho: false).ConfigureAwait(false);
            submitted = true;
            byte confirmed = await ReadLocalDimmingFlagsAsync(cancellationToken).ConfigureAwait(false);
            if (confirmed != plan.Value)
            {
                throw new IOException("Independent readback did not confirm that only local dimming was turned off.");
            }
            policy.Record("LocalDimming", "Readback", "Confirmed", true,
                $"PowerModeControl=0x{plan.Value:X2}", $"PowerModeControl=0x{confirmed:X2}");
        }
        catch (Exception error)
        {
            throw MapWriteFailure("LocalDimming", submitted, error);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<bool> ReadMiniLedPanelAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("LocalDimmingPanel",
            LocalDimmingProtocol.SkuHardwareConfigurationCommandClass,
            LocalDimmingProtocol.SkuHardwareConfigurationCommandId,
            [0], false, cancellationToken,
            payloadValidator: static value => value.Length == 1).ConfigureAwait(false);
        if (!LocalDimmingProtocol.TryDecodeMiniLedPanel(payload.Span, out bool miniLedPanel))
        {
            RecordQuery("LocalDimmingPanel", "UnsupportedState",
                DeviceStateContentRejection.FormatPayload(payload.Span), "OutsideSkuHardwareConfigurationContract");
            throw new IOException("The SKU hardware configuration reply is outside its reviewed contract.");
        }
        RecordQuery("LocalDimmingPanel", "Confirmed",
            $"SkuHardwareConfiguration=0x{payload.Span[0]:X2};MiniLedPanel={miniLedPanel}");
        return miniLedPanel;
    }

    private async Task<byte> ReadLocalDimmingFlagsAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("LocalDimming", 0x07, 0x8F,
            [0], false, cancellationToken, variableResponse: true,
            payloadValidator: static value => value.Length == 1).ConfigureAwait(false);
        if (!TemporaryFullChargeProtocol.TryDecodeQuery(payload.Span, out _))
        {
            RecordQuery("LocalDimming", "UnsupportedState",
                DeviceStateContentRejection.FormatPayload(payload.Span), "OutsideSharedPowerModeControlContract");
            throw new IOException("Power Mode Control is outside the shared bitmask contract.");
        }
        RecordQuery("LocalDimming", "Confirmed",
            $"PowerModeControl=0x{payload.Span[0]:X2};"
                + $"LocalDimmingBit={(payload.Span[0] & TemporaryFullChargeProtocol.LocalDimmingBit) != 0}");
        return payload.Span[0];
    }

    // Clear writes 00 or 04 only after the fresh guard refused CPU boost (0x01)
    // and Maximum Fans (0x02), so the whole-byte write changes no thermal bit.
    // It therefore keeps the general write admission without the thermal
    // external-power gate, like the 02A0 full-charge bit.
    private bool IsLocalDimmingClearSet(string operation, byte commandClass, byte commandId, byte[] arguments) =>
        SupportsLocalDimmingClear(model) && operation == "LocalDimming"
        && commandClass == 0x07 && commandId == 0x0F
        && arguments is [0 or TemporaryFullChargeProtocol.TemporaryFullChargeBit];
}
