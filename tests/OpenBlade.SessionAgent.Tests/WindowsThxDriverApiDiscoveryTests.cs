using System.Security.Cryptography;
using System.Text;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsThxDriverApiDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "OpenBlade.WindowsThxDriverApiDiscoveryTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DriverEnumInstanceIsRetainedByItsThxSwcHardwareId()
    {
        Assert.True(WindowsThxDriverApiDiscovery.IsRelevant(
            @"SWD\DRIVERENUM\private-instance",
            [@"SWC\VEN_THX&SID_RZNB12SVC"]));
        Assert.False(WindowsThxDriverApiDiscovery.IsRelevant(
            @"SWD\DRIVERENUM\private-instance",
            [@"SWC\VEN_OTHER&SID_UNRELATED"]));
    }

    [Fact]
    public void EmptyHardwareIdPropertyIsOrdinaryButMalformedNonemptyListsFailClosed()
    {
        Assert.Empty(WindowsThxDriverApiDiscovery.ParseHardwareIds([0, 0], 0x2012));
        Assert.Throws<InvalidDataException>(() =>
            WindowsThxDriverApiDiscovery.ParseHardwareIds([1, 0], 0x2012));
    }

    [Fact]
    public void ModernRuntimeAcceptsUpdatedCatalogMembersAndIgnoresGeneratedPackageFiles()
    {
        Fixture fixture = CreateModernFixture();
        var calls = new List<(string Catalog, IReadOnlyList<string> Members)>();

        ThxDriverApiEvidence first = WindowsThxDriverApiDiscovery.Evaluate(
            fixture.Profile, fixture.Nodes, fixture.ServiceBinary, null,
            (catalog, members) => Observe(calls, catalog, members));

        Assert.True(first.Trusted, first.Error);
        Assert.Equal(Path.GetFullPath(fixture.ServiceBinary), first.ServiceBinary);
        Assert.DoesNotContain(calls.SelectMany(call => call.Members),
            path => path.EndsWith(".pnf", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(calls, call => call.Members.Contains(fixture.ServiceBinary,
            StringComparer.OrdinalIgnoreCase));

        File.WriteAllText(fixture.ServiceBinary, "new signed service version");
        ThxDriverApiEvidence updated = WindowsThxDriverApiDiscovery.Evaluate(
            fixture.Profile, fixture.Nodes, fixture.ServiceBinary, null,
            (catalog, members) => Observe([], catalog, members));

        Assert.True(updated.Trusted, updated.Error);
        Assert.NotEqual(first.Identity, updated.Identity);
    }

    [Fact]
    public void SwcMustBeUniquelyBoundToTheExactCodec()
    {
        Fixture fixture = CreateModernFixture();
        ThxDriverApiNode service = fixture.Nodes.Single(node =>
            node.HardwareIds.Contains(@"SWC\VEN_THX&SID_RZNB11SVC"));
        ThxDriverApiNode[] unbound = fixture.Nodes.Select(node => node == service
            ? node with { Parent = "UNRELATED\\CODEC" } : node).ToArray();

        ThxDriverApiEvidence evidence = WindowsThxDriverApiDiscovery.Evaluate(
            fixture.Profile, unbound, fixture.ServiceBinary, null, Observe);

        Assert.False(evidence.Trusted);
        Assert.Contains("service package", evidence.Error, StringComparison.OrdinalIgnoreCase);

        ThxDriverApiEvidence sharedPackage = WindowsThxDriverApiDiscovery.Evaluate(
            fixture.Profile, fixture.Nodes.Append(service with { Id = service.Id + "-same-package" }).ToArray(),
            fixture.ServiceBinary, null, Observe);
        Assert.True(sharedPackage.Trusted, sharedPackage.Error);

        string otherServiceInf = Package("other-service", "thx_rznb11_svc.inf",
            [fixture.Profile.Stack.ServiceExecutable]);
        ThxDriverApiEvidence duplicate = WindowsThxDriverApiDiscovery.Evaluate(
            fixture.Profile, fixture.Nodes.Append(service with
            {
                Id = service.Id + "-duplicate",
                StoreInf = otherServiceInf,
            }).ToArray(),
            fixture.ServiceBinary, null, Observe);
        Assert.False(duplicate.Trusted);
        Assert.Contains("ambiguous", duplicate.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogFailureNamesTheBoundApiRoleAndDoesNotFallback()
    {
        Fixture fixture = CreateModernFixture();

        ThxDriverApiEvidence evidence = WindowsThxDriverApiDiscovery.Evaluate(
            fixture.Profile, fixture.Nodes, fixture.ServiceBinary, null,
            (catalog, members) => Path.GetFileName(catalog).Contains("svc", StringComparison.OrdinalIgnoreCase)
                ? throw new CryptographicException(
                    "Windows rejected driver catalog member trust (0x800B0109).")
                : Observe([], catalog, members));

        Assert.False(evidence.Trusted);
        Assert.Contains("service package", evidence.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failed catalog trust", evidence.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thx_rznb11_svc.inf", evidence.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x800B0109", evidence.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRuntimeVerifiesActualServiceAndRegisteredApoAgainstTheirBoundCatalogs()
    {
        ThxAudioCompatibilityProfile profile = ThxAudioCompatibility.Profiles.Single(value =>
            value.ModelNumber == "RZ09-0483");
        string codecInf = Package("codec", "intelaudio.inf", []);
        string serviceInf = Package("service", "THXRTSVC.inf", []);
        string apoInf = Package("apo", "THXRTAPO.inf", []);
        string system = Path.Combine(root, "System32");
        Directory.CreateDirectory(system);
        string service = Write(system, "VSSrv.exe", "installed service update");
        string apo = Write(system, "THXOutAPO-AVX2-v2.6.dll", "installed APO update");
        ThxDriverApiNode[] nodes =
        [
            Node("codec", "", profile.CodecHardwareIds, codecInf),
            Node("service", "codec", [@"SWC\VEN_THX&PID_THXSVC"], serviceInf),
            Node("apo", "codec", [@"SWC\VEN_THX&CID_THXAPO"], apoInf),
        ];
        var calls = new List<(string Catalog, IReadOnlyList<string> Members)>();

        ThxDriverApiEvidence evidence = WindowsThxDriverApiDiscovery.Evaluate(
            profile, nodes, service, apo,
            (catalog, members) => Observe(calls, catalog, members));

        Assert.True(evidence.Trusted, evidence.Error);
        Assert.Contains(calls, call => call.Members.Contains(service, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(calls, call => call.Members.Contains(apo, StringComparer.OrdinalIgnoreCase));

        ThxDriverApiEvidence wrongApo = WindowsThxDriverApiDiscovery.Evaluate(
            profile, nodes, service, Write(system, "unrecognized.dll", "signed but unrelated"), Observe);
        Assert.False(wrongApo.Trusted);
        Assert.Contains("APO", wrongApo.Error, StringComparison.OrdinalIgnoreCase);
    }

    private Fixture CreateModernFixture()
    {
        ThxAudioCompatibilityProfile profile = ThxAudioCompatibility.Profiles.Single(value =>
            value.ModelNumber == "RZ09-0528");
        string codecInf = Package("codec", "realtek.inf", []);
        string serviceInf = Package("service", "thx_rznb11_svc.inf",
            [profile.Stack.ServiceExecutable]);
        string[] apoMembers = profile.Stack.Packages.Single(package =>
                package.InfName == "thx_rznb11_apo4.inf")
            .Files.Where(file => file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || file.Name.EndsWith("_data.bin", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Name).ToArray();
        string apoInf = Package("apo", "thx_rznb11_apo4.inf", apoMembers);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(apoInf)!, "thx_rznb11_apo4.pnf"),
            "Windows generated cache");
        string service = Path.Combine(Path.GetDirectoryName(serviceInf)!, profile.Stack.ServiceExecutable);
        ThxDriverApiNode[] nodes =
        [
            Node("codec", "", profile.CodecHardwareIds, codecInf),
            Node("service", "codec", [@"SWC\VEN_THX&SID_RZNB11SVC"], serviceInf),
            Node("apo", "codec", [@"SWC\VEN_THX&AID_RZNB11APO4"], apoInf),
        ];
        return new(profile, nodes, service);
    }

    private string Package(string directory, string infName, IReadOnlyList<string> members)
    {
        string path = Path.Combine(root, directory);
        Directory.CreateDirectory(path);
        string inf = Write(path, infName, "signed INF");
        Write(path, Path.ChangeExtension(infName, ".cat"), "catalog");
        foreach (string member in members)
        {
            Write(path, member, "signed package member");
        }
        return inf;
    }

    private static string Write(string directory, string name, string contents)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static ThxDriverApiNode Node(string id, string parent,
        IReadOnlyList<string> hardwareIds, string storeInf) =>
        new(id, parent, hardwareIds, 8, 0, storeInf);

    private static string Observe(string catalog, IReadOnlyList<string> members) =>
        Observe([], catalog, members);

    private static string Observe(
        ICollection<(string Catalog, IReadOnlyList<string> Members)> calls,
        string catalog,
        IReadOnlyList<string> members)
    {
        Assert.True(File.Exists(catalog));
        Assert.All(members, member => Assert.True(File.Exists(member), member));
        calls.Add((catalog, members.ToArray()));
        string observation = string.Join('|', members.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => value + ":" + File.ReadAllText(value)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observation)));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Fixture(
        ThxAudioCompatibilityProfile Profile,
        IReadOnlyList<ThxDriverApiNode> Nodes,
        string ServiceBinary);
}
