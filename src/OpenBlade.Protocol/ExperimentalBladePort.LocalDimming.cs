using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // Products 695 (RZ09-0510) and 696 (RZ09-0509) expose a local dimming toggle
    // only for mini-LED panels. Pinned sources, reviewed read-only, with UTF-8
    // byte offsets:
    // 695 rzDevice25.60bfcef58690c8bb715d.js
    // (SHA-256 2D69449422DAC50C23A0EC81A5421CAB178BD04610725273FA67497574195491)
    // decodes getSKUHardwareConfiguration 0D/8F [1,13,143] at byte 64925;
    // 696 rzDevice25.6339454cb5b2fe587300.js
    // (SHA-256 8780AA380D11665A2D551174D162C7278404465772219D0530A29B09E297BC81)
    // defines the same getter at byte 66696. The mask enum
    // {DDS:1,Mini_LED_Resolution:2,Illegal_Battery_Support:4} is module 83379 of
    // 695 6737.7ec91e479ef65c194b6c.js
    // (SHA-256 22D359EB7EDA08A9D2F4766EB6898A875A5F009EDB666BA4CD5A0425491AD1DA) at byte
    // 900284 and of 696 6737.2463f7361e4d145671a2.js
    // (SHA-256 4EF6AFB6402DD50CD6DE07CA96E191F475797903507C82CAE194F0AF63E32ECC) at byte
    // 888791. UI main 695 (byte 6936465) and 696 (byte 7201418) show the toggle only
    // when miniLedResolution is Mini_LED_Supported (0x02). Power Mode Control is the
    // shared 07/8F/07/0F mask. Only the developer-guide Clear is admitted; turning
    // local dimming on is not.
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

    // The optional state read is skipped after it failed, until a Clear or
    // recovery reads Power Mode Control again.
    public bool SupportsLocalDimmingQuery => SupportsLocalDimmingClear(model)
        && policy.MiniLedPanel == true
        && !policy.LocalDimmingReadUnavailable;

    // Optional panel metadata, read once per exact device (factory) rather than
    // on every port open. A successful reading is kept; a failed read is
    // remembered and leaves an unread panel unknown, so the control is not
    // offered, and it never replaces an earlier confirmed reading. It never
    // guesses mini-LED. Every Clear still reads the panel afresh.
    internal async Task InitializeLocalDimmingPanelAsync(CancellationToken cancellationToken)
    {
        if (!SupportsLocalDimmingClear(model) || policy.MiniLedPanelResolved)
        {
            return;
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!exactHostStillPresent())
            {
                throw new CapabilityUnavailableException("The exact laptop is no longer verified.");
            }
            policy.ConfirmMiniLedPanel(await ReadMiniLedPanelAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (IOException) when (session.Disposition == RazerCommandSessionDisposition.Healthy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            policy.RecordMiniLedPanelReadUnavailable();
            RecordQuery("LocalDimmingPanel", "Unavailable", failure: "MiniLedPanelReadUnavailable");
        }
        catch (IOException)
        {
            policy.RecordMiniLedPanelReadUnavailable();
            RecordQuery("LocalDimmingPanel", "Unavailable", failure: "MiniLedPanelReadUnavailable");
            throw;
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>
    /// Reads the local dimming bit. A failed read is remembered so later state
    /// reads skip it; the caller isolates the failure and leaves the field unknown.
    /// </summary>
    public async Task<bool?> QueryLocalDimmingAsync(CancellationToken cancellationToken)
    {
        if (!SupportsLocalDimmingQuery)
        {
            return null;
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadLocalDimmingActiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            policy.RecordLocalDimmingRead(readable: false);
            RecordQuery("LocalDimming", "Unavailable", failure: "LocalDimmingReadbackUnavailable");
            throw;
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
    /// experimental writes with a local dimming recovery obligation; the write
    /// is never replayed.
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
            // The reads above can outlive a pause or host change; admit again at submission.
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

    // Recovery after a possibly submitted Clear reads the real bit. A failed
    // read throws, so the pause stays until Power Mode Control is readable.
    private async Task<bool> ReadLocalDimmingActiveAsync(CancellationToken cancellationToken) =>
        (await ReadLocalDimmingFlagsAsync(cancellationToken).ConfigureAwait(false)
            & TemporaryFullChargeProtocol.LocalDimmingBit) != 0;

    private async Task<bool> ReadMiniLedPanelAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("LocalDimmingPanel",
            LocalDimmingProtocol.SkuHardwareConfigurationCommandClass,
            LocalDimmingProtocol.SkuHardwareConfigurationCommandId,
            [0], false, cancellationToken,
            payloadValidator: static value => value.Length == Rz090581CapabilityDiagnosticsProtocol.SingleBytePayloadLength)
            .ConfigureAwait(false);
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
        byte flags = await ReadPowerModeControlAsync("LocalDimming",
            "Power Mode Control is outside the shared bitmask contract.", cancellationToken).ConfigureAwait(false);
        policy.RecordLocalDimmingRead(readable: true);
        RecordQuery("LocalDimming", "Confirmed",
            $"PowerModeControl=0x{flags:X2};"
                + $"LocalDimmingBit={(flags & TemporaryFullChargeProtocol.LocalDimmingBit) != 0}");
        return flags;
    }

    // Clear writes 00 or 04 only after the fresh guard refused the firmware CPU
    // boost flag (0x01) and Maximum Fans (0x02), so the whole-byte write changes
    // no thermal bit. It therefore keeps the general write admission without the
    // thermal external-power gate, like the 02A0 full-charge bit.
    private bool IsLocalDimmingClearSet(string operation, byte commandClass, byte commandId, byte[] arguments) =>
        SupportsLocalDimmingClear(model) && operation == "LocalDimming"
        && commandClass == 0x07 && commandId == 0x0F
        && arguments is [0 or TemporaryFullChargeProtocol.TemporaryFullChargeBit];
}
