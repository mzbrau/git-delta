using CliWrap;

namespace GitDelta.Git.Internal;

/// <summary>
/// Wraps a <see cref="PipeTarget"/> to tally bytes as they stream through, without buffering
/// them a second time. Used to feed <c>GitDeltaMeters.GitBytesRead</c>.
/// </summary>
internal sealed class ByteCountingPipeTarget(PipeTarget inner, Action<long> onBytesRead) : PipeTarget
{
    public override async Task CopyFromAsync(Stream origin, CancellationToken cancellationToken = default)
    {
        var counting = new CountingReadStream(origin, onBytesRead);
        await inner.CopyFromAsync(counting, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Read-only passthrough stream that reports bytes as they are consumed by the inner target.</summary>
    private sealed class CountingReadStream(Stream source, Action<long> onBytesRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read > 0)
                onBytesRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
                onBytesRead(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
