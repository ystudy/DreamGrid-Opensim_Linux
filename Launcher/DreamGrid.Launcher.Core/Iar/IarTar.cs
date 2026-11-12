using System.IO.Compression;
using System.Text;

namespace DreamGrid.Launcher.Core.Iar;

/// <summary>One entry's header in an archive: its path, its type flag ('0' file, '5' folder) and its size.</summary>
internal sealed record IarTarEntry(string Name, char Type, long Size)
{
    public bool IsDirectory => Type == '5' || Name.EndsWith('/');
}

/// <summary>
/// Reads a gzip tar entry by entry, the way OpenSim's TarArchiveReader does: GNU long names ('L'), and the ustar prefix when a
/// header has one. Names are read as Latin-1 so their bytes are kept exactly when they are written back. Every byte taken from the
/// gzip stream counts toward <c>maxUnpacked</c>: past it, <see cref="IarTooBigException"/> is thrown before more is read.
/// </summary>
internal sealed class IarTarReader : IDisposable
{
    private readonly Stream _file;
    private readonly GZipStream _gz;
    private readonly long _maxUnpacked;
    private long _pendingData;
    private long _pendingPad;

    public IarTarReader(string path, long maxUnpacked = long.MaxValue)
    {
        _file = File.OpenRead(path);
        _gz = new GZipStream(_file, CompressionMode.Decompress);
        _maxUnpacked = maxUnpacked;
    }

    /// <summary>The bytes unpacked so far.</summary>
    public long Read { get; private set; }

    public void Dispose()
    {
        _gz.Dispose();
        _file.Dispose();
    }

    /// <summary>The next entry, or null at the end. Data of the previous entry that wasn't read is skipped.</summary>
    public IarTarEntry? Next()
    {
        Skip();
        var header = new byte[512];
        if (!Fill(header)) return null;
        if (header.All(b => b == 0)) return null;

        string? longName = null;
        while (header[156] is (byte)'L' or (byte)'x' or (byte)'g')
        {
            var size = Octal(header, 124, 12);
            var data = new byte[size];
            if (!Fill(data)) throw new InvalidDataException("The archive ends inside a header.");
            SkipBytes(Pad(size));
            if (header[156] == (byte)'L') longName = Encoding.Latin1.GetString(data).TrimEnd('\0');
            if (!Fill(header)) throw new InvalidDataException("The archive ends after a long name.");
        }

        var name = longName ?? Text(header, 0, 100);
        if (longName is null && Encoding.ASCII.GetString(header, 257, 5) == "ustar" && header[263] == (byte)'0')
        {
            var prefix = Text(header, 345, 155);
            if (prefix.Length > 0) name = prefix + "/" + name;
        }
        var entrySize = Octal(header, 124, 12);
        _pendingData = entrySize;
        _pendingPad = Pad(entrySize);
        var type = header[156] == 0 ? '0' : (char)header[156];
        return new IarTarEntry(name, type, entrySize);
    }

    /// <summary>The current entry's data, all of it.</summary>
    public byte[] ReadData()
    {
        if (_pendingData > int.MaxValue) throw new InvalidDataException("An entry is too big to read at once.");
        var data = new byte[_pendingData];
        if (!Fill(data)) throw new InvalidDataException("The archive ends inside an entry.");
        _pendingData = 0;
        return data;
    }

    /// <summary>Copies the current entry's data into <paramref name="target"/>.</summary>
    public void CopyData(Stream target)
    {
        var buffer = new byte[81920];
        while (_pendingData > 0)
        {
            var n = (int)Math.Min(buffer.Length, _pendingData);
            if (!Fill(buffer.AsSpan(0, n))) throw new InvalidDataException("The archive ends inside an entry.");
            target.Write(buffer, 0, n);
            _pendingData -= n;
        }
    }

    private void Skip()
    {
        SkipBytes(_pendingData + _pendingPad);
        _pendingData = 0;
        _pendingPad = 0;
    }

