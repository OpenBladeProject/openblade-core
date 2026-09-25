using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public sealed record ControllerPresenceObservation(
    IReadOnlyList<string> PotentialControllers,
    string? DetectionWarning)
{
    // Null preserves the conservative name-based policy for older or synthetic monitors.
    // The Windows monitor supplies provenance-aware HyperBoost blockers.
    public IReadOnlyList<string>? HyperBoostBlockingControllers { get; init; }
}

public interface IControllerPresenceMonitor
{
    ControllerPresenceObservation Observe();
}

internal enum ControllerExclusivityPolicy
{
    Strict,
    Advisory,
}

public sealed class WindowsControllerPresenceMonitor : IControllerPresenceMonitor
{
    private readonly IRunningProcessSource processSource;
    private readonly IWindowsServiceSource serviceSource;
    private readonly Func<IReadOnlyList<RunningControllerProcess>>? processSnapshots;
    private readonly Func<IReadOnlyList<LampArrayEndpoint>>? lampArrayEndpoints;
    private readonly SynapseBladeBlockerVerifier blocker = new();

    public WindowsControllerPresenceMonitor(
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource)
        : this(processSource, serviceSource,
            processSource is RunningProcessSource live ? live.GetControllerProcessSnapshots : null,
            processSource is RunningProcessSource ? new WindowsLampArrayInterfaceSource().Observe : null)
    {
    }

    internal WindowsControllerPresenceMonitor(
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        Func<IReadOnlyList<RunningControllerProcess>>? processSnapshots,
        Func<IReadOnlyList<LampArrayEndpoint>>? lampArrayEndpoints = null)
    {
        this.processSource = processSource;
        this.serviceSource = serviceSource;
        this.processSnapshots = processSnapshots;
        this.lampArrayEndpoints = lampArrayEndpoints;
    }

