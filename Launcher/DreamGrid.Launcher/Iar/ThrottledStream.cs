namespace DreamGrid.Launcher.Iar;

/// <summary>
/// Reads <c>inner</c> no faster than <c>bytesPerSecond</c> on average (spec 2026-11-11-iar-backups-design §5.2: a big download
/// doesn't use up the grid's upload). Seekable when <c>inner</c> is, so range requests keep working; the rate counts from the first
/// read after each seek.
/// </summary>
public sealed class ThrottledStream(Stream inner, int bytesPerSecond, TimeProvider time) : Stream
{
    private long _sent;
    private long _since = -1;

    /// <summary>How long to wait before more may be sent, when <paramref name="sent"/> bytes went in <paramref name="elapsed"/>.</summary>
    public static TimeSpan WaitFor(long sent, TimeSpan elapsed, int bytesPerSecond)
    {
        var due = TimeSpan.FromSeconds((double)sent / bytesPerSecond);
        return due > elapsed ? due - elapsed : TimeSpan.Zero;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_since < 0) _since = time.GetTimestamp();
        var wait = WaitFor(_sent, time.GetElapsedTime(_since), bytesPerSecond);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, time, cancellationToken);
        // At most a quarter second's worth at a time, so the pace stays even.
        var n = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, Math.Max(1, bytesPerSecond / 4))], cancellationToken);
        _sent += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set { inner.Position = value; Restart(); }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var p = inner.Seek(offset, origin);
        Restart();
        return p;
    }

    private void Restart()
    {
        _sent = 0;
        _since = -1;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
