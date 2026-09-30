using System.Management;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// No concrete scope exists. Implementations must independently prove the exact
// source, loaded lifetime and continuous fresh admission; record shape is only
// an additional rejection check. This contract grants getters, never setters.
internal abstract class CpuLiveCurveRangeTrustedScope
{
    internal abstract bool IsCurrent(CpuLiveCurveContext exactContext);
}

internal enum CpuLiveCurveRangeStatus { Incomplete, Complete, AdmissionDenied, NativeFailure, CleanupUncertain }
internal enum CpuLiveCurveRangeReturnState { NotRead, Absent, Boolean, WrongType, Unreadable }
internal sealed record CpuLiveCurveRangeRaw(uint? Version = null, bool? ReturnValue = null,
    CpuLiveCurveRangeReturnState ReturnState = CpuLiveCurveRangeReturnState.NotRead,
    uint? FirmwareShadow = null, uint? MinimumMagnitude = null, uint? MaximumMagnitude = null,
    uint? Step = null, uint? Selector = null);
internal sealed record CpuLiveCurveRangeResult(CpuLiveCurveTrustedRange? Range,
    CpuLiveCurveRangeStatus Status, bool CleanupComplete, CpuLiveCurveRangeRaw Version,
    CpuLiveCurveRangeRaw MinMax);

internal interface IWindowsCpuCurveRangeSession : IDisposable
{
    IWindowsCpuCurveRangeObject? QueryVersion(Action checkpoint);
    IWindowsCpuCurveRangeObject? GetCurveMinMax(Action checkpoint);
}
internal interface IWindowsCpuCurveRangeObject : IDisposable
{
    IEnumerable<string> Names { get; }
    object? Get(string name);
}

// Synchronous for the entire native lifetime. A caller must retain its actual
// worker until this returns; WMI timeout options do not guarantee cancellation.
// Deliberately unwired: no scope factory, service composition or admission.
internal sealed class WindowsCpuLiveCurveRangeMetadata
{
    private readonly Func<Action, IWindowsCpuCurveRangeSession> open;
    internal WindowsCpuLiveCurveRangeMetadata() : this(NativeSession.Open) { }
    internal WindowsCpuLiveCurveRangeMetadata(Func<Action, IWindowsCpuCurveRangeSession> open) => this.open = open;

    internal CpuLiveCurveRangeResult Read(CpuLiveCurveContext? context,
        CpuLiveCurveRangeTrustedScope? scope, CancellationToken token = default)
    {
        var version = new CpuLiveCurveRangeRaw();
        var minMax = new CpuLiveCurveRangeRaw();
        CpuLiveCurveTrustedRange? range = null;
        var status = CpuLiveCurveRangeStatus.Incomplete;
        bool cleanup = true;
        var resources = new List<IDisposable>();
        void Check()
        {
            try
            {
                if (token.IsCancellationRequested || context is not { IsWellFormed: true }
                    || context.Identity.BiosVersion != "2.02" || scope is null || !scope.IsCurrent(context))
                { throw new AdmissionFailure(); }
            }
            catch (Exception) { throw new AdmissionFailure(); }
        }
        try
        {
            Check();
            var session = open(Check); resources.Add(session); Check();
            var output = session.QueryVersion(Check);
            if (output is not null) { resources.Add(output); }
            Check();
            bool validVersion = output is not null && DecodeVersion(output, Check, ref version);
            if (validVersion)
            {
                Check();
                output = session.GetCurveMinMax(Check);
                if (output is not null) { resources.Add(output); }
                Check();
                if (output is not null) { range = DecodeMinMax(output, Check, ref minMax); }
                Check();
            }
            status = range is null ? CpuLiveCurveRangeStatus.Incomplete : CpuLiveCurveRangeStatus.Complete;
        }
        catch (NativeOpenCleanupFailure) { cleanup = false; status = CpuLiveCurveRangeStatus.CleanupUncertain; }
        catch (AdmissionFailure) { status = CpuLiveCurveRangeStatus.AdmissionDenied; }
        catch (Exception) { status = CpuLiveCurveRangeStatus.NativeFailure; }
        finally
        {
            for (int index = resources.Count - 1; index >= 0; index--)
            {
                // Revocation never skips cleanup. Cleanup uncertainty is sticky.
                try { Check(); } catch { if (status != CpuLiveCurveRangeStatus.CleanupUncertain) { status = CpuLiveCurveRangeStatus.AdmissionDenied; } }
                try { resources[index].Dispose(); }
                catch (AdmissionFailure) { if (status != CpuLiveCurveRangeStatus.CleanupUncertain) { status = CpuLiveCurveRangeStatus.AdmissionDenied; } }
                catch { cleanup = false; status = CpuLiveCurveRangeStatus.CleanupUncertain; }
                try { Check(); } catch { if (status != CpuLiveCurveRangeStatus.CleanupUncertain) { status = CpuLiveCurveRangeStatus.AdmissionDenied; } }
            }
        }
        return new(status == CpuLiveCurveRangeStatus.Complete && cleanup ? range : null, status, cleanup, version, minMax);
    }

