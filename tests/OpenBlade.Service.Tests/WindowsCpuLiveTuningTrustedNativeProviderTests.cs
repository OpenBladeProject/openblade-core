using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsCpuLiveTuningTrustedNativeProviderTests
{
    [Fact]
    public async Task PreparedProofSharesOnlyExactReadPrefixAndRequiresFreshSetterAuthority()
    {
        using var fixture = new Fixture();
        int full = 0;
        fixture.GetterAdmission = (_, token, lifetime) => new(token, lifetime,
            _ => { full++; return fixture.Admitted; }, continuity: _ => fixture.Admitted);
        await fixture.Run(context =>
        {
            var request = Request(-2);
            using var proof = fixture.Provider.CreatePreparedWriteProof(context, request, fixture.OperationToken)!;
            Assert.NotNull(proof); Assert.Equal(1, full);
            int hosts = fixture.Platform.HostReads, usb = fixture.UsbReads;
            Assert.False(proof.PreparationCurrent(Request(-2)));
            Assert.False(proof.Current(request)); // A read prefix is never setter authority.
            Assert.True(proof.Consume(request)); Assert.False(proof.Consume(request));
            for (int i = 0; i < 25; i++)
            {
                Assert.True(proof.PreparationCurrent(request));
                Assert.True(context.MatchesHardwareContext(fixture.Provider.ReadFreshContext(fixture.OperationToken)));
            }
            Assert.Equal(hosts, fixture.Platform.HostReads); Assert.Equal(usb, fixture.UsbReads);
            using (var adopted = proof.BeginRead(request))
            {
                Assert.NotNull(adopted); Assert.Equal(1, full);
                Assert.False(proof.PreparationCurrent(request));
                Assert.Throws<InvalidOperationException>(() => proof.BeginRead(request));
                Assert.False(proof.Current(request));
                Assert.NotNull(proof.ReadFreshRange(request));
                Assert.True(proof.CompleteRead(request));
            }
            Assert.Equal(2, full);
            Assert.True(proof.Current(request));
            fixture.Admitted = false;
            Assert.False(proof.Current(request));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnadoptedPreparationIsRevokedAndDisposedBeforeNextRequest(bool cancel)
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        fixture.GetterAdmission = (_, token, lifetime) => new(token, lifetime, _ => true, continuity: _ => true);
        await fixture.Run(context =>
        {
            var request = Request(-2);
            var proof = fixture.Provider.CreatePreparedWriteProof(context, request, cancellation.Token)!;
            Assert.NotNull(proof);
            if (cancel) { cancellation.Cancel(); }
            else { fixture.ControllerCurrent = false; }
            Assert.False(proof.Consume(request));
            fixture.ControllerCurrent = true;
            Assert.False(proof.PreparationCurrent(request));
            proof.Dispose(); proof.Dispose();
            Assert.False(proof.Current(request));
            Assert.Throws<InvalidOperationException>(() => proof.BeginRead(request));
            using var next = fixture.Provider.CreatePreparedWriteProof(context, Request(-2), fixture.OperationToken);
            Assert.NotNull(next);
        });
    }

    [Fact]
    public async Task CompletePlanPreflightHasConstantDiscoveryAndNoWriteAuthority()
    {
        using var fixture = new Fixture();
        int full = 0;
        fixture.GetterAdmission = (_, token, lifetime) => new(token, lifetime,
            _ => { full++; return fixture.Admitted; }, continuity: _ => fixture.Admitted);
        int? expectedHosts = null, expectedUsb = null;
        foreach (int count in new[] { 1, 36 })
        {
            await fixture.Run(context =>
            {
                int hosts = fixture.Platform.HostReads, usb = fixture.UsbReads, initialFull = full;
                fixture.OnMinMax = () => Assert.Null(fixture.Provider.CreateWriteProof(context, Request(-2), fixture.OperationToken));
                Assert.True(fixture.Provider.CheckPlanAdmission(context, PlanChecks().Take(count).ToArray(), fixture.OperationToken));
                int hostReads = fixture.Platform.HostReads - hosts, usbReads = fixture.UsbReads - usb;
                Assert.Equal(2, full - initialFull);
                if (expectedHosts is null) { expectedHosts = hostReads; expectedUsb = usbReads; }
                else { Assert.Equal(expectedHosts, hostReads); Assert.Equal(expectedUsb, usbReads); }
                fixture.Admitted = false;
                Assert.Null(fixture.Provider.CreateWriteProof(context, Request(-2), fixture.OperationToken));
            });
            fixture.Admitted = true;
        }
        Assert.Equal(2, fixture.MetadataOpens); // A later request gets its own range and scope.
    }

    [Theory]
    [InlineData("controller")]
    [InlineData("cancel")]
    [InlineData("disposed")]
    [InlineData("final-context")]
    public async Task CompletePlanPreflightRejectsContinuityLossAndAlwaysDisposes(string failure)
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        CpuLiveGetterAdmission? active = null;
        int full = 0;
        fixture.GetterAdmission = (_, token, lifetime) => active = new(token, lifetime,
            _ => { if (++full == 2 && failure == "final-context") { fixture.Platform.HostValid = false; } return true; },
            continuity: _ => true);
        await fixture.Run(context =>
        {
            fixture.OnMinMax = () =>
            {
                if (failure == "controller") { fixture.ControllerCurrent = false; }
                if (failure == "cancel") { cancellation.Cancel(); }
                if (failure == "disposed") { active!.Dispose(); }
            };
            Assert.False(fixture.Provider.CheckPlanAdmission(context, PlanChecks(), cancellation.Token));
            Assert.False(active!.Allows());
            // The failed preflight did not leave a nested scope or grant a proof.
            fixture.Admitted = false;
            Assert.Null(fixture.Provider.CreateWriteProof(context, Request(-2), fixture.OperationToken));
        });
    }

    private static CpuLiveCurveAdmissionCheck[] PlanChecks()
    {
        var checks = new List<CpuLiveCurveAdmissionCheck>();
        foreach (var core in CpuLiveCurveTuningTransactionTests.ValuesFixture())
        {
            Assert.True(CpuLiveCurveCommand.TryCreate(core.PhysicalCoreId, -2, 0xFF55, out var forward));
            Assert.True(CpuLiveCurveCommand.TryCreate(core.PhysicalCoreId, -1, 0xFF55, out var reverse));
            checks.Add(new(forward, CpuLiveCurveCommandPurpose.Forward));
            checks.Add(new(reverse, CpuLiveCurveCommandPurpose.Restore));
        }
        checks.AddRange(checks.Where(check => check.Purpose == CpuLiveCurveCommandPurpose.Restore).ToArray());
        return checks.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumedOperationReadScopeBorrowsMetadataAndFullyRevalidates(bool finalDrift)
    {
        using var fixture = new Fixture();
        int full = 0;
        fixture.GetterAdmission = (_, token, lifetime) => new(token, lifetime,
            _ => { full++; return fixture.Admitted; }, continuity: _ => fixture.Admitted);
        await fixture.Run(context =>
        {
            var request = Request(-2);
            var proof = fixture.Provider.CreateWriteProof(context, request, fixture.OperationToken)!;
            Assert.Throws<InvalidOperationException>(() => proof.BeginRead(request));
            Assert.True(proof.Consume(request));
            int initialFull = full;
            using (var scope = proof.BeginRead(request))
            {
                Assert.NotNull(scope);
                int hosts = fixture.Platform.HostReads, usb = fixture.UsbReads;
                var other = Request(-2);
                Assert.False(proof.ReadCurrent(other));
                Assert.Throws<InvalidOperationException>(() => proof.BeginRead(other));
                for (int i = 0; i < 250; i++) { Assert.True(proof.ReadCurrent(request)); }
                fixture.OnMinMax = () => Assert.Null(fixture.Provider.CreateWriteProof(context, other, default));
                Assert.NotNull(proof.ReadFreshRange(request));
                Assert.Equal(hosts, fixture.Platform.HostReads); Assert.Equal(usb, fixture.UsbReads);
                Assert.Equal(initialFull + 1, full); // Metadata borrowed, never nested or disposed the caller.
                Assert.True(proof.ReadCurrent(request));
                if (finalDrift) { fixture.Platform.HostValid = false; }
                Assert.Equal(!finalDrift, proof.CompleteRead(request));
                Assert.True(fixture.Platform.HostReads > hosts);
                Assert.Equal(initialFull + 2, full);
                if (finalDrift)
                { fixture.Platform.HostValid = true; Assert.False(proof.ReadCurrent(request)); }
            }
            // Read continuity never substitutes for fresh write authority.
            fixture.Admitted = false;
            Assert.False(proof.Current(request));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerPeerGetterSharesContinuityAndRequiresIndependentFullCompletion(bool finalDrift)
    {
        using var fixture = new Fixture();
        int full = 0, continuity = 0;
        fixture.GetterAdmission = (_, token, lifetime) => new(token, lifetime,
            _ => { full++; return fixture.Admitted; }, continuity: _ => { continuity++; return fixture.Admitted; });
        CpuPmInstalledApproval? previous = null;
        await fixture.Run(_ =>
        {
            ICpuLivePowerTrustedNativeProvider provider = fixture.Provider;
            for (int read = 0; read < 2; read++)
            {
                using var scope = provider.BeginGetter(fixture.OperationToken);
                var context = provider.ReadFreshContext(fixture.OperationToken)!;
                var approval = provider.CreateInstalledObservationApproval(context, fixture.OperationToken)!;
                Assert.True(approval.GetterScoped);
                int hosts = fixture.Platform.HostReads, usb = fixture.UsbReads;
                for (int checkpoint = 0; checkpoint < 250; checkpoint++)
                {
                    Assert.True(approval.IsCurrent());
                    Assert.NotNull(provider.ReadFreshContext(fixture.OperationToken));
                }
                Assert.Equal(hosts, fixture.Platform.HostReads); Assert.Equal(usb, fixture.UsbReads);
                Assert.True(continuity > 250);
                Assert.Null(provider.CreateWriteProof(context, null!, fixture.OperationToken));
                Assert.False(provider.CheckWriteAdmission(context, default, default, null!, null!, fixture.OperationToken));
                if (previous is not null) { Assert.False(previous.IsCurrent()); }
                if (finalDrift) { fixture.Platform.HostValid = false; }
                Assert.Equal(!finalDrift, provider.CompleteGetter(context, fixture.OperationToken));
                Assert.True(fixture.Platform.HostReads > hosts);
                Assert.Equal((read + 1) * 2, full);
                if (finalDrift)
                {
                    fixture.Platform.HostValid = true; Assert.False(approval.IsCurrent()); break;
                }
                previous = approval;
            }
        });
    }

    [Fact]
    public async Task MetadataCheckpointsShareGetterContextButCannotAuthorizeAWrite()
    {
        using var fixture = new Fixture();
        int full = 0, continuity = 0;
        fixture.GetterAdmission = (_, token, lifetime) => new(token, lifetime,
            _ => { full++; return true; }, continuity: _ => { continuity++; return true; });
        await fixture.Run(context =>
        {
            fixture.OnMinMax = () =>
            {
                var nested = Request(-2);
                Assert.Null(fixture.Provider.CreateWriteProof(context, nested, default));
            };
            var request = Request(-2);
            Assert.True(fixture.Provider.CheckWriteAdmission(context, request.Command, request.Purpose, fixture.OperationToken));
            Assert.Equal(2, full); Assert.True(continuity > 10);
            // Scope has ended; actual command admission remains independent.
            fixture.Admitted = false;
            Assert.Null(fixture.Provider.CreateWriteProof(context, request, fixture.OperationToken));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetterReusesExactContextThenRequiresFullFinalRevalidation(bool finalDrift)
    {
        using var fixture = new Fixture();
        int full = 0, continuous = 0;
        fixture.GetterAdmission = (_, token, lifetime) => new(token, lifetime,
            _ => { full++; return fixture.Admitted; }, continuity: _ => { continuous++; return fixture.Admitted; });
        await fixture.Run(_ =>
        {
            for (int query = 0; query < 2; query++)
            {
                using var scope = fixture.Provider.BeginGetter(fixture.OperationToken);
                var context = fixture.Provider.ReadFreshContext(fixture.OperationToken)!;
                var approval = fixture.Provider.CreateInstalledQueryApproval(context, fixture.OperationToken)!;
                int hosts = fixture.Platform.HostReads, usbReads = fixture.UsbReads;
                for (int i = 0; i < 164; i++) { Assert.True(approval.IsCurrent()); }
                Assert.Equal(hosts, fixture.Platform.HostReads);
                Assert.Equal(usbReads, fixture.UsbReads);
                Assert.True(continuous >= 164);
                if (finalDrift) { fixture.Platform.HostValid = false; }
                Assert.Equal(!finalDrift, fixture.Provider.CompleteGetter(context, fixture.OperationToken));
                Assert.True(fixture.Platform.HostReads > hosts);
                if (finalDrift)
                {
                    fixture.Platform.HostValid = true;
                    Assert.False(approval.IsCurrent());
                    break;
                }
                Assert.Equal((query + 1) * 2, full);
            }
        });
    }

    [Theory]
    [InlineData("controller")]
    [InlineData("driver")]
    [InlineData("usb")]
    public async Task GetterChecksMutableContinuityWithoutFullDiscovery(string drift)
    {
        using var fixture = new Fixture();
        fixture.GetterAdmission = (_, token, lifetime) => new(token, lifetime, _ => true, continuity: _ => true);
        await fixture.Run(_ =>
        {
            using var scope = fixture.Provider.BeginGetter(fixture.OperationToken);
            var context = fixture.Provider.ReadFreshContext(fixture.OperationToken)!;
            var approval = fixture.Provider.CreateInstalledQueryApproval(context, fixture.OperationToken)!;
            int hosts = fixture.Platform.HostReads;
            if (drift == "controller") { fixture.ControllerCurrent = false; }
            if (drift == "driver") { fixture.Platform.Running = false; }
            if (drift == "usb") { fixture.Usb = null; }
            Assert.False(approval.IsCurrent());
            Assert.Equal(hosts, fixture.Platform.HostReads);
            fixture.ControllerCurrent = true; fixture.Platform.Running = true;
            fixture.Usb = context.Identity;
            Assert.False(approval.IsCurrent());
        });
    }

    [Fact]
    public async Task IdentityDriftRevokesEntireGetterBeforeANewApprovalCanBeCreated()
    {
        using var fixture = new Fixture();
        fixture.GetterAdmission = (_, token, lifetime) => new(token, lifetime,
            proof => proof.FullPower(() => true, () => PowerSource.ExternalUnknown));
        await fixture.Run(_ =>
        {
            using var getter = fixture.Provider.BeginGetter(fixture.OperationToken);
            Assert.NotNull(getter);
            var context = fixture.Provider.ReadFreshContext(fixture.OperationToken)!;
            var approval = fixture.Provider.CreateInstalledQueryApproval(context, fixture.OperationToken)!;
            var usb = fixture.Usb; fixture.Usb = null;
            Assert.False(approval.IsCurrent());
            fixture.Usb = usb;
            Assert.Null(fixture.Provider.ReadFreshContext(fixture.OperationToken));
            Assert.Null(fixture.Provider.CreateInstalledQueryApproval(context, fixture.OperationToken));
        });
    }

    [Fact]
    public async Task GetterScopeSharesNestedAdmissionExpiresAndCannotIssueWriteProof()
    {
        using var fixture = new Fixture();
        int classifications = 0;
        fixture.GetterAdmission = (_, token, lifetime) => new(token, lifetime,
            proof => proof.FullPower(() => { classifications++; return true; }, () => PowerSource.ExternalUnknown));
        CpuQueryInstalledApproval? previous = null;
        await fixture.Run(_ =>
        {
            for (int query = 0; query < 2; query++)
            {
                using var getter = fixture.Provider.BeginGetter(fixture.OperationToken);
                Assert.NotNull(getter);
                var context = fixture.Provider.ReadFreshContext(fixture.OperationToken)!;
                var approval = fixture.Provider.CreateInstalledQueryApproval(context, fixture.OperationToken)!;
                for (int i = 0; i < 164; i++) { Assert.True(approval.IsCurrent()); }
                Assert.Equal(query + 1, classifications);
                Assert.False(previous?.IsCurrent() ?? false);
                var request = Request(-2);
                Assert.False(fixture.Provider.CheckWriteAdmission(context, request.Command, request.Purpose, default));
                Assert.Null(fixture.Provider.CreateWriteProof(context, request, default));
                previous = approval;
            }
            Assert.False(previous!.IsCurrent());
        });
        Assert.Equal(2, classifications); Assert.False(previous!.IsCurrent());
    }

    [Fact]
    public async Task ProviderAdmissionUsesTheNativeObservationOperationToken()
    {
        using var fixture = new Fixture();
        var tokens = new List<CancellationToken>();
        fixture.ObserveAdmission = (_, token) => { tokens.Add(token); return true; };
        fixture.BeforeProvider = token => { Assert.True(token.CanBeCanceled); tokens.Clear(); };
        await fixture.Run(_ =>
        {
            Assert.NotEmpty(tokens);
            Assert.All(tokens, token => Assert.Equal(fixture.OperationToken, token));
        });
    }

    [Fact]
    public async Task ProviderReportsFirstIdentityRejectionWithoutRetainingBackend()
    {
        using var fixture = new Fixture { Usb = null };
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        await fixture.RunRaw(context => Assert.Null(context), diagnostic);
        Assert.Equal("provider matching USB identity", diagnostic.FirstFailure);
        Assert.Equal("curve transaction ObservationUnavailable", diagnostic.LiveObservationReason);
        Assert.Equal(0, fixture.Platform.Opens); Assert.Equal(0, fixture.Platform.Pins);
    }
    [Fact]
    public async Task PowerObservationSharesRetainedDriverWithCurveAndCannotManufactureAttendedAuthority()
    {
        using var fixture = new Fixture();
        var power = (ICpuLivePowerTrustedNativeProvider)fixture.Provider;
        Assert.Null(power.ReadFreshContext(default));
        CpuPmInstalledApproval? captured = null;
        await fixture.Run(curve =>
        {
            var context = Assert.IsType<CpuLivePowerContext>(power.ReadFreshContext(default));
            Assert.Equal(curve.Identity, context.Identity); Assert.Equal(curve.Backend, context.Backend);
            Assert.Equal(curve.RuntimeLifetime, context.RuntimeLifetime);
            Assert.Null(power.CreateObservationApproval(context, default));
            Assert.Null(power.ReadFreshObservationLifetime(context, default));
            captured = power.CreateInstalledObservationApproval(context, default);
            Assert.NotNull(captured); Assert.True(captured.IsCurrent());
            Assert.NotNull(power.CreateObservationMetadata(context, default));
            Assert.Equal(0, fixture.MetadataOpens);
            // Physical PPT admission is still pending, including restoration.
            Assert.False(power.CheckWriteAdmission(context, default, CpuLivePowerCommandPurpose.Restore, null!, null!, default));
            Assert.Null(power.CreateWriteProof(context, null!, default));
        });
        Assert.False(captured!.IsCurrent());
        await fixture.Run(_ =>
        {
            var context = power.ReadFreshContext(default)!;
            Assert.False(captured.IsCurrent());
            Assert.True(power.CreateInstalledObservationApproval(context, default)!.IsCurrent());
        });
        Assert.Equal(1, fixture.Platform.Opens); Assert.Equal(1, fixture.Platform.Pins);
    }

    [Fact]
    public async Task CurvePeerProofAllowsUnownedObservationButCannotAdmitPendingPptRestoration()
    {
        using var fixture = new Fixture();
        CpuLiveCurveWriteProof? captured = null; CpuLiveCurveNativeRequest? capturedRequest = null;
        var expected = CpuLivePowerTuningTransactionTests.PairFixture(45000, 45000);
        await fixture.Run(context =>
        {
            var single = Request(-2);
            capturedRequest = new(single.Scope, single.Command, single.Purpose, single.Before, new(expected, null));
            captured = fixture.Provider.CreateWriteProof(context, capturedRequest, default);
            Assert.NotNull(captured);
            var cap = CpuLivePowerFusedCap.TryDecode(4, 7u << 28)!;
            Assert.True(captured.AdmitPowerPeer(capturedRequest, 6, cap));
            var owned = new CpuLiveCurveNativeRequest(single.Scope, single.Command, single.Purpose, single.Before,
                new(expected, CpuLivePowerTuningTransactionTests.PairFixture(100123, 80765)));
            var ownedProof = fixture.Provider.CreateWriteProof(context, owned, default)!;
            Assert.False(ownedProof.AdmitPowerPeer(owned, 6, cap));
            Assert.False(captured.AdmitPowerPeer(owned, 6, cap));
            Assert.False(captured.Consume(owned)); // Values/correlation never rebind the exact native proof.
        });
        Assert.False(captured!.AdmitPowerPeer(capturedRequest!, 6, CpuLivePowerFusedCap.TryDecode(4, 7u << 28)!));
    }

    [Fact]
    public async Task PowerObservationRejectsChangedRuntimeContextAndUsbReturnCannotRenewOldApproval()
    {
        using var fixture = new Fixture();
        var power = (ICpuLivePowerTrustedNativeProvider)fixture.Provider;
        await fixture.Run(_ =>
        {
            var context = power.ReadFreshContext(default)!;
            var changed = context with { RuntimeLifetime = context.RuntimeLifetime! with { PowerSourceGeneration = 99 } };
            Assert.Null(power.CreateInstalledObservationApproval(changed, default));
            Assert.Null(power.CreateObservationMetadata(changed, default));
            var approval = power.CreateInstalledObservationApproval(context, default)!;
            var usb = fixture.Usb; fixture.Usb = null;
            Assert.False(approval.IsCurrent());
            fixture.Usb = usb;
            Assert.False(approval.IsCurrent());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrMismatchedPresentUsbCannotAcquireInstalledDevice(bool mismatch)
    {
        using var fixture = new Fixture();
        fixture.Usb = mismatch ? new("RZ09-0581", 0x1532, 0x02E0, "Unknown") : null;
        await fixture.RunRaw(context => Assert.Null(context));
        Assert.Equal(0, fixture.Platform.Opens); Assert.Equal(0, fixture.MetadataOpens);
    }

    [Fact]
    public async Task UsbDriftIrreversiblyRevokesCapturedApprovalAndProof()
    {
        using var fixture = new Fixture();
        await fixture.Run(context =>
        {
            var approval = fixture.Provider.CreateInstalledQueryApproval(context, default)!;
            var request = Request(-2);
            var proof = fixture.Provider.CreateWriteProof(context, request, default)!;
            Assert.True(approval.IsCurrent()); Assert.True(proof.Current(request));
            var usb = fixture.Usb; fixture.Usb = null;
            Assert.False(approval.IsCurrent()); Assert.False(proof.Current(request));
            fixture.Usb = usb;
            Assert.False(approval.IsCurrent()); Assert.False(proof.Current(request));
            Assert.Null(proof.ReadFreshRange(request));
        });
    }

    [Fact]
    public async Task RetainedDeviceSurvivesRequestsButOldApprovalAndWriteProofExpire()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Provider.ReadFreshContext(default));
        Assert.Equal(0, fixture.Platform.Opens);
        CpuQueryInstalledApproval? approval = null; CpuLiveCurveWriteProof? proof = null;
        var request = Request(-2);
        await fixture.Run(context =>
        {
            approval = fixture.Provider.CreateInstalledQueryApproval(context, default);
            Assert.NotNull(approval); Assert.True(approval.IsCurrent());
            Assert.Null(fixture.Provider.CreateQueryApproval(context, default));
            proof = fixture.Provider.CreateWriteProof(context, request, default);
            Assert.NotNull(proof); Assert.True(proof.Current(request));
            Assert.NotNull(proof.ReadFreshRange(request));
            Assert.NotNull(proof.ReadFreshRange(request));
            Assert.Equal(2, fixture.MetadataOpens); // Preflight and this exact native request.
        });
        Assert.False(approval!.IsCurrent()); Assert.False(proof!.Current(request));
        Assert.Null(proof.ReadFreshRange(request));
        await fixture.Run(context =>
        {
            Assert.False(approval.IsCurrent()); Assert.False(proof.Current(request));
            Assert.True(fixture.Provider.CreateInstalledQueryApproval(context, default)!.IsCurrent());
            Assert.True(fixture.Provider.CheckWriteAdmission(context, request.Command, request.Purpose, default));
        });
        Assert.Equal(1, fixture.Platform.Opens); Assert.Equal(1, fixture.Platform.Pins);
        Assert.Equal(3, fixture.MetadataOpens);
    }

    [Fact]
    public async Task GetterRevocationCannotProduceWriteProofOrRenewOnLaterRequest()
    {
        using var fixture = new Fixture();
        fixture.OnMinMax = () => fixture.Admitted = false;
        await fixture.Run(context => Assert.Null(fixture.Provider.CreateWriteProof(context, Request(-2), default)));
        Assert.Equal(1, fixture.MetadataOpens);
        fixture.Admitted = true; fixture.OnMinMax = null;
        await fixture.Run(context => Assert.NotNull(fixture.Provider.CreateWriteProof(context, Request(-2), default)));
        Assert.Equal(2, fixture.MetadataOpens);
        Assert.Equal(1, fixture.Platform.Opens);
    }

    [Fact]
    public async Task NarrowRangeRequiresExactImmediateReverseBeforeWriteProof()
    {
        using var fixture = new Fixture { Minimum = 2, Maximum = 5 };
        await fixture.Run(context =>
        {
            Assert.True(fixture.Provider.CheckWriteAdmission(context, Request(-2).Command, CpuLiveCurveCommandPurpose.Forward, default));
            // Original -1 falls outside this fresh range. No inverse guess.
            Assert.Null(fixture.Provider.CreateWriteProof(context, Request(-2), default));
        });
    }

    [Fact]
    public async Task InstalledConfigurationDriftRejectsFurtherContextAndApproval()
    {
        using var fixture = new Fixture();
        await fixture.Run(context =>
        {
            var approval = fixture.Provider.CreateInstalledQueryApproval(context, default);
            fixture.Platform.Running = false;
            Assert.False(approval!.IsCurrent());
            Assert.Null(fixture.Provider.ReadFreshContext(default));
        });
        Assert.Equal(1, fixture.Platform.Opens);
    }

    private static CpuLiveCurveNativeRequest Request(short offset)
    {
        Assert.True(CpuLiveCurveCommand.TryCreate(0, offset, 0xFF55, out var command));
        var before = CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50,
            CpuLiveCurveTuningTransactionTests.ValuesFixture()))!;
        return new(new(), command, CpuLiveCurveCommandPurpose.Forward, before);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Platform Platform = new();
        internal readonly WindowsCpuLiveTuningTrustedNativeProvider Provider;
        private readonly ProbeBackend backend = new();
        private readonly CpuTuningController controller;
        internal int MetadataOpens, UsbReads;
        internal bool ControllerCurrent = true;
        internal uint Minimum, Maximum = 50;
        internal bool Admitted = true;
        internal DeviceIdentity? Usb = new("RZ09-0528", 0x1532, 0x02C6, "Unknown");
        internal Action? OnMinMax;
        internal Func<CpuTuningFeatures, CancellationToken, bool>? ObserveAdmission;
        internal Action<CancellationToken>? BeforeProvider;
        internal Func<CpuTuningFeatures, CancellationToken, Func<bool>, CpuLiveGetterAdmission>? GetterAdmission;
        internal CancellationToken OperationToken;
        internal Fixture()
        {
            CpuTuningController? captured = null;
            Provider = new(WindowsRz090528CpuNativeSession.ForPlatform(Platform), () => captured, new Source(), () => { UsbReads++; return Usb; },
                _ => new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, 42),
                new WindowsCpuLiveCurveRangeMetadata(_ => { MetadataOpens++; return new Metadata(this); }),
                () => new CpuGetterUsbContinuity(() => new(Usb is null ? null : @"USB\VID_1532&PID_02C6\test", 8, 0),
                    @"USB\VID_1532&PID_02C6\test", () => { }), () => () => ControllerCurrent);
            controller = captured = new(new Source(), curveBackend: backend,
                curveStore: new CpuLiveCurveTuningTransactionTests.MemoryStore());
        }
        internal async Task Run(Action<CpuLiveCurveContext> check)
            => await RunRaw(context => check(Assert.IsType<CpuLiveCurveContext>(context)));

        internal async Task RunRaw(Action<CpuLiveCurveContext?> check, CpuSavedAdmissionDiagnostic? diagnostic = null)
        {
            backend.Check = token =>
            {
                OperationToken = token;
                BeforeProvider?.Invoke(token);
                var context = Provider.ReadFreshContext(token);
                check(context);
                return new(context, new(0xFF55, 0x005D000B, 0xD50, CpuLiveCurveTuningTransactionTests.ValuesFixture()));
            };
            await controller.ObserveLiveAsync((feature, token) => ObserveAdmission?.Invoke(feature, token) ?? Admitted, default, 2, 3, diagnostic, GetterAdmission);
        }
        public void Dispose() => Provider.Dispose();
    }

    private sealed class ProbeBackend : ICpuLiveCurveBackend
    {
        internal Func<CancellationToken, CpuLiveCurveBackendObservation> Check = _ => throw new InvalidOperationException();
        public Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token) => Task.FromResult(Check(token));
        public Task<bool> CheckWriteAdmissionAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CancellationToken token) => throw new InvalidOperationException();
        public Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class Source : ICpuTuningObservationSource
    {
        public DeviceIdentity? ReadIdentity() => new("RZ09-0528", 0x1532, 0x02C6, "Unknown");
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity) => throw new InvalidOperationException();
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend) => throw new InvalidOperationException();
    }

    internal sealed class Platform : ICpuQueryWindowsPlatform, ICpuQueryRetainableDevice, ICpuQueryMutex
    {
        internal int Opens, Pins, HostReads;
        internal bool Running = true, HostValid = true;
        public HostIdentityReadResult ReadHost()
        { HostReads++; return new(new(HostValid ? "Razer" : "Other", "Blade 16 - RZ09-0528", "RZ09-05289EN4", "Unknown"), null); }
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x1A, 0x24, 0);
        public bool ControllersAbsent() => true;
        public IDisposable PinBackendFiles() { Pins++; return new Cleanup(); }
        public InstalledCpuBackendSnapshot ReadBackend() => new(Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair =>
            new CpuBackendFileMetadata(pair.Key, pair.Value, true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray(),
            true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, Running);
        public ICpuQueryDevice OpenExistingDevice() { Opens++; return this; }
        public ICpuQueryDevice Duplicate() => throw new InvalidOperationException("Provider tests must not invoke the device.");
        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength) => throw new InvalidOperationException();
        public ICpuQueryMutex CreateSharedMutex() => this;
        public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken cancellationToken) => CpuQueryMutexWait.Acquired;
        public void Release() { }
        public void Dispose() { }
        private sealed class Cleanup : IDisposable { public void Dispose() { } }
    }

    private sealed class Metadata(Fixture fixture) : IWindowsCpuCurveRangeSession
    {
        public IWindowsCpuCurveRangeObject? QueryVersion(Action checkpoint) => new Output(new() { ["version"] = (uint)7 });
        public IWindowsCpuCurveRangeObject? GetCurveMinMax(Action checkpoint)
        {
            fixture.OnMinMax?.Invoke();
            return new Output(new()
            {
                ["pack"] = new Output(new()
                {
                    ["Current"] = (uint)0,
                    ["Min"] = fixture.Minimum,
                    ["Max"] = fixture.Maximum,
                    ["Stepping"] = (uint)1,
                    ["Value"] = (uint)0x5000A,
                })
            });
        }
        public void Dispose() { }
    }
    private sealed class Output(Dictionary<string, object?> values) : IWindowsCpuCurveRangeObject
    {
        public IEnumerable<string> Names => values.Keys;
        public object? Get(string name) => values[name];
        public void Dispose() { }
    }
}
