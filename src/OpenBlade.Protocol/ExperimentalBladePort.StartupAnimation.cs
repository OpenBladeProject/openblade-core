using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class ExperimentalStartupAnimationState
{
    internal int ReadAdmitted;
    internal int ReconciliationRequired;
}

internal sealed partial class ExperimentalBladePort
{
    private readonly ExperimentalStartupAnimationState startupAnimationState =
        sharedStartupAnimationState ?? new();

    // Exact Product 672 inherits rzDevice25 SHA256
    // DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24.
    // GET 0F/98: [0]; SET 0F/18: [profile=0, disable]. The shared middleware
    // calls this exact class with enabled ? 0 : 1. Its missing UI version flag
    // is not a command prohibition. Admit mutation only after valid readback.
    public bool SupportsStartupAnimationQuery => model.ProductId == 0x02A0;

    internal static DeviceCapabilities WithStartupReadAdmission(
        DeviceCapabilities capabilities, ExperimentalStartupAnimationState state)
    {
        if (!capabilities.OneShotLaptopControls.Contains(OneShotLaptopControl.StartupAnimation)
            || Volatile.Read(ref state.ReadAdmitted) != 0)
        {
            return capabilities;
        }
        return capabilities with
        {
            OneShotLaptopControls = capabilities.OneShotLaptopControls
                .Where(control => control != OneShotLaptopControl.StartupAnimation).ToHashSet(),
        };
    }

    public async Task<bool?> QueryStartupAnimationAsync(CancellationToken cancellationToken)
    {
        if (!SupportsStartupAnimationQuery)
        {
            return null;
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadStartupAnimationCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (session.Disposition == RazerCommandSessionDisposition.Healthy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordQuery("StartupAnimation", "Unavailable", failure: "StartupReadbackUnavailable");
            return null;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<bool> ReadStartupAnimationCoreAsync(CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("StartupAnimation", 0x0F, 0x98,
            [0], false, token, variableResponse: true,
            payloadValidator: static value => value.Length is 1 or 2).ConfigureAwait(false);
        ReadOnlySpan<byte> bytes = payload.Span;
        if (bytes.Length == 2 && bytes[0] != 0 || bytes[^1] > 1)
        {
            session.RetainQueryFailure(0x0F, 0x98, FirmwareQueryOutcome.ContentRejected);
            throw new IOException("Startup animation returned an unknown profile or state.");
        }
        bool enabled = bytes[^1] == 0;
        Volatile.Write(ref startupAnimationState.ReadAdmitted, 1);
        Volatile.Write(ref startupAnimationState.ReconciliationRequired, 0);
        RecordQuery("StartupAnimation", "Confirmed", $"Enabled={enabled}");
        return enabled;
    }

    public async Task ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!SupportsStartupAnimationQuery || !value.IsWellFormed()
            || value.Control != OneShotLaptopControl.StartupAnimation)
        {
            throw new CapabilityUnavailableException("This one-shot control is not admitted for the exact model.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            RequireWriteAdmission();
            bool baseline = await ReadStartupAnimationCoreAsync(cancellationToken).ConfigureAwait(false);
            bool requested = value.Enabled!.Value;
            if (baseline == requested)
            {
                return;
            }
            policy.Record("StartupAnimation", "Baseline", "Confirmed",
                requested: requested.ToString(), confirmed: baseline.ToString());
            RequireWriteAdmission();
            // Vendor ACK does not promise an echo; the independent GET below
            // establishes the applied state. No retry after possible submission.
            Volatile.Write(ref startupAnimationState.ReconciliationRequired, 1);
            await ExchangeAsync("StartupAnimation", 0x0F, 0x18,
                [0, requested ? (byte)0 : (byte)1], true, cancellationToken,
                variableResponse: true, payloadValidator: static payload => payload.Length <= 2,
                requireEcho: false).ConfigureAwait(false);
            submitted = true;
            if (await ReadStartupAnimationCoreAsync(cancellationToken).ConfigureAwait(false) != requested)
            {
                throw new IOException("Independent startup-animation readback did not match the requested state.");
            }
            policy.Record("StartupAnimation", "Readback", "Confirmed", true,
                requested.ToString(), requested.ToString());
        }
        catch (Exception error)
        {
            BladeWriteException failure = MapWriteFailure("StartupAnimation", submitted, error);
            if (failure.SubmissionState == WriteSubmissionState.KnownNotApplied)
            {
                Volatile.Write(ref startupAnimationState.ReconciliationRequired, 0);
            }
            throw failure;
        }
        finally
        {
            operationGate.Release();
        }
    }
}
