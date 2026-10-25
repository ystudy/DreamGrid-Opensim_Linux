using System.Text;

namespace DreamGrid.Launcher;

/// <summary>
/// A minimal rolling log file: <c>launcher.log</c> is renamed to <c>launcher.1.log</c> (and so on, the oldest dropped)
/// once it passes the size limit. All loggers share one lock, so concurrent requests cannot interleave lines.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _name;
    private readonly string _extension;
    private readonly long _maxBytes;
    private readonly int _files;
    private readonly TimeProvider _time;
    private StreamWriter? _writer;
    private long _size;

    public RollingFileLoggerProvider(string path, long maxBytes = 10 * 1024 * 1024, int files = 5, TimeProvider? time = null)
    {
        _directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        _name = Path.GetFileNameWithoutExtension(path);
        _extension = Path.GetExtension(path);
        _maxBytes = maxBytes;
        _files = files;
        _time = time ?? TimeProvider.System;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private string FileName(int index) =>
        Path.Combine(_directory, index == 0 ? _name + _extension : $"{_name}.{index}{_extension}");

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                if (_writer is null) Open();
                else if (_size >= _maxBytes) Roll();
                _writer!.WriteLine(line);
                _size += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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
        if (_size >= _maxBytes) Roll();
    }

    private void Roll()
    {
        _writer?.Dispose();
        _writer = null;
        // The current file counts as one of the files, so launcher.{files-1}.log is the oldest kept.
        File.Delete(FileName(_files - 1));
        for (var i = _files - 2; i >= 0; i--)
            if (File.Exists(FileName(i))) File.Move(FileName(i), FileName(i + 1), true);
        Open();
    }

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = formatter(state, exception);
            if (exception is not null) text += Environment.NewLine + exception;
            owner.Write($"{owner._time.GetUtcNow():yyyy-MM-dd HH:mm:ss.fff}Z {Short(logLevel)} {category}: {text}");
        }

        private static string Short(LogLevel l) => l switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            _ => "crit",
        };
    }
}
