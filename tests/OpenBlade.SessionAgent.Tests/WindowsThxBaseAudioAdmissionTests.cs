using System.Security.Cryptography;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsThxBaseAudioAdmissionTests
{
    private static ThxAudioCompatibilityProfile Profile(string model = "RZ09-0581") =>
        ThxAudioCompatibility.Profiles.Single(p => p.ModelNumber == model);
    private static ThxBaseAudioNode Node(string id, string parent, string hardware, string inf = "", string section = "",
        string service = "", string provider = "", string version = "", string deviceClass = "System", string[]? compatible = null) =>
        new(id, parent, [hardware], compatible ?? [], inf.Length == 0 ? 0x400u : 8u, inf.Length == 0 ? 28u : 0u,
            inf.Length == 0 ? "" : "oem42.inf", inf.Length == 0 ? "" : Path.Combine("C:\\Windows\\System32\\DriverStore\\FileRepository\\fixture", inf),
            section, provider, version, service, deviceClass);
    private static ThxBaseAudioNode Usb(string model = "RZ09-0581") => Node("USB\\VID_1532&PID_" + Profile(model).UsbProductId + "\\fixture", "usb", "usb");
    private static ThxBaseAudioNode Root(bool bound = false) => Node("PCI\\root", "PCI\\parent",
        @"PCI\VEN_8086&DEV_E328&SUBSYS_30101A58&REV_04", bound ? "IntcAudioBus.inf" : "", bound ? "IntcAudioBusPTLplus.NT" : "",
        bound ? "IntcAudioBus" : "", bound ? "Intel(R) Corporation" : "", bound ? "20.43.12634.0" : "",
        compatible: [@"PCI\VEN_8086&DEV_E328&CC_040380"]);
    private static ThxBaseAudioNode Realtek(string model = "RZ09-0528", bool bound = false) => Node("HDAUDIO\\root", "PCI\\hda",
        Profile(model).CodecHardwareIds.Single(), bound ? "HDXACPRazer.inf" : "", bound ? "IntcAzAudModel.NTamd64" : "",
        bound ? "IntcAzAudAddService" : "", bound ? "Realtek Semiconductor Corp." : "", bound ? "6.0.9906.1" : "", "MEDIA",
        [@"HDAUDIO\FUNC_01"]);
    private static ThxBaseAudioAdmissionSnapshot Evaluate(IReadOnlyList<ThxBaseAudioNode> nodes, string model = "RZ09-0581",
        Func<ThxBaseAudioNode, ThxBaseAudioRecipe, string>? pinned = null, Func<string, string, string, string>? inbox = null) =>
        WindowsThxBaseAudioAdmission.Evaluate(Profile(model), nodes, pinned ?? ((n, _) => Path.GetFileName(n.StoreInf) + "=trusted-hash"),
            inbox ?? ((inf, _, _) => inf + "=trusted-windows-member"));

    [Fact]
    public void ExactUniqueUnboundIntelRootAdmitsMissingChildrenWithoutInferringReadiness()
    {
        var result = Evaluate([Usb(), Root()]);
        Assert.True(result.ExactRoot);
        Assert.True(result.CanInstall);
        Assert.False(result.Ready);
        Assert.Equal(Root().Id, result.Target);
        Assert.Contains("problem 28", result.Identity);
    }

    [Theory]
    [InlineData("RZ09-0528")]
    [InlineData("RZ09-0530")]
    public void CompleteActuallyBoundRealtekPackageIsReadyAndNeverReinstalled(string model)
    {
        ThxBaseAudioNode? verified = null;
        var result = Evaluate([Usb(model), Realtek(model, true)], model, (node, _) => { verified = node; return "bound-file-hashes"; });
        Assert.True(result.Ready);
        Assert.False(result.CanInstall);
        Assert.NotNull(verified);
        Assert.Equal(Realtek(model, true).StoreInf, verified.StoreInf);
        Assert.Equal("oem42.inf", verified.PublishedInf);
        Assert.Contains("bound-file-hashes", result.Identity);
    }

    [Fact]
    public void MissingDriverIsNotInferredFromAnUnreadableOrFailedBinding()
    {
        foreach (var root in new[] { Root() with { Problem = 0 }, Root() with { Problem = 10 }, Root() with { PublishedInf = "oem99.inf" } })
        {
            Assert.False(Evaluate([Usb(), root]).CanInstall);
        }

        Assert.False(Evaluate([Usb()]).ExactRoot);
        Assert.False(Evaluate([Root()]).ExactRoot);
    }

    [Fact]
    public void DuplicateRootAndWrongModelAreRefused()
    {
        Assert.False(Evaluate([Usb(), Root(), Root() with { Id = "PCI\\second" }]).ExactRoot);
        Assert.False(Evaluate([Usb(), Root() with { HardwareIds = [@"PCI\VEN_8086&DEV_E328&SUBSYS_99999999"] }]).ExactRoot);
        Assert.False(Evaluate([Usb("RZ09-0528"), Realtek("RZ09-0530")], "RZ09-0528").ExactRoot);
    }

    [Theory]
    [InlineData("E428")]
    [InlineData("4D28")]
    public void OtherIntelInfMatchesCannotBecomeIncidentalInstallTargets(string device)
    {
        var incidental = Node("PCI\\other", "PCI\\parent", $@"PCI\VEN_8086&DEV_{device}&SUBSYS_99999999",
            compatible: [$@"PCI\VEN_8086&DEV_{device}&CC_0401"]);
        Assert.False(Evaluate([Usb(), Root(), incidental]).ExactRoot);
    }

    [Fact]
    public void RealtekInfIncidentalTargetIsRefusedButUnmatchedAudioRemainsAllowed()
    {
        var incidental = Node("HDAUDIO\\other", "PCI\\other", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1A58201E");
        Assert.False(Evaluate([Usb("RZ09-0528"), Realtek(), incidental], "RZ09-0528").ExactRoot);
        var unrelated = incidental with { HardwareIds = [@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_99999999"] };
        Assert.True(Evaluate([Usb("RZ09-0528"), Realtek(), unrelated], "RZ09-0528").CanInstall);
    }

    [Theory]
    [InlineData("9.0.99999.0", "HDXACPRazer.inf")]
    [InlineData("6.0.9906.1", "custom.inf")]
    public void UnknownOrNewerRealtekBindingIsPreserved(string version, string inf)
    {
        var root = Realtek(bound: true) with { Version = version, StoreInf = Path.Combine("C:\\store", inf) };
        var result = Evaluate([Usb("RZ09-0528"), root], "RZ09-0528", (_, _) => throw new Exception("Unknown binding must not reach pinned trust."));
        Assert.False(result.Ready);
        Assert.False(result.CanInstall);
    }

    [Fact]
    public void MicrosoftBasicHdaRequiresIndependentInboxTrust()
    {
        var basic = Realtek(bound: true) with
        {
            StoreInf = Path.Combine("C:\\store", "hdaudio.inf"),
            Section = "HdAudModel",
            Provider = "Microsoft",
            Version = "10.0.99999.123",
            Service = "HdAudAddService"
        };
        Assert.True(Evaluate([Usb("RZ09-0528"), basic], "RZ09-0528").CanInstall);
        Assert.False(Evaluate([Usb("RZ09-0528"), basic], "RZ09-0528", inbox: (_, _, _) => throw new CryptographicException()).CanInstall);
    }

    [Fact]
    public void IntelInboxPrerequisitesMustAlreadyBeTrustedEvenWhenRootIsUnbound()
    {
        var result = Evaluate([Usb(), Root()], inbox: (_, _, _) => throw new FileNotFoundException());
        Assert.True(result.ExactRoot);
        Assert.False(result.CanInstall);
        Assert.False(result.Ready);
    }

    [Fact]
    public void PinnedMemberMismatchDoesNotAdmitRepairOfAnUnknownPackage()
    {
        var result = Evaluate([Usb("RZ09-0528"), Realtek(bound: true)], "RZ09-0528",
            (_, _) => throw new InvalidDataException("Changed package member."));
        Assert.True(result.ExactRoot);
        Assert.False(result.Ready);
        Assert.False(result.CanInstall);
    }

    [Fact]
    public void CompleteIntelGraphRequiresActualAncestryAndBothInboxAudioRoles()
    {
        var nodes = IntelGraph();
        Assert.True(Evaluate(nodes).Ready);
        Assert.False(Evaluate(nodes).CanInstall);
        var wrongParent = nodes.Select(n => n.Id == "INTELAUDIO\\sdw" ? n with { Parent = Root().Id } : n).ToArray();
        Assert.False(Evaluate(wrongParent).CanInstall);
        Assert.False(Evaluate(wrongParent).Ready);
        Assert.False(Evaluate(nodes.Where(n => n.Id != "SOUNDWIRE\\aggregate-speaker").ToArray()).Ready);
        Assert.False(Evaluate(nodes, inbox: (_, _, _) => throw new CryptographicException()).Ready);
    }

    [Fact]
    public void RecognizedPartialIntelGraphCanInstallButCannotHideUnknownBoundChild()
    {
        var nodes = IntelGraph().Where(n => n.Id is "PCI\\root" or "INTELAUDIO\\oed" || n.Id.StartsWith("USB\\", StringComparison.Ordinal)).ToArray();
        Assert.True(Evaluate(nodes).CanInstall);
        var changed = nodes.Select(n => n.Id == "INTELAUDIO\\oed" ? n with { Version = "20.99.99999.0" } : n).ToArray();
        Assert.False(Evaluate(changed).CanInstall);
    }

    [Fact]
    public void RepeatedEvaluationRechecksTrustAndRecordsBindingDrift()
    {
        int calls = 0;
        var nodes = new[] { Usb("RZ09-0528"), Realtek(bound: true) };
        var first = Evaluate(nodes, "RZ09-0528", (_, _) => { calls++; return "first-hash"; });
        var second = Evaluate(nodes, "RZ09-0528", (_, _) => { calls++; return "second-hash"; });
        Assert.Equal(2, calls);
        Assert.NotEqual(first.Identity, second.Identity);
        Assert.NotEqual(first.Identity, Evaluate([nodes[0], nodes[1] with { PublishedInf = "oem43.inf" }], "RZ09-0528").Identity);
    }

    [Fact]
    public void CompatibleOnlyIncidentalMatchIsKeptByNativePrefilterAndRefusedByEvaluation()
    {
        var incidental = Node("HDAUDIO\\other", "PCI\\other", @"HDAUDIO\generic",
            compatible: [@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1A58201E"]);
        Assert.True(WindowsThxBaseAudioAdmission.MatchesInstallRoot(incidental.HardwareIds, incidental.CompatibleIds,
            ThxBaseAudioPackage.ForModel("RZ09-0528").RootHardwareIds));
        Assert.False(Evaluate([Usb("RZ09-0528"), Realtek(), incidental], "RZ09-0528").ExactRoot);
    }
    [Fact]
    public void KnownCodecCannotUseAnotherTrustedPeripheralOnTheSameSoundWireController()
    {
        var nodes = IntelGraph().Select(n => n.Id == "SOUNDWIRE\\peripheral"
            ? n with { HardwareIds = [@"SOUNDWIRE\SDCA_PERIPHERAL&MAN_025D&PART_1320&VER_03&SUBSYS_30101A58"] } : n).ToArray();
        Assert.False(Evaluate(nodes).Ready);
        Assert.False(Evaluate(nodes).CanInstall);
    }

    [Fact]
    public void UnobservedFunctionAliasDoesNotInventAPeripheralMapping()
    {
        var nodes = IntelGraph().Select(n => n.Id == "SOUNDWIRE\\function"
            ? n with { HardwareIds = [@"SOUNDWIRE\SDCA_11&MAN_025D&FUNC_0721&TYPE_0B&VER_01&ADR_04&LID_03&UID_00&SUBSYS_30101A58"] }
            : n.Id == "SOUNDWIRE\\dynamic-speaker" ? n with { HardwareIds = [Profile().CodecHardwareIds[6], @"SDCA\DynamicEnumChild"] } : n).ToArray();
        Assert.False(Evaluate(nodes).Ready);
        Assert.False(Evaluate(nodes).CanInstall);
    }
    [Fact]
    public void OptionalCompatibleIdsAcceptObservedEmptyMultiszWithoutWeakeningHardwareIds()
    {
        Assert.Empty(WindowsThxBaseAudioAdmission.ParseCompatibleIds([0, 0], 0x2012));
        Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ParseHardwareIds([0, 0], 2, 0x2012));
        Assert.Throws<InvalidDataException>(() => WindowsThxBaseAudioAdmission.ParseCompatibleIds([0, 0], 0x12));
        Assert.Throws<InvalidDataException>(() => WindowsThxBaseAudioAdmission.ParseCompatibleIds([65, 0], 0x2012));
        Assert.Equal([@"PCI\VEN_8086&DEV_E328&CC_0401"], WindowsThxBaseAudioAdmission.ParseCompatibleIds(
            System.Text.Encoding.Unicode.GetBytes("PCI\\VEN_8086&DEV_E328&CC_0401\0\0"), 0x2012));
    }
    [Theory]
    [InlineData("1.0.4139.0", true)]
    [InlineData("1.0.9999.0", false)]
    public void DetectionVerificationChildUsesItsOwnReviewedBuildAndPackageTrust(string version, bool ready)
    {
        var child = Node("INTELAUDIO\\detection", "INTELAUDIO\\oed",
            @"INTELAUDIO\DIF_0000&UIF_0000&CTLR_DEV_E328&VEN_8086&DetectionVerification",
            "DetectionVerificationDrv.inf", "IntcDVWin11.NT", "WUDFRd", "Intel(R) Corporation", version);
        bool verified = false;
        var result = Evaluate([.. IntelGraph(), child], pinned: (node, _) =>
        {
            verified |= node.Id == child.Id;
            return "verified-package";
        });
        Assert.Equal(ready, result.Ready);
        Assert.Equal(ready, verified);
        Assert.False(result.CanInstall);
    }
    private static ThxBaseAudioNode[] IntelGraph()
    {
        var root = Root(true);
        var oed = Node("INTELAUDIO\\oed", root.Id, @"INTELAUDIO\DSP_CTLR_DEV_E328&VEN_8086&DEV_0222&SUBSYS_30101A58",
            "IntcOED.inf", "IntcOED_PTL_11.NT", "IntcOED", "Intel(R) Corporation", "20.43.12634.0");
        var sdw = Node("INTELAUDIO\\sdw", oed.Id, @"INTELAUDIO\SDW_CTLR_DEV_E328&VEN_8086&DEV_0333&SUBSYS_30101A58",
            "IntcSdwBus.inf", "IntcSdwBus.NT", "IntcSdwBus", "Intel(R) Corporation", "20.43.12634.0");
        var peripheral = Node("SOUNDWIRE\\peripheral", sdw.Id, @"SOUNDWIRE\SDCA_PERIPHERAL&MAN_025D&PART_0721&VER_03&SUBSYS_30101A58",
            "sdcamfd.inf", "SdcaMfd_Device.NT", "SdcaMfdInbox", "Microsoft", "10.0.99999.1", "System", [@"SOUNDWIRE\SDCA_PERIPHERAL"]);
        var function = Node("SOUNDWIRE\\function", peripheral.Id, @"SOUNDWIRE\SDCA_FUNCTION_11&MAN_025D&FUNC_0721&TYPE_0B&VER_01&ADR_04&LID_03&UID_00&SUBSYS_30101A58",
            "sdcaclass.inf", "SdcaClass_Device.NT", "SdcaClassInbox", "Microsoft", "10.0.99999.1", "MEDIA", [@"SDCA_11\AUDIOFUNCTION"]);
        var dynamic = Node("SOUNDWIRE\\dynamic-speaker", function.Id, Profile().CodecHardwareIds[3], "sdcaclass.inf",
            "SdcaClass_Device_Child.NT", "", "Microsoft", "10.0.99999.1", "MEDIA") with
        { HardwareIds = [Profile().CodecHardwareIds[3], @"SDCA\DynamicEnumChild"] };
        var aggregator = Node("SOUNDWIRE\\aggregator", sdw.Id, @"SOUNDWIRE\SDCA_AGGREGATOR", "sdcaaggregator.inf",
            "SdcaAggregator_Device.NT", "SdcaAggregatorInbox", "Microsoft", "10.0.99999.1", "MEDIA");
        var aggregated = Node("SOUNDWIRE\\aggregate-speaker", aggregator.Id, Profile().CodecHardwareIds[0], "sdcaaggregator.inf",
            "SdcaAggregator_ChildDevice.NT", "", "Microsoft", "10.0.99999.1", "MEDIA") with
        { HardwareIds = [Profile().CodecHardwareIds[0], @"SDCA\AGGREGATEDSPEAKER"] };
        return [Usb(), root, oed, sdw, peripheral, function, dynamic, aggregator, aggregated];
    }
}