    private static bool DecodeVersion(IWindowsCpuCurveRangeObject output, Action check, ref CpuLiveCurveRangeRaw raw)
    {
        var fields = Fields(output, ["version", "ReturnValue"], check, ref raw);
        // Exact BIOS2.02 is enforced by every checkpoint. Only a complete
        // successful enumeration can establish the pinned COM compatibility.
        return fields is not null && fields.TryGetValue("version", out var value) && value is uint word && word == 7
            && (raw.ReturnState == CpuLiveCurveRangeReturnState.Absent
                || raw is { ReturnState: CpuLiveCurveRangeReturnState.Boolean, ReturnValue: true });
    }
    private static CpuLiveCurveTrustedRange? DecodeMinMax(IWindowsCpuCurveRangeObject output, Action check,
        ref CpuLiveCurveRangeRaw raw)
    {
        var header = Fields(output, ["ReturnValue", "pack"], check, ref raw);
        if (header is null || !header.TryGetValue("pack", out var embedded) || embedded is not IWindowsCpuCurveRangeObject pack)
        { return null; }
        // Native output owns embedded objects. Fakes model the same ownership.
        var fields = Fields(pack, ["Current", "Min", "Max", "Stepping", "Value"], check, ref raw);
        if (fields is null || fields.Count != 5 || raw.ReturnState != CpuLiveCurveRangeReturnState.Boolean || raw.ReturnValue != true
            || raw.FirmwareShadow is null || raw.MinimumMagnitude is not uint min || raw.MaximumMagnitude is not uint max
            || raw.Step != 1 || raw.Selector != 0x5000A || min > max || max > 50)
        { return null; }
        return new(0x5000A, (int)min, (int)max, 1, true);
    }
    private static Dictionary<string, object?>? Fields(IWindowsCpuCurveRangeObject output, string[] allowed,
        Action check, ref CpuLiveCurveRangeRaw raw)
    {
        var names = new List<string>();
        check();
        foreach (string name in output.Names)
        {
            check();
            if (names.Count >= allowed.Length || !allowed.Contains(name, StringComparer.Ordinal)
                || names.Contains(name, StringComparer.Ordinal)) { return null; }
            names.Add(name);
        }
        check();
        if (allowed.Contains("ReturnValue") && !names.Contains("ReturnValue"))
        { raw = raw with { ReturnState = CpuLiveCurveRangeReturnState.Absent }; }
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            check();
            object? value;
            try { value = output.Get(name); }
            catch
            {
                if (name == "ReturnValue") { raw = raw with { ReturnState = CpuLiveCurveRangeReturnState.Unreadable }; }
                throw;
            }
            fields.Add(name, value);
            uint? word = value is uint number ? number : null;
            raw = name switch
            {
                "version" => raw with { Version = word },
                "ReturnValue" => raw with
                {
                    ReturnValue = value as bool?,
                    ReturnState = value is bool ? CpuLiveCurveRangeReturnState.Boolean : CpuLiveCurveRangeReturnState.WrongType,
                },
                "Current" => raw with { FirmwareShadow = word },
                "Min" => raw with { MinimumMagnitude = word },
                "Max" => raw with { MaximumMagnitude = word },
                "Stepping" => raw with { Step = word },
                "Value" => raw with { Selector = word },
                _ => raw,
            };
            check();
        }
        return fields;
    }
    private sealed class AdmissionFailure : Exception;

    private sealed class NativeObject(ManagementBaseObject source, Action check) : IWindowsCpuCurveRangeObject
    {
        private readonly List<NativeObject> children = [];
        public IEnumerable<string> Names
        { get { foreach (PropertyData property in source.Properties) { yield return property.Name; } } }
        public object? Get(string name)
        {
            object? value = source[name];
            if (value is not ManagementBaseObject embedded) { return value; }
            var child = new NativeObject(embedded, check); children.Add(child); return child;
        }
        public void Dispose()
        {
            Exception? error = null;
            bool revoked = false;
            foreach (var child in children) { DisposeResource(child); }
            DisposeResource(source);
            if (error is not null) { throw error; }
            if (revoked) { throw new AdmissionFailure(); }
            void DisposeResource(IDisposable resource)
            {
                try { check(); } catch (AdmissionFailure) { revoked = true; }
                try { resource.Dispose(); }
                catch (AdmissionFailure) { revoked = true; }
                catch (Exception exception) { error ??= exception; }
                try { check(); } catch (AdmissionFailure) { revoked = true; }
            }
        }
    }
    private sealed class NativeSession : IWindowsCpuCurveRangeSession
    {
        private readonly List<IDisposable> resources = [];
        private ManagementObject instance = null!;
        private Action check = null!;
        internal static IWindowsCpuCurveRangeSession Open(Action check)
        {
            var session = new NativeSession { check = check };
            try
            {
                check();
                var search = new ManagementObjectSearcher(new ManagementScope(@"root\wmi"),
                    new ObjectQuery("SELECT * FROM AMD_ACPI"), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) });
                session.resources.Add(search); check();
                var objects = search.Get(); session.resources.Add(objects); check();
                int count = 0;
                foreach (ManagementObject candidate in objects)
                {
                    session.resources.Add(candidate); check();
                    if (++count > 1) { throw new InvalidDataException("Unexpected provider count."); }
                    session.instance = candidate;
                }
                check();
                if (count != 1) { throw new InvalidDataException("Missing provider."); }
                return session;
            }
            catch
            {
                // Preserve an explicit cleanup exception rather than returning
                // an apparently terminal clean failure after partial Open.
                try { session.Dispose(); }
                catch (AdmissionFailure) { throw; }
                catch (Exception exception) { throw new NativeOpenCleanupFailure(exception); }
                throw;
            }
        }
        public IWindowsCpuCurveRangeObject? QueryVersion(Action checkpoint)
        {
            checkpoint();
            var output = instance.InvokeMethod("QueryVersion", null, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) });
            return output is null ? null : new NativeObject(output, checkpoint);
        }
        public IWindowsCpuCurveRangeObject? GetCurveMinMax(Action checkpoint)
        {
            checkpoint();
            var input = instance.GetMethodParameters("GetMinMaxSteppingValues"); resources.Add(input); checkpoint();
            input["ID"] = (uint)0x5000A; checkpoint();
            var output = instance.InvokeMethod("GetMinMaxSteppingValues", input,
                new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) });
            return output is null ? null : new NativeObject(output, checkpoint);
        }
        public void Dispose()
        {
            Exception? error = null;
            bool revoked = false;
            for (int index = resources.Count - 1; index >= 0; index--)
            {
                try { check(); } catch (AdmissionFailure) { revoked = true; }
                try { resources[index].Dispose(); } catch (Exception exception) { error ??= exception; }
                try { check(); } catch (AdmissionFailure) { revoked = true; }
            }
            resources.Clear();
            if (error is not null) { throw error; }
            if (revoked) { throw new AdmissionFailure(); }
        }
    }
    private sealed class NativeOpenCleanupFailure(Exception inner) : Exception("Native range cleanup uncertain.", inner);
}
