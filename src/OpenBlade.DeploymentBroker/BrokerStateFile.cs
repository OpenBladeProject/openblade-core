namespace OpenBlade.DeploymentBroker;

internal static class BrokerStateFile
{
    public static async Task<byte[]> ReadAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long length = stream.Length;
        if (length < 0 || length > maximumBytes)
        {
            throw new InvalidDataException("The broker state file exceeds its fixed size bound.");
        }

        byte[] bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        // Do not trust a length observed before the read if the source changes.
        byte[] probe = new byte[1];
        if (await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("The broker state file changed size while being read.");
        }

        return bytes;
    }
}
