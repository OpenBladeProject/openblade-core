using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBlade.Contracts.Tests;

public sealed class NativeThermalMutationResponseTests
{
    [NativeThermalEdgeFact]
    public async Task ActualNativeMutationResponsesUseManagedV91CodecAndOutcomes()
    {
        // The evidence runner extracts actual emitted Rust test replies into a
        // held, hashed private input. There is deliberately no product fallback.
        string path = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_THERMAL_RESPONSES")
            ?? throw new InvalidOperationException("The prelisted native response input is required.");
        Assert.True(Path.IsPathFullyQualified(path));
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var seen = new HashSet<ControlOperationStatus>();
        bool sawConfirmedPreset = false;
        bool sawPartialCustom = false;
        bool sawManualDeferral = false, sawGpuIncomplete = false, sawCapacityRefusal = false, sawPowerChanged = false, sawCleared = false;
        bool sawUnsupported = false;
        var decodeErrors = new List<string>();
        int index = 0;
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            int frameIndex = index++;
            byte[] bytes = Encoding.UTF8.GetBytes(element.GetRawText());
            byte[] framed = new byte[bytes.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(framed, bytes.Length);
            bytes.CopyTo(framed, 4);
            using var stream = new MemoryStream(framed, writable: false);
            ServiceResponse response;
            try
            {
                response = Assert.IsType<ServiceResponse>(await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
            }
            catch (Exception exception)
            {
                decodeErrors.Add($"Frame {frameIndex}: {exception.Message} {exception.InnerException?.Message}");
                continue;
            }
            Assert.Equal(OpenBladeIpc.ProtocolVersion, response.Version);
            Assert.NotEqual(Guid.Empty, response.RequestId);
            if (!response.Success)
            {
                Assert.False(string.IsNullOrWhiteSpace(response.Error));
                Assert.Null(response.Status);
                Assert.Null(response.Result);
                Assert.Null(response.Archive);
                // ControlRequestHandler's fallback for an operation it does not dispatch.
                sawUnsupported |= response.Error == "IPC request operation is unsupported."
                    && response.ErrorCode is null;
                continue;
            }
            Assert.Null(response.Error);
            if (response.Status is { } status)
            {
                Assert.Null(response.Result);
                // Native publishes no capability set. Only a confirmed HyperBoost
                // state carries the observation record the codec requires there.
                Assert.Equal(status.ConfirmedState?.HyperBoostActive == true, status.Capabilities is not null);
                Assert.Null(response.Archive);
                if (status.ConfirmedState is { Performance: PerformancePreset.Custom, CustomPerformance: null } partial)
                {
                    Assert.Equal(FanMode.Maximum, partial.Fans);
                    sawPartialCustom = true;
                    // Derive both negative inputs from the actual native frame.
                    // No inferred CPU/GPU settings are added to its positive case.
                    JsonNode desired = JsonNode.Parse(element.GetRawText())!;
                    desired["status"]!["desiredState"]!["performance"]!["value"] = (int)PerformancePreset.Custom;
                    desired["status"]!["desiredState"]!["customPerformance"] = null;
                    using var desiredFrame = FrameJson(desired.ToJsonString());
                    await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadServerFrameAsync(desiredFrame, CancellationToken.None));
                    JsonNode malformed = JsonNode.Parse(element.GetRawText())!;
                    malformed["status"]!["confirmedState"]!["customPerformance"] = new JsonObject { ["cpu"] = 99, ["gpu"] = 2 };
                    using var malformedFrame = FrameJson(malformed.ToJsonString());
                    await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadServerFrameAsync(malformedFrame, CancellationToken.None));
                }
                if (status.ConfirmedState?.Performance is PerformancePreset.Balanced or PerformancePreset.Performance)
                {
                    Assert.Null(status.Capabilities);
                    // A later metadata projection keeps confirmed presentation,
                    // but cannot renew the mutation's independent read authority.
                    Assert.True(status.WritesBlocked);
                    sawConfirmedPreset = true;
                }
                continue;
            }
            var result = Assert.IsType<ControlOperationResult>(response.Result);
            Assert.Null(response.Status);
            Assert.Null(response.Archive);
            seen.Add(result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            if (result.Status == ControlOperationStatus.Applied)
            {
                Assert.Contains("GPU", result.Message, StringComparison.Ordinal);
            }
            // Managed's only Stored-without-apply performance reply is its manual-fan
            // deferral; any other saved but unapplied override is Blocked with the
            // committed-intent prefix of ExecuteScopedMutationAsync.
            if (result.Status == ControlOperationStatus.Stored)
            {
                // ServiceControlRuntime.DeferredPerformanceIntentMessage.
                Assert.Equal("Performance intent was stored and will be applied when fans return to Automatic.", result.Message);
                sawManualDeferral = true;
            }
            const string committed = "Performance override intent was stored. Hardware result: ";
            if (result.Status == ControlOperationStatus.Blocked && result.Message.StartsWith(committed, StringComparison.Ordinal))
            {
                string cause = result.Message[committed.Length..];
                sawGpuIncomplete |= cause.StartsWith("GPU clock handling is incomplete", StringComparison.Ordinal);
                sawCapacityRefusal |= cause.Contains("capacity refused before publication", StringComparison.Ordinal);
                sawPowerChanged |= cause.StartsWith("The independently observed power source changed", StringComparison.Ordinal);
            }
            sawCleared |= result.Status == ControlOperationStatus.Blocked
                && result.Message.StartsWith("Performance override was cleared. Hardware result: ", StringComparison.Ordinal);
            if (result.Status != ControlOperationStatus.Blocked)
            {
                Assert.DoesNotContain("Hardware result:", result.Message, StringComparison.Ordinal);
            }
        }
        // Native, like managed, applies the deferred intent through the fan request
        // that returns the fans to Automatic; the Rust handback tests cover that pair.
        Assert.True(sawManualDeferral, "External-power manual-fan deferral is the native Stored reply, matching managed's reply.");
        Assert.True(sawGpuIncomplete && sawCapacityRefusal && sawPowerChanged,
            "Saved but unapplied GPU, capacity and power-change replies are Blocked with the managed prefix.");
        Assert.True(sawCleared, "An unapplied clear is Blocked with managed's cleared prefix.");
        Assert.True(sawUnsupported, "An unserved managed operation gets managed's unsupported-operation failure.");
        Assert.True(decodeErrors.Count == 0, string.Join(Environment.NewLine, decodeErrors));
        Assert.True(sawPartialCustom, "Actual CustomManual observation must retain known selectors and unknown settings.");
        using var requestedFrame = FrameJson("""{"operation":"setPerformanceOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000001","value":{"value":5,"lifetime":1,"customPerformance":null,"gpuClockTuning":null}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadRequestAsync(requestedFrame, CancellationToken.None));
        Assert.True(sawConfirmedPreset, "Actual status retains confirmed hardware separately from the result-only reply without renewing write authority.");
        Assert.Contains(ControlOperationStatus.Stored, seen);
        Assert.Contains(ControlOperationStatus.Applied, seen);
        Assert.Contains(ControlOperationStatus.Blocked, seen);
    }

    [NativeThermalEdgeFact]
    public async Task ActualNativeBatteryResponsesKeepConfirmedPolicyDesiredLimitAndUnknownTemporarySeparate()
    {
        string path = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_BATTERY_RESPONSES") ?? throw new InvalidOperationException("Explicit actual native battery responses required.");
        Assert.True(Path.IsPathFullyQualified(path)); using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        bool off = false, active = false, unknown = false, storedLimit = false; var outcomes = new HashSet<ControlOperationStatus>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            using var frame = FrameJson(element.GetRawText()); var response = Assert.IsType<ServiceResponse>(await IpcMessageCodec.ReadServerFrameAsync(frame, CancellationToken.None));
            Assert.Equal(OpenBladeIpc.ProtocolVersion, response.Version); Assert.True(response.Success);
            if (response.Result is { } result) { outcomes.Add(result.Status); Assert.Null(response.Status); }
            if (response.Status is { } status)
            {
                if (status.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent == 65) { storedLimit = true; }
                if (status.ConfirmedState?.Battery is { } battery) { if (battery.ProtectionEnabled) { active = true; Assert.NotNull(battery.ChargeLimitPercent); } else { off = true; Assert.Null(battery.ChargeLimitPercent); } if (battery.TemporaryFullCharge is null) { unknown = true; } }
            }
        }
        Assert.True(off); Assert.True(active); Assert.True(unknown); Assert.True(storedLimit);
        Assert.Contains(ControlOperationStatus.Applied, outcomes); Assert.Contains(ControlOperationStatus.Blocked, outcomes);
    }

    [NativeThermalEdgeFact]
    public async Task ActualNativeBatteryPublicationsKeepUnavailableGroupsAndUnknownTemporaryCompatible()
    {
        string path = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_BATTERY_PUBLICATIONS")
            ?? throw new InvalidOperationException("Explicit actual native publication frames required.");
        Assert.True(Path.IsPathFullyQualified(path));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        bool unavailable = false, unknownTemporary = false, eventSeen = false;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            using var frame = FrameJson(element.GetRawText());
            var decoded = await IpcMessageCodec.ReadServerFrameAsync(frame, CancellationToken.None);
            ServiceStatusSnapshot? status;
            if (decoded is ServiceStatusEvent update)
            {
                Assert.Equal(OpenBladeIpc.ProtocolVersion, update.Version);
                eventSeen = true;
                status = update.Status;
            }
            else
            {
                var response = Assert.IsType<ServiceResponse>(decoded);
                Assert.Equal(OpenBladeIpc.ProtocolVersion, response.Version);
                Assert.True(response.Success);
                status = response.Status;
            }
            if (status is null)
            {
                continue;
            }
            Assert.True(status.WritesBlocked);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE_SENTINEL", status.Message, StringComparison.Ordinal);
            unavailable |= status.ConfirmedState?.Battery is null;
            if (status.ConfirmedState?.Battery is { } battery)
            {
                Assert.False(!battery.ProtectionEnabled && battery.TemporaryFullCharge is true);
                unknownTemporary |= battery.TemporaryFullCharge is null;
            }
        }
        Assert.True(unavailable);
        Assert.True(unknownTemporary);
        Assert.True(eventSeen);
    }

    [NativeThermalEdgeFact]
    public async Task ActualNativeTemporaryChargeCorrectionFramesKeepStrictManagedCodec()
    {
        string path = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_TFC_CORRECTION_FRAMES")
            ?? throw new InvalidOperationException("Explicit actual correction frames required.");
        Assert.True(Path.IsPathFullyQualified(path));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Assert.NotEmpty(document.RootElement.EnumerateArray());
        foreach (var element in document.RootElement.EnumerateArray())
        {
            using var frame = FrameJson(element.GetRawText());
            var decoded = await IpcMessageCodec.ReadServerFrameAsync(frame, CancellationToken.None);
            ServiceStatusSnapshot? status;
            if (decoded is ServiceStatusEvent update)
            {
                Assert.Equal(OpenBladeIpc.ProtocolVersion, update.Version);
                status = update.Status;
            }
            else
            {
                var response = Assert.IsType<ServiceResponse>(decoded);
                Assert.Equal(OpenBladeIpc.ProtocolVersion, response.Version);
                if (!response.Success)
                {
                    Assert.NotNull(response.Error);
                }
                status = response.Status;
            }
            if (status?.ConfirmedState?.Battery is { } battery)
            {
                Assert.False(!battery.ProtectionEnabled && battery.TemporaryFullCharge is true);
            }
        }
    }

    private static MemoryStream FrameJson(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        byte[] framed = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(framed, bytes.Length);
        bytes.CopyTo(framed, 4);
        return new MemoryStream(framed, writable: false);
    }
}
