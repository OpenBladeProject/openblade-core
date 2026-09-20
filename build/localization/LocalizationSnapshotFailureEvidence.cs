using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OpenBlade.Localization;

internal static class LocalizationSnapshotFailureEvidence
{
    internal const string DirectoryEnvironmentVariable =
        "OPENBLADE_LOCALIZATION_FAILURE_EVIDENCE_DIR";
    internal const int MaximumFrameBytes = 32 * 1024 * 1024;

    internal static bool TryWrite(
        string? evidenceRoot,
        string surface,
        string stage,
        int previousWidth,
        int previousHeight,
        byte[] previousPixels,
        int candidateWidth,
        int candidateHeight,
        byte[] candidatePixels)
    {
        if (string.IsNullOrWhiteSpace(evidenceRoot))
        {
            return false;
        }

        try
        {
            Write(
                evidenceRoot!,
                surface,
                stage,
                previousWidth,
                previousHeight,
                previousPixels,
                candidateWidth,
                candidateHeight,
                candidatePixels);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }
    }

    private static void Write(
        string evidenceRoot,
        string surface,
        string stage,
        int previousWidth,
        int previousHeight,
        byte[] previousPixels,
        int candidateWidth,
        int candidateHeight,
        byte[] candidatePixels)
    {
        ValidateIdentifier(surface, nameof(surface));
        ValidateIdentifier(stage, nameof(stage));
        ValidateFrame(previousWidth, previousHeight, previousPixels, nameof(previousPixels));
        ValidateFrame(candidateWidth, candidateHeight, candidatePixels, nameof(candidatePixels));

        string root = Path.GetFullPath(evidenceRoot);
        string stageDirectory = Path.GetFullPath(Path.Combine(root, surface + "-" + stage));
        string directory = Path.GetFullPath(Path.Combine(
            stageDirectory,
            Guid.NewGuid().ToString("N")));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string stagePrefix = stageDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!stageDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !directory.StartsWith(stagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Snapshot evidence path escaped its configured root.");
        }

        Directory.CreateDirectory(directory);
        string previousPath = Path.Combine(directory, "previous.bgra");
        string candidatePath = Path.Combine(directory, "candidate.bgra");
        File.WriteAllBytes(previousPath, previousPixels);
        File.WriteAllBytes(candidatePath, candidatePixels);
        string metadataPath = Path.Combine(directory, "metadata.json");
        string pendingMetadataPath = metadataPath + ".tmp";
        File.WriteAllText(
            pendingMetadataPath,
            CreateMetadata(
                surface,
                stage,
                previousWidth,
                previousHeight,
                previousPixels,
                candidateWidth,
                candidateHeight,
                candidatePixels),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(pendingMetadataPath, metadataPath);
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            throw new ArgumentException(
                "Snapshot evidence identifiers must contain 1 through 64 characters.",
                parameterName);
        }

        foreach (char character in value)
        {
            if ((character < 'a' || character > 'z')
                && (character < '0' || character > '9')
                && character != '-')
            {
                throw new ArgumentException(
                    "Snapshot evidence identifiers may contain only lowercase ASCII, digits, and hyphens.",
                    parameterName);
            }
        }
    }

    private static void ValidateFrame(
        int width,
        int height,
        byte[] pixels,
        string parameterName)
    {
        if (pixels is null)
        {
            throw new ArgumentNullException(parameterName);
        }
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Snapshot evidence dimensions must be positive.");
        }

        long expectedBytes = checked((long)width * height * 4);
        if (expectedBytes > MaximumFrameBytes || pixels.LongLength != expectedBytes)
        {
            throw new ArgumentException(
                $"Snapshot evidence must contain exactly {expectedBytes.ToString(CultureInfo.InvariantCulture)} "
                    + $"BGRA bytes and no more than {MaximumFrameBytes.ToString(CultureInfo.InvariantCulture)} bytes.",
                parameterName);
        }
    }

    private static string CreateMetadata(
        string surface,
        string stage,
        int previousWidth,
        int previousHeight,
        byte[] previousPixels,
        int candidateWidth,
        int candidateHeight,
        byte[] candidatePixels)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{{\n  \"schemaVersion\": 1,\n  \"pixelFormat\": \"bgra8-premultiplied-canonical\",\n"
                + "  \"surface\": \"{0}\",\n  \"stage\": \"{1}\",\n"
                + "  \"previous\": {{ \"file\": \"previous.bgra\", \"width\": {2}, \"height\": {3}, \"bytes\": {4}, \"sha256\": \"{5}\" }},\n"
                + "  \"candidate\": {{ \"file\": \"candidate.bgra\", \"width\": {6}, \"height\": {7}, \"bytes\": {8}, \"sha256\": \"{9}\" }}\n}}\n",
            surface,
            stage,
            previousWidth,
            previousHeight,
            previousPixels.Length,
            GetSha256(previousPixels),
            candidateWidth,
            candidateHeight,
            candidatePixels.Length,
            GetSha256(candidatePixels));
    }

    private static string GetSha256(byte[] bytes)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(bytes);
        var result = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash)
        {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;
}
