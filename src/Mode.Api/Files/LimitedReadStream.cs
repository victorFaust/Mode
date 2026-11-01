namespace Mode.Api.Files;

/// <summary>Throws <see cref="InvalidDataException"/> once more than <paramref name="limit"/> bytes are read.</summary>
internal sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    private long _total;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _total; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    private int Count(int read)
    {
        _total += read;
        if (_total > limit) throw new InvalidDataException($"More than {limit} bytes were sent.");
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
