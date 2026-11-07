using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    internal static IndependentLightingSettings? SuppressIndependentBrightness(
        IndependentLightingSettings? fields, bool suppressed) =>
        suppressed && fields is { Brightness: not null } ? fields with { Brightness = 0 } : fields;

    public Task<ControlOperationResult> SetIndependentLightingOverrideAsync(
        ManualOverride<IndependentLightingSettings>? value,
        CancellationToken token)
    {
        EnsureInitialized();
        if (value is not null && (!Enum.IsDefined(value.Lifetime) || value.Value is not { IsWellFormed: true }))
        {
            return Task.FromResult(RecordRejectedIntent(new ArgumentOutOfRangeException(nameof(value))));
        }
        if (GetStatus().Capabilities?.SupportsIndependentLightingControls != true)
        {
            return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked,
                "The exact model has no independent lighting contract."));
        }
        return ExecuteScopedMutationAsync(
            DeviceSubsystem.Lighting,
            async cancellationToken =>
            {
                if (value is null)
                {
                    await SetLightingOverrideCoreAsync(null, cancellationToken).ConfigureAwait(false);
                    return;
                }
                LightingSettings intent = ResolveAutomationDesired(GetStatus().ConfirmedState ?? UnknownConfirmedState).Lighting.Value;
                IndependentLightingSettings? prior = intent.IndependentControls;
                IndependentLightingSettings fields = new(
                    value.Value.Brightness ?? prior?.Brightness,
                    value.Value.LogoMode ?? prior?.LogoMode);
                // Preserve the saved effect as unmanaged intent. Neither this request
                // nor its confirmation invents an active keyboard effect.
                var staged = new ManualOverride<LightingSettings>(intent with
                {
                    IndependentControls = fields,
                }, value.Lifetime);
                await SetLightingOverrideCoreAsync(staged, cancellationToken).ConfigureAwait(false);
            },
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyLightingOutcomeAsync(desired.Lighting.Value, cancellationToken),
            token,
            value is null ? "Independent lighting override was cleared." : "Independent lighting intent was stored.",
            afterApply: () =>
            {
                lightingRecoveryPauseMessage = null;
                explicitLightingOwnershipActive = false;
            },
            hardwareDesiredProjection: desired => value is null ? desired : desired with
            {
                Lighting = desired.Lighting with
                {
                    Value = desired.Lighting.Value with
                    {
                        IndependentControls = SuppressIndependentBrightness(value.Value, lightingSuppressions.Count > 0),
                    },
                },
            });
    }
}
