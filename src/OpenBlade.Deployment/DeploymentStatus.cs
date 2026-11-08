using System.Text.Json;

namespace OpenBlade.Deployment;

public sealed record DeploymentStatusFile(string Name, string State, bool Truncated, string? Base64);
public sealed record DeploymentStatusSnapshot(bool InstallEnabled, string Selection, Guid? DeploymentRequestId,
    IReadOnlyList<DeploymentStatusFile> Files);
public sealed record DeploymentStatusResponse(int ProtocolVersion, Guid RequestId, string Status, string Code,
    DeploymentStatusSnapshot Diagnostics);

// This larger envelope is exclusive to ReadDeploymentStatus. Write replies keep their old bound/codec.
public static class DeploymentStatusCodec
{
    public const int MaximumBytes = 512 * 1024;
    public const int MaximumFileBytes = 32 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static byte[] Serialize(DeploymentStatusResponse response)
    {
        Validate(response);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, Json);
        if (bytes.Length > MaximumBytes) { throw new InvalidDataException("Deployment status exceeds its bound."); }
        return bytes;
    }

    public static DeploymentStatusResponse Parse(ReadOnlySpan<byte> bytes, Guid requestId)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumBytes) { throw new InvalidDataException("Invalid deployment status size."); }
        try
        {
            var response = JsonSerializer.Deserialize<DeploymentStatusResponse>(bytes, Json)
                ?? throw new InvalidDataException("Missing deployment status.");
            Validate(response);
            if (response.RequestId != requestId || !bytes.SequenceEqual(Serialize(response)))
            { throw new InvalidDataException("Deployment status identity or encoding differs."); }
            return response;
        }
        catch (JsonException exception) { throw new InvalidDataException("Malformed deployment status.", exception); }
    }

    private static void Validate(DeploymentStatusResponse response)
    {
        if (response.ProtocolVersion != 1 || response.RequestId == Guid.Empty || response.Status != "Succeeded"
            || response.Code != "deployment-status" || response.Diagnostics is not { } snapshot
            || snapshot.Selection is not ("Current" or "None" or "Unavailable")
            || (snapshot.Selection == "Current") != (snapshot.DeploymentRequestId is { } id && id != Guid.Empty)
            || snapshot.Files is null || snapshot.Files.Count > 7)
        { throw new InvalidDataException("Invalid deployment status contract."); }
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (DeploymentStatusFile file in snapshot.Files)
        {
            if (file is null || file.Name is not ("inflight" or "report" or "journal" or "cpuLaunch" or "installerLog" or "setupLog1" or "setupLog2")
                || !names.Add(file.Name) || file.State is not ("Available" or "Missing" or "Unavailable")
                || (file.State == "Available") != (file.Base64 is not null)
                || file.State != "Available" && file.Truncated)
            { throw new InvalidDataException("Invalid deployment diagnostic file."); }
            if (file.Base64 is not null)
            {
                try
                {
                    if (file.Base64.Length > 4 * ((MaximumFileBytes + 2) / 3)
                        || Convert.FromBase64String(file.Base64).Length > MaximumFileBytes)
                    { throw new InvalidDataException("Deployment diagnostic file exceeds its bound."); }
                }
                catch (FormatException exception) { throw new InvalidDataException("Invalid diagnostic encoding.", exception); }
            }
        }
    }
}
