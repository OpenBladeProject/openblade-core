using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class ExperimentalTemporaryFullChargeState
{
    internal int ReadAdmitted;
}

internal sealed partial class ExperimentalBladePort
{
    private readonly ExperimentalTemporaryFullChargeState temporaryFullChargeState =
        sharedTemporaryFullChargeState ?? new();

    // Product 672 inherits rzDevice25 DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24:
    // GET 07/8F [0], SET 07/0F [mask], oneTimeOverride = bit 04.
    // Its shared middleware 651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997
    // changes that field through read-modify-write. Only 00/04 are admitted here,
    // with other shared bits clear; a readable zero mask is not physical validation.
    public bool SupportsTemporaryFullChargeQuery => model.ProductId == 0x02A0;

    internal static DeviceCapabilities WithTemporaryFullChargeReadAdmission(
        DeviceCapabilities capabilities, ExperimentalTemporaryFullChargeState state) =>
        capabilities.SupportsTemporaryFullCharge && Volatile.Read(ref state.ReadAdmitted) == 0
            ? capabilities with { SupportsTemporaryFullCharge = false } : capabilities;

    internal async Task InitializeBlade18TemporaryFullChargeAsync(CancellationToken token)
    {
        if (SupportsTemporaryFullChargeQuery)
        {
            _ = await QueryTemporaryFullChargeAsync(token).ConfigureAwait(false);
        }
    }

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
        ReadOnlyMemory<byte> payload = await ExchangeAsync("TemporaryFullCharge", 0x07, 0x8F,
            [0], false, token, variableResponse: true,
            payloadValidator: static value => value.Length == 1).ConfigureAwait(false);
        if (!TemporaryFullChargeProtocol.TryDecodeQuery(payload.Span, out bool active, out byte otherSettings))
        {
            session.RetainQueryFailure(0x07, 0x8F, FirmwareQueryOutcome.ContentRejected);
            throw new IOException("Temporary full charge returned an invalid shared Power Mode Control mask.");
        }
        Volatile.Write(ref temporaryFullChargeState.ReadAdmitted, 1);
        RecordQuery("TemporaryFullCharge", "Confirmed", $"Active={active};OtherSettings=0x{otherSettings:X2}");
        return payload.Span[0];
    }

    private async Task<ConfirmedBatteryState> ReadTemporaryFullChargeRecoveryAsync(CancellationToken token)
    {
        ConfirmedBatteryState policyState = await ReadBatteryAsync(token).ConfigureAwait(false);
        byte flags = await ReadTemporaryFullChargeFlagsAsync(token).ConfigureAwait(false);
        return new(policyState.ProtectionEnabled, policyState.ChargeLimitPercent,
            (flags & TemporaryFullChargeProtocol.TemporaryFullChargeBit) != 0);
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
            byte flags = await ReadTemporaryFullChargeFlagsAsync(cancellationToken).ConfigureAwait(false);
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
                "Enabling temporary full charge requires a confirmed connected power source.");
        }
    }

    private bool TemporaryFullChargeEnablePowerAdmitted() =>
        capturePowerSource() is PowerSource.PluggedIn or PowerSource.UsbC;

    private bool IsTemporaryFullChargeSet(string operation, byte commandClass, byte commandId, byte[] arguments) =>
        SupportsTemporaryFullChargeQuery && operation == "TemporaryFullCharge"
        && commandClass == 0x07 && commandId == 0x0F
        && arguments.Length == 1 && arguments[0] is 0 or TemporaryFullChargeProtocol.TemporaryFullChargeBit;
}
