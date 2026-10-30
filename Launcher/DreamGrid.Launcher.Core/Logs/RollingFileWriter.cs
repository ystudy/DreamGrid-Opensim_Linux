using System.Text;

namespace DreamGrid.Launcher.Core.Logs;

/// <summary>
/// Appends lines to a file that rolls by size: <c>name.ext</c> is renamed to <c>name.1.ext</c> (and so on, the oldest
/// dropped) once it passes the limit. One lock covers every write, so concurrent callers cannot interleave lines. Writing
/// never throws: a failed line is lost and the next write tries again. The launcher log and the audit log both use it.
/// </summary>
public sealed class RollingFileWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _name;
    private readonly string _extension;
    private readonly long _maxBytes;
    private readonly int _files;
    private StreamWriter? _writer;
    private long _size;
    private long _rollRetryAt;
    private bool _disposed;

    public RollingFileWriter(string path, long maxBytes = 10 * 1024 * 1024, int files = 5)
    {
        _directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        _name = Path.GetFileNameWithoutExtension(path);
        _extension = Path.GetExtension(path);
        _maxBytes = maxBytes;
        _files = files;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private string FileName(int index) =>
        Path.Combine(_directory, index == 0 ? _name + _extension : $"{_name}.{index}{_extension}");

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_disposed) return; // a late line after shutdown must not reopen the file
            try
            {
                if (_writer is null) Open();
                else if (_size >= Math.Max(_maxBytes, _rollRetryAt)) Roll();
                _writer!.WriteLine(line);
                _size += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            }
            catch (Exception)
            {
                // Logging must never take a request down; the line is lost and the next write retries.
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private void Open()
    {
        Directory.CreateDirectory(_directory);
        var stream = new FileStream(FileName(0), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _size = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        if (_size >= Math.Max(_maxBytes, _rollRetryAt)) Roll();
    }

    private void Roll()
    {
        _writer?.Dispose();
        _writer = null;
        try
        {
            // The current file counts as one of the files, so name.{files-1}.ext is the oldest kept.
            File.Delete(FileName(_files - 1));
            for (var i = _files - 2; i >= 0; i--)
                if (File.Exists(FileName(i))) File.Move(FileName(i), FileName(i + 1), true);
            _rollRetryAt = 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep appending to the current file rather than losing lines, and try again after another file's worth.
            _rollRetryAt = _size + _maxBytes;
        }
        Open();
    }
}
