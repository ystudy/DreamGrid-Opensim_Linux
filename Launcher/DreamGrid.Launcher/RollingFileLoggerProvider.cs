using DreamGrid.Launcher.Core.Logs;

namespace DreamGrid.Launcher;

/// <summary>
/// A minimal rolling log file: <c>launcher.log</c> is renamed to <c>launcher.1.log</c> (and so on, the oldest dropped)
/// once it passes the size limit (the rotation is <see cref="RollingFileWriter"/>, shared with the audit log). All loggers
/// share one writer, so concurrent requests cannot interleave lines.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly RollingFileWriter _file;
    private readonly TimeProvider _time;

    public RollingFileLoggerProvider(string path, long maxBytes = 10 * 1024 * 1024, int files = 5, TimeProvider? time = null)
    {
        _file = new RollingFileWriter(path, maxBytes, files);
        _time = time ?? TimeProvider.System;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() => _file.Dispose();

    private void Write(string line) => _file.WriteLine(line);

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                var text = formatter(state, exception);
                if (exception is not null) text += Environment.NewLine + exception;
                owner.Write($"{owner._time.GetUtcNow():yyyy-MM-dd HH:mm:ss.fff}Z {Short(logLevel)} {category}: {text}");
            }
            catch (Exception)
            {
                // Logging never throws into the caller.
            }
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
