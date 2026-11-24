using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // Product 672 inherits rzDevice25 DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24:
    // GET 07/8F [0], SET 07/0F [mask], oneTimeOverride = bit 04.
    // Its shared middleware 651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997
    // changes that field through read-modify-write. Product 711 (RZ09-0529) pins the
    // same contract: rzDevice25.c8da1ead63e6cf002d32.js
    // (02C55B78B3EABDCC104D725C64C94CADE500FA78D29713ACE5FEF520D243EBB9) GET/SET helpers at
    // bytes 47963/48088, tuples [1,7,143]/[1,7,15] at 52973, decoder at 47786; its
    // 6737.c63928b0e4fd1b7673b4.js (3EF16C0BEB5E8399CDEE51E9995A717E029CDF2125324735440B68A34D2D7B88)
    // read-modify-write helper at 2376567 and override callers at 1986374/1988648.
    // Product 709 (RZ09-0530) pins it independently: rzDevice25.d532642d8de7a9cfcf5d.js
    // (9109826D790FEB8597BBFC115231066CA66E2C480ED9769BB85F3F91188AAD0A) GET/SET helpers at
    // bytes 52840/52965, tuples at 57850, decoder at 52663; its 6737.d34a3f9a638030f35c6a.js
    // (4758FB7E2D8296564FB9CE971F6DCE3D580ED5407EEF07A69E1C362A04FF28C0) read-modify-write
    // helper at 2312703 and override callers at 1941348/1943622.
    // See evidence/source-reviewed-blades/2026-11-19-temporary-full-charge-rz09-0529-0530.json.
    // Only 00/04 are admitted here, with other shared bits clear; a readable zero
    // mask is not physical validation.
    public bool SupportsTemporaryFullChargeQuery => SupportsTemporaryFullCharge(model);

    internal const string TemporaryFullChargeLastSeenActiveMessage =
        "Temporary full charge was last on and can't be read now, so OpenBlade won't change the charge limit. Check the laptop state again before retrying.";

    internal static bool SupportsTemporaryFullCharge(ExperimentalBladeModel model) =>
        model.ProductId is 0x02A0 or 0x02C5 or 0x02C7;

    // Command support is stable. An unknown override reading must not be
    // mistaken for a backend without the query; every SET below reads it afresh.

    public async Task<bool?> QueryTemporaryFullChargeAsync(CancellationToken cancellationToken)
    {
        if (!SupportsTemporaryFullChargeQuery)
        {
            return null;
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte flags = await ReadTemporaryFullChargeFlagsAsync(cancellationToken).ConfigureAwait(false);
            return (flags & TemporaryFullChargeProtocol.TemporaryFullChargeBit) != 0;
        }
        catch (IOException) when (session.Disposition == RazerCommandSessionDisposition.Healthy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordQuery("TemporaryFullCharge", "Unavailable", failure: "TemporaryFullChargeReadbackUnavailable");
            return null;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<byte> ReadTemporaryFullChargeFlagsAsync(CancellationToken token)
    {
        try
        {
            byte flags = await ReadPowerModeControlAsync("TemporaryFullCharge",
                "Temporary full charge returned an invalid shared Power Mode Control mask.", token).ConfigureAwait(false);
            bool active = (flags & TemporaryFullChargeProtocol.TemporaryFullChargeBit) != 0;
            RecordQuery("TemporaryFullCharge", "Confirmed",
                $"Active={active};OtherSettings=0x{flags & ~TemporaryFullChargeProtocol.TemporaryFullChargeBit:X2}");
            policy.RecordTemporaryFullChargeRead(readable: true, active);
            return flags;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            policy.RecordTemporaryFullChargeRead(readable: false);
            throw;
        }
    }

    // The one shared Power Mode Control (07/8F) read. Every caller decodes the
    // same whole-byte mask; an undecodable mask is retained as a rejected query
    // so later reads in this session fail closed instead of repeating it.
    private async Task<byte> ReadPowerModeControlAsync(string operation, string invalidMaskMessage,
        CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync(operation, 0x07, 0x8F,
            [0], false, token, variableResponse: true,
            payloadValidator: static value => value.Length == 1).ConfigureAwait(false);
        if (!TemporaryFullChargeProtocol.TryDecodeQuery(payload.Span, out _))
        {
            session.RetainQueryFailure(0x07, 0x8F, FirmwareQueryOutcome.ContentRejected);
            throw new IOException(invalidMaskMessage);
        }
        return payload.Span[0];
    }

    // 07/8F has not been observed on every admitted model. Where it only refines
    // battery state, a failed or undecodable read leaves the flag unknown while
    // the session is still usable. A session that ended cannot continue.
    private async Task<bool?> TryReadTemporaryFullChargeActiveAsync(CancellationToken token)
    {
        try
        {
            return (await ReadTemporaryFullChargeFlagsAsync(token).ConfigureAwait(false)
                & TemporaryFullChargeProtocol.TemporaryFullChargeBit) != 0;
        }
        catch (IOException) when (!token.IsCancellationRequested
            && session.Disposition == RazerCommandSessionDisposition.Healthy)
        {
            RecordQuery("TemporaryFullCharge", "Unavailable", failure: "TemporaryFullChargeReadbackUnavailable");
            return null;
        }
    }

    private async Task<ConfirmedBatteryState> ReadTemporaryFullChargeRecoveryAsync(CancellationToken token)
    {
        ConfirmedBatteryState policyState = await ReadBatteryAsync(token).ConfigureAwait(false);
        byte flags = await ReadTemporaryFullChargeFlagsAsync(token).ConfigureAwait(false);
        return WithTemporaryFullCharge(policyState,
            (flags & TemporaryFullChargeProtocol.TemporaryFullChargeBit) != 0);
    }

    // Synapse clears bit 04 whenever it turns the limiter off, so protection off
    // with the override set has no confirmed representation. Publish the override
    // as unknown and record why, rather than failing every subsystem in the read.
    // The decoded bit still counts as last seen active for later limiter writes.
    internal ConfirmedBatteryState WithTemporaryFullCharge(ConfirmedBatteryState policyState, bool? active)
    {
        if (!policyState.ProtectionEnabled && active == true)
        {
            RecordQuery("TemporaryFullCharge", "InconsistentState", "Active=True;ProtectionEnabled=False",
                "TemporaryFullChargeWithProtectionOff");
            active = null;
        }
        return new(policyState.ProtectionEnabled, policyState.ChargeLimitPercent, active);
    }

    public async Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!SupportsTemporaryFullChargeQuery)
        {
            throw new CapabilityUnavailableException("Temporary full charge has no admitted implementation for this exact model.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            RequireTemporaryFullChargeWriteAdmission(enabled);
            var baseline = await ReadBatteryPolicyAsync(cancellationToken).ConfigureAwait(false);
            byte flags;
            try
            {
                flags = await ReadTemporaryFullChargeFlagsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "OpenBlade can't read temporary full charge right now, so it won't turn it on or off. Check the laptop state again before retrying.",
                    error);
            }
            bool active = (flags & TemporaryFullChargeProtocol.TemporaryFullChargeBit) != 0;
            if (active == enabled)
            {
                return;
            }
            if (enabled && (!baseline.State.ProtectionEnabled || baseline.State.ChargeLimitPercent is not int limit
                || !GetExperimentalChargeLimits(model).Contains(limit)))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Temporary full charge requires a confirmed active charge limit.");
            }
            byte otherSettings = (byte)(flags & ~TemporaryFullChargeProtocol.TemporaryFullChargeBit);
            if (otherSettings != 0)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    TemporaryFullChargeProtocol.OtherSettingsBlockMessage(otherSettings));
            }
            policy.Record("TemporaryFullCharge", "Baseline", "Confirmed", requested: enabled.ToString(),
                confirmed: $"Active={active};ChargePolicyRaw=0x{baseline.Raw:X2}");
            RequireTemporaryFullChargeWriteAdmission(enabled);
            byte requestedFlags = TemporaryFullChargeProtocol.Encode(enabled);
            // Each admitted model's own pinned 07/0F helper parses data only when the
            // ACK returns some; that is its reviewed status-only SET contract.
            if (!UsesSourceBackedSetAckContract(model))
            {
                throw new CapabilityUnavailableException("Temporary full charge has no reviewed SET acknowledgment contract for this exact model.");
            }
            await ExchangeAsync("TemporaryFullCharge", 0x07, 0x0F, [requestedFlags], true,
                cancellationToken, variableResponse: true,
                payloadValidator: static payload => payload.Length <= 1,
                requireEcho: false).ConfigureAwait(false);
            submitted = true;
            byte confirmedFlags = await ReadTemporaryFullChargeFlagsAsync(cancellationToken).ConfigureAwait(false);
            var confirmedPolicy = await ReadBatteryPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (confirmedFlags != requestedFlags || confirmedPolicy.Raw != baseline.Raw)
            {
                throw new IOException("Independent full-charge readback did not confirm the flag and unchanged charge policy.");
            }
            policy.RecordTemporaryFullChargeWrite(enabled);
            policy.Record("TemporaryFullCharge", "Readback", "Confirmed", true,
                enabled.ToString(), enabled.ToString());
        }
        catch (Exception error)
        {
            throw MapWriteFailure("TemporaryFullCharge", submitted, error);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void RequireTemporaryFullChargeWriteAdmission(bool enabled)
    {
        RequireWriteAdmission();
        if (enabled && !TemporaryFullChargeEnablePowerAdmitted())
        {
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "Enabling temporary full charge requires external power. Connect the charger and retry.");
        }
    }

    // The production callback reports Windows AC presence as ExternalUnknown,
    // the same preliminary presence the other experimental writes accept. This
    // battery-policy bit has no adapter-class contract, so Battery alone refuses.
    private bool TemporaryFullChargeEnablePowerAdmitted() =>
        ExternalPowerReported(capturePowerSource());

    private bool IsTemporaryFullChargeSet(string operation, byte commandClass, byte commandId, byte[] arguments) =>
        SupportsTemporaryFullChargeQuery && operation == "TemporaryFullCharge"
        && commandClass == 0x07 && commandId == 0x0F
        && arguments.Length == 1 && arguments[0] is 0 or TemporaryFullChargeProtocol.TemporaryFullChargeBit;
}