    private void SkipBytes(long count)
    {
        var buffer = new byte[Math.Min(81920, Math.Max(count, 1))];
        while (count > 0)
        {
            var n = (int)Math.Min(buffer.Length, count);
            if (!Fill(buffer.AsSpan(0, n))) throw new InvalidDataException("The archive ends inside an entry.");
            count -= n;
        }
    }

    private bool Fill(Span<byte> target)
    {
        var done = 0;
        while (done < target.Length)
        {
            if (Read >= _maxUnpacked) throw new IarTooBigException("unpacked", Read);
            var want = (int)Math.Min(target.Length - done, _maxUnpacked - Read);
            var n = _gz.Read(target[done..(done + want)]);
            if (n == 0) return done == 0 && target.Length > 0 ? false : throw new InvalidDataException("The archive ends too soon.");
            done += n;
            Read += n;
        }
        return true;
    }

    private static long Pad(long size) => size % 512 == 0 ? 0 : 512 - size % 512;

    private static string Text(byte[] header, int at, int length)
    {
        var end = Array.IndexOf(header, (byte)0, at, length);
        return Encoding.Latin1.GetString(header, at, (end < 0 ? at + length : end) - at);
    }

    private static long Octal(byte[] header, int at, int length)
    {
        long value = 0;
        for (var i = at; i < at + length; i++)
        {
            var b = header[i];
            if (b is 0 or (byte)' ') { if (value > 0) break; continue; }
            if (b is < (byte)'0' or > (byte)'7') throw new InvalidDataException("A size in the archive isn't a number.");
            value = value * 8 + (b - '0');
        }
        return value;
    }
}

/// <summary>
/// Writes a gzip tar exactly as OpenSim's TarArchiveWriter does (its header fields, GNU long names for paths over 100 bytes, and
/// the two zero blocks at the end), so OpenSim's reader always reads what the launcher wrote.
/// </summary>
internal sealed class IarTarWriter : IDisposable
{
    private readonly Stream _file;
    private readonly GZipStream _gz;

    public IarTarWriter(string path)
    {
        _file = File.Create(path);
        _gz = new GZipStream(_file, CompressionLevel.Optimal);
    }

    public void Write(string name, char type, long size, Action<Stream> data)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        if (nameBytes.Length > 100)
        {
            Header("././@LongLink"u8.ToArray(), 'L', nameBytes.Length);
            _gz.Write(nameBytes);
            Pad(nameBytes.Length);
        }
        Header(nameBytes, type, size);
        var counted = new CountingStream(_gz);
        data(counted);
        if (counted.Count != size) throw new InvalidOperationException("An entry's data doesn't match its size.");
        Pad(size);
    }

    public void Dispose()
    {
        _gz.Write(new byte[1024]);
        _gz.Dispose();
        _file.Dispose();
    }

    private void Header(byte[] name, char type, long size)
    {
        var header = new byte[512];
        Array.Copy(name, header, Math.Min(name.Length, 100));
        "0000777"u8.CopyTo(header.AsSpan(100));
        "0000764"u8.CopyTo(header.AsSpan(108));
        "0000764"u8.CopyTo(header.AsSpan(116));
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0')).CopyTo(header.AsSpan(124));
        "11017037332"u8.CopyTo(header.AsSpan(136));
        header[156] = (byte)type;
        "0000000"u8.CopyTo(header.AsSpan(329));
        "0000000"u8.CopyTo(header.AsSpan(337));
        "        "u8.CopyTo(header.AsSpan(148));
        var sum = header.Sum(b => (int)b);
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0')).CopyTo(header.AsSpan(148));
        header[154] = 0;
        _gz.Write(header);
    }

    private void Pad(long size)
    {
        if (size % 512 != 0) _gz.Write(new byte[512 - size % 512]);
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Count += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); Count += buffer.Length; }
    }
}
