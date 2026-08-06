using System.Text;

namespace GitDelta.Git.Internal;

/// <summary>
/// Minimal helpers for the `git cat-file --batch` line/byte protocol. Reads exactly the bytes
/// the protocol promises so a single duplex stream can safely interleave text headers with raw
/// binary payloads.
/// </summary>
internal static class GitBatchProtocol
{
    public static async Task WriteRequestLineAsync(Stream stream, string oid, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(oid + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one line terminated by '\n', one byte at a time. This must not use a buffered
    /// reader: any bytes read past the newline would be lost from the subsequent raw byte read
    /// of the object payload, which follows immediately in the same stream.
    /// </summary>
    public static async Task<string?> ReadHeaderLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        List<byte>? bytes = null;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0)
                return bytes is null ? null : Encoding.ASCII.GetString(bytes.ToArray());

            if (buffer[0] == (byte)'\n')
                return bytes is null ? string.Empty : Encoding.ASCII.GetString(bytes.ToArray());

            bytes ??= [];
            bytes.Add(buffer[0]);
        }
    }

    public static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("git cat-file --batch stream ended before the expected object payload was fully read.");

            totalRead += read;
        }
    }
}
