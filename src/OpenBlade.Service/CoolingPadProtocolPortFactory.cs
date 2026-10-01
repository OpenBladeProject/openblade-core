using OpenBlade.Contracts;
using OpenBlade.Protocol;
using ProtocolPadState = OpenBlade.Protocol.CoolingPadRawControlState;
using ProtocolPadWrite = OpenBlade.Protocol.CoolingPadWriteResult;

namespace OpenBlade.Service;

/// <summary>The ordinary constructor cannot open a native pad session.</summary>
public sealed class CoolingPadProtocolPortFactory : ICoolingPadControlPortFactory, ICoolingPadProductionPortFactory
{
    private readonly Func<CancellationToken, Task<CoolingPadSessionOpenResult>> open;

    public CoolingPadProtocolPortFactory() : this(new CoolingPadSessionFactory().OpenAsync) { }

    internal CoolingPadProtocolPortFactory(Func<CancellationToken, Task<CoolingPadSessionOpenResult>> open) =>
        this.open = open ?? throw new ArgumentNullException(nameof(open));

    // Developer composition is explicit and is not reachable from IPC or saved settings.
    internal static CoolingPadProtocolPortFactory CreateForDeveloperValidation() =>
        new(CoolingPadSessionFactory.CreateDeveloperWindows().OpenAsync);

    internal static CoolingPadProtocolPortFactory CreateProduction(CoolingPadControllerLease ownership) =>
        new(CoolingPadSessionFactory.CreateProductionWindows(ownership).OpenAsync) { Production = true, SupportedHost = CoolingPadSessionFactory.IsExactProductionHost() };
    internal bool Production { get; private init; }
    internal bool SupportedHost { get; private init; }
    bool ICoolingPadProductionPortFactory.Production => Production;
    bool ICoolingPadProductionPortFactory.SupportedHost => SupportedHost;

    public async Task<CoolingPadPortOpenResult> OpenAsync(CancellationToken token)
    {
        var result = await open(token).ConfigureAwait(false);
        return new(result.Session is null ? null : new Port(result.Session), Map(result.Fault));
    }

    private static CoolingPadPortFault? Map(CoolingPadSessionFault? fault) => fault switch
    {
        null => null,
        CoolingPadSessionFault.AdmissionClosed => CoolingPadPortFault.AdmissionClosed,
        CoolingPadSessionFault.AdmissionRevoked => CoolingPadPortFault.AdmissionRevoked,
        CoolingPadSessionFault.Disconnected => CoolingPadPortFault.Disconnected,
        CoolingPadSessionFault.ControllerConflict => CoolingPadPortFault.ControllerConflict,
        CoolingPadSessionFault.SubmissionUnknown => CoolingPadPortFault.SubmissionUnknown,
        _ => CoolingPadPortFault.Rejected,
    };
    private static CoolingPadWriteResult Map(ProtocolPadWrite result) =>
        new(result.Attempted, result.Acknowledged, Map(result.Fault));

    private sealed class Port(ICoolingPadDemandSession session) : ICoolingPadControlPort, ICoolingPadDeveloperRestorationPort
    {
        public async Task<CoolingPadQueryResult> QueryControlStateAsync(CancellationToken token)
        {
            var result = await session.QueryRawControlStateAsync(token).ConfigureAwait(false);
            return new(result.State switch
            {
                ProtocolPadState.Raw0000 => CoolingPadRawControlState.Raw0000,
                ProtocolPadState.Raw0300 => CoolingPadRawControlState.Raw0300,
                _ => CoolingPadRawControlState.Other,
            }, Map(result.Fault));
        }
        public async Task<CoolingPadWriteResult> AcquireAsync(Func<bool> submissionAllowed, CancellationToken token) =>
            Map(await session.AcquireAsync(submissionAllowed, token).ConfigureAwait(false));
        public async Task<CoolingPadEntryResult> EnterDemandAsync(CoolingPadConfiguredRpmDemand demand,
            Func<bool> submissionAllowed, CancellationToken token)
        {
            var result = await session.EnterDemandAsync(demand, submissionAllowed, token).ConfigureAwait(false);
            return new(Map(result.Entry), Map(result.Demand));
        }
        public async Task<CoolingPadWriteResult> SetDemandAsync(CoolingPadConfiguredRpmDemand demand,
            Func<bool> submissionAllowed, CancellationToken token) =>
            Map(await session.UpdateDemandAsync(demand, submissionAllowed, token).ConfigureAwait(false));
        public ValueTask DisposeAsync() => session.DisposeAsync();
        public async Task<CoolingPadWriteResult> ReassertMediumAsync(Func<bool> allowed, CancellationToken token) =>
            Map(await session.ReassertMediumAsync(allowed, token).ConfigureAwait(false));
        public async Task<CoolingPadWriteResult> ReassertQuietAsync(Func<bool> allowed, CancellationToken token) =>
            Map(await session.ReassertQuietAsync(allowed, token).ConfigureAwait(false));
        public async Task<CoolingPadWriteResult> RestoreRawAsync(Func<bool> allowed, CancellationToken token) =>
            Map(await session.RestoreRawControlStateAsync(allowed, token).ConfigureAwait(false));
    }
}
