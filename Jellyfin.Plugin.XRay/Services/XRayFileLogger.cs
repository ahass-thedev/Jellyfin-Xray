using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Services;

/// <summary>
/// Writes to a dedicated xray.log file in the plugin data directory.
/// </summary>
public sealed class XRayFileLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;

    public XRayFileLogger(string dataPath)
    {
        try
        {
            Directory.CreateDirectory(dataPath);
            var logPath = Path.Combine(dataPath, "xray.log");
            _writer = new StreamWriter(logPath, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
            Write("INF", "=== X-Ray plugin started ===");
        }
        catch
        {
            // If the log file can't be opened, fall back to a no-op writer
            _writer = StreamWriter.Null;
        }
    }

    internal void Write(string level, string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] {message}";
        try
        {
            lock (_lock)
            {
                if (!_disposed)
                    _writer.WriteLine(line);
            }
        }
        catch
        {
            // Never let a log write crash the caller
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}

/// <summary>
/// Wraps an <see cref="ILogger{T}"/> and mirrors every log call to <see cref="XRayFileLogger"/>
/// so X-Ray entries appear in both Jellyfin's main log and the dedicated plugin log file.
/// </summary>
public sealed class TeeLogger<T> : ILogger<T>
{
    private readonly ILogger<T> _inner;
    private readonly XRayFileLogger _file;

    public TeeLogger(ILogger<T> inner, XRayFileLogger file)
    {
        _inner = inner;
        _file = file;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _inner.Log(logLevel, eventId, state, exception, formatter);

        var level = logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "UNK"
        };

        _file.Write(level, $"[{typeof(T).Name}] {formatter(state, exception)}");
        if (exception is not null)
            _file.Write(level, exception.ToString());
    }
}
