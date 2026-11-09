using System.Globalization;

namespace OpenBlade.DeploymentClient;

internal sealed record InstalledProcessObservation(
    int ProcessId, int ParentProcessId, long CreationTime,
    string ExecutablePath, int SessionId, string? OwnerSid, bool IsNormalIntegrity,
    IReadOnlyList<string> Arguments);

internal sealed record InstalledProcessSnapshot(
    bool IsComplete, IReadOnlyList<InstalledProcessObservation> Processes);

internal static class InstalledProcessFamilyPolicy
{
    internal static InstalledProcessInventory Evaluate(
        InstalledPayloadPaths paths, int sessionId, string? ownerSid, InstalledProcessSnapshot snapshot)
    {
        bool valid = snapshot.IsComplete && !string.IsNullOrEmpty(ownerSid);
        List<(InstalledProcessObservation Process, Role Role, int Parent)> agents = [];
        int trays = 0;
        HashSet<int> ids = [];
        foreach (InstalledProcessObservation process in snapshot.Processes)
        {
            bool agent = SamePath(process.ExecutablePath, paths.SessionAgentExecutable);
            if (!agent && !SamePath(process.ExecutablePath, paths.TrayExecutable))
            {
                continue;
            }
            valid &= process.ProcessId > 0 && ids.Add(process.ProcessId)
                && process.CreationTime > 0 && process.SessionId == sessionId
                && process.IsNormalIntegrity && process.OwnerSid == ownerSid
                && process.Arguments.Count > 0
                && SamePath(process.Arguments[0], process.ExecutablePath);
            if (agent)
            {
                Role role = ParseRole(process.Arguments, out int parent);
                valid &= role != Role.Unknown;
                agents.Add((process, role, parent));
            }
            else
            {
                trays++;
                // The installed Startup shortcut adds exactly --startup;
                // service/driver helper and developer-harness modes are not trays.
                valid &= process.Arguments.Count == 1
                    || process.Arguments is [_, "--startup"];
            }
        }

        int primaries = agents.Count(value => value.Role == Role.Main);
        int helpers = agents.Count - primaries;
        valid &= primaries <= 1 && trays <= 1;
        foreach (var child in agents.Where(value => value.Role != Role.Main))
        {
            Role required = child.Role == Role.SnapTapWorker ? Role.SnapTapInput : Role.Main;
            var parents = agents.Where(value => value.Process.ProcessId == child.Parent).ToArray();
            valid &= parents.Length == 1 && parents[0].Role == required
                && child.Process.ParentProcessId == child.Parent
                && child.Process.ProcessId != child.Parent
                && parents[0].Process.CreationTime < child.Process.CreationTime;
        }
        // Supervisors own exactly one input worker; repeated helpers are not
        // additional primary agents, but are still an ambiguous process family.
        valid &= agents.Where(value => value.Role != Role.Main)
            .GroupBy(value => (value.Role, value.Parent)).All(group => group.Count() == 1);
        return new InstalledProcessInventory(valid, primaries, trays, helpers);
    }

    private static Role ParseRole(IReadOnlyList<string> args, out int parent)
    {
        parent = 0;
        if (args.Count == 1)
        {
            return Role.Main;
        }
        if (args.Count == 3 && TryParent(args[2], out parent))
        {
            return args[1] switch
            {
                "--snap-tap-input" => Role.SnapTapInput,
                "--snap-tap-driver-worker" => Role.SnapTapWorker,
                _ => Role.Unknown,
            };
        }
        if (args.Count == 4 && args[1] == "--thx-audio-helper" && TryParent(args[3], out parent))
        {
            string prefix = $"OpenBlade.Thx.Helper.v2.{parent}.";
            return args[2].StartsWith(prefix, StringComparison.Ordinal)
                && args[2].Length == prefix.Length + 32
                && Guid.TryParseExact(args[2][prefix.Length..], "N", out _)
                ? Role.ThxAudio : Role.Unknown;
        }
        return Role.Unknown;
    }

    private static bool TryParent(string value, out int parent) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parent) && parent > 0;

    private static bool SamePath(string actual, string expected)
    {
        try
        {
            return Path.IsPathFullyQualified(actual)
                && string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private enum Role { Unknown, Main, SnapTapInput, SnapTapWorker, ThxAudio }
}