    private static readonly HashSet<string> KnownControllerProcesses = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "RazerAppEngine",
        "razerwdl",
    };

    public ControllerPresenceObservation Observe()
    {
        List<string> potentialControllers = [];
        List<string> hyperBoostBlockers = [];
        bool processFailed = false;
        bool serviceFailed = false;
        bool patchedSynapse = false;

        try
        {
            IReadOnlyList<RunningControllerProcess>? snapshots = processSnapshots?.Invoke();
            IReadOnlyList<string> names = snapshots is null
                ? processSource.GetProcessNames()
                : snapshots.Select(process => process.Name).ToArray();
            RunningControllerProcess[] engines = snapshots?
                .Where(process => process.Name.Equals("RazerAppEngine", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? [];
            IReadOnlySet<int>?[] exclusions = engines.Select(blocker.GetAppliedBladeProductIds).ToArray();
            patchedSynapse = engines.Length > 0 && exclusions.All(ids => ids is not null);
            RunningControllerProcess[] helpers = snapshots?
                .Where(process => process.Name.Equals("razerwdl", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? [];
            bool peripheralLightingOnly = patchedSynapse && helpers.Length > 0
                && helpers.All(helper => IsVerifiedEngineChild(helper, engines))
                && HasNoBladeLampArray(exclusions);
            string[] detectedProcesses = names
                .Where(process =>
                    KnownControllerProcesses.Contains(process)
                    || RazerControllerServiceAliases.Contains(process))
                .Where(process => !patchedSynapse
                    || (!process.Equals("RazerAppEngine", StringComparison.OrdinalIgnoreCase)
                        && !(process.Equals("razerwdl", StringComparison.OrdinalIgnoreCase)
                            && peripheralLightingOnly)))
                .ToArray();
            potentialControllers.AddRange(detectedProcesses);
            foreach (string process in detectedProcesses)
            {
                bool verifiedLightingHelper = process.Equals("razerwdl", StringComparison.OrdinalIgnoreCase)
                    && snapshots is not null
                    && helpers.Length > 0
                    && helpers.All(helper => helper.IsLampArrayHelper);
                // Service aliases seen as process names have no service provenance.
                bool unverifiedUpdaterProcess = process.Equals("RGS", StringComparison.OrdinalIgnoreCase)
                    || process.Equals("Razer Gaming Services Updater", StringComparison.OrdinalIgnoreCase);
                if (unverifiedUpdaterProcess
                    || process.Equals("razerwdl", StringComparison.OrdinalIgnoreCase)
                        && !verifiedLightingHelper
                    || RazerThermalControllerPolicy.IsBlocking(process)
                        && !(patchedSynapse && IsSynapseServiceAlias(process)))
                {
                    hyperBoostBlockers.Add(process);
                }
            }
        }
        catch (Exception)
        {
            processFailed = true;
        }

        try
        {
            WindowsServiceSnapshot[] detectedServices = serviceSource
                .GetServices()
                .Where(service => IsActive(service.State))
                .Where(service => RazerControllerServiceAliases.MatchesService(
                    service.ServiceName,
                    service.DisplayName))
                .ToArray();
            foreach (WindowsServiceSnapshot service in detectedServices)
            {
                string name = RazerControllerServiceAliases.Contains(service.DisplayName)
                    ? service.DisplayName
                    : service.ServiceName;
                potentialControllers.Add(name);
                bool verifiedUpdaterService = service.ServiceName.Equals("RGS", StringComparison.OrdinalIgnoreCase);
                bool unverifiedUpdaterAlias = name.Equals("RGS", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Razer Gaming Services Updater", StringComparison.OrdinalIgnoreCase);
                bool synapseService = IsSynapseServiceAlias(service.ServiceName)
                    || IsSynapseServiceAlias(service.DisplayName);
                if (!verifiedUpdaterService
                    && (unverifiedUpdaterAlias
                        || synapseService && !patchedSynapse
                        || !synapseService && RazerThermalControllerPolicy.IsBlocking(name)))
                {
                    hyperBoostBlockers.Add(name);
                }
            }
        }
        catch (Exception)
        {
            serviceFailed = true;
        }

        string[] confirmed = potentialControllers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(conflict => conflict, StringComparer.Ordinal)
            .ToArray();
        string? detectionWarning = (processFailed, serviceFailed) switch
        {
            (true, true) => "Process and Windows service enumeration failed.",
            (true, false) => "Process enumeration failed.",
            (false, true) => "Windows service enumeration failed.",
            _ => null,
        };

        return new ControllerPresenceObservation(confirmed, detectionWarning)
        {
            HyperBoostBlockingControllers = hyperBoostBlockers
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(controller => controller, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static bool IsSynapseServiceAlias(string name) =>
        name.Equals("RazerSynapseService", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Razer Synapse Service", StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(WindowsServiceState state) =>
        state is WindowsServiceState.Running
            or WindowsServiceState.StartPending
            or WindowsServiceState.ContinuePending;

    private bool HasNoBladeLampArray(IEnumerable<IReadOnlySet<int>?> exclusions)
    {
        try
        {
            // Refresh on every observation: a cached absence must not survive hotplug.
            // Unavailable enumeration retains razerwdl as a controller.
            if (lampArrayEndpoints is null)
            {
                return false;
            }

            HashSet<int> bladeIds = exclusions.SelectMany(ids => ids!).ToHashSet();
            return lampArrayEndpoints().All(endpoint => endpoint.VendorId is > 0
                && endpoint.ProductId is > 0
                && (endpoint.VendorId != 0x1532 || !bladeIds.Contains(endpoint.ProductId.Value)));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsVerifiedEngineChild(
        RunningControllerProcess helper,
        IReadOnlyList<RunningControllerProcess> engines) =>
        helper.IsLampArrayHelper
        && helper.ProcessId is > 0
        && helper.ParentProcessId is > 0
        && helper.SessionId is >= 0
        && helper.StartedAtUtc is { } helperStarted
        && helperStarted <= DateTimeOffset.UtcNow
        && engines.Count(engine => engine.ProcessId == helper.ParentProcessId
            && engine.ProcessId != helper.ProcessId
            && engine.SessionId == helper.SessionId
            && engine.StartedAtUtc is { } engineStarted
            && engineStarted < helperStarted) == 1;
}
