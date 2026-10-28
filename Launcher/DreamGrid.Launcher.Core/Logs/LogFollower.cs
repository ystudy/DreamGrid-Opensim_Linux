using System.Text;

namespace DreamGrid.Launcher.Core.Logs;

/// <summary>
/// Reads the whole lines a log file gains after the follower was made; a line still being written waits for the next read.
/// A file that shrank (rolled over) is read again from its start.
/// </summary>
internal sealed class LogFollower(string path)
{
    private long _offset = LengthOf(path);
    private byte[] _partial = [];

    public IReadOnlyList<string> ReadNew()
    {
        FileStream stream;
        try
        {
            // OpenSim keeps writing while we read.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }

        using (stream)
        {
            var length = stream.Length;
            if (length < _offset) (_offset, _partial) = (0, []);
            stream.Seek(_offset, SeekOrigin.Begin);
            var added = new byte[length - _offset];
            var read = 0;
            while (read < added.Length)
            {
                var n = stream.Read(added, read, added.Length - read);
                if (n == 0) break;
                read += n;
            }
            _offset += read;

            var bytes = _partial.Concat(added.Take(read)).ToArray();
            // A '\n' byte is never part of a longer UTF-8 character, so cutting after it keeps every character whole.
            var end = Array.LastIndexOf(bytes, (byte)'\n');
            _partial = bytes[(end + 1)..];
            if (end < 0) return [];
            return Encoding.UTF8.GetString(bytes, 0, end).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        }
    }

    private static long LengthOf(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }
}
