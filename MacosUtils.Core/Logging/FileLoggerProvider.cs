using System.Globalization;
using Microsoft.Extensions.Logging;

namespace MacosUtils.Core.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that writes to a daily rolling text file, one per day
/// (<c>macosutils-yyyyMMdd.log</c>). Thread-safe and lazy: the file is only opened on the
/// first write of each day. A failed write (disk full, missing log folder, ...) is swallowed so
/// logging can never take the application down. Log files older than <paramref name="retainDays"/>
/// are deleted when the file rolls to a new day.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogWriter _writer;
    private readonly LogLevel _minLevel;

    /// <param name="directory">The folder that will contain the log files.</param>
    /// <param name="minLevel">Entries below this level are dropped.</param>
    /// <param name="retainDays">How many days of log files to keep; older files are deleted.
    /// Use a negative value to keep them all.</param>
    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information, int retainDays = 7)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _writer = new FileLogWriter(directory, retainDays);
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _minLevel);

    public void Dispose() => _writer.Dispose();
}

/// <summary>One logger per category, sharing the provider's single writer.</summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLogWriter _writer;
    private readonly LogLevel _minLevel;

    public FileLogger(string category, FileLogWriter writer, LogLevel minLevel)
    {
        _category = category;
        _writer = writer;
        _minLevel = minLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var text = formatter(state, exception);
        if (exception is not null)
            text = text.Length == 0 ? exception.ToString() : text + Environment.NewLine + exception;

        _writer.Write(logLevel, _category, text);
    }
}

/// <summary>Appends formatted lines to the current day's file, rolling at midnight.</summary>
internal sealed class FileLogWriter : IDisposable
{
    private const string FilePrefix = "macosutils-";
    private const string FileExtension = ".log";

    private readonly object _lock = new();
    private readonly string _directory;
    private readonly int _retainDays;
    private StreamWriter? _writer;
    private string _currentPath = "";
    private bool _failed;

    public FileLogWriter(string directory, int retainDays)
    {
        _directory = directory;
        _retainDays = retainDays;
    }

    public void Write(LogLevel level, string category, string message)
    {
        if (_failed)
            return;

        var now = DateTime.Now;
        var path = Path.Combine(_directory, FilePrefix + $"{now:yyyyMMdd}" + FileExtension);
        try
        {
            lock (_lock)
            {
                if (_writer is null || _currentPath != path)
                {
                    _writer?.Dispose();
                    AppPaths.EnsurePrivateDirectory(_directory);
                    _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                    _currentPath = path;
                    DeleteOldLogs(now);
                }
                _writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} [{LevelLabel(level),-5}] [{category}] {message}");
            }
        }
        catch (Exception)
        {
            // Logging must never crash the app; stop trying once the file is unwritable.
            _failed = true;
            try { _writer?.Dispose(); } catch (Exception) { }
            _writer = null;
        }
    }

    /// <summary>Deletes log files whose name date is older than <see cref="_retainDays"/> days.
    /// Runs only when the day rolls over (or on the first write), so at most once a day.</summary>
    private void DeleteOldLogs(DateTime now)
    {
        if (_retainDays < 0)
            return;

        var cutoff = now.Date.AddDays(-_retainDays);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var datePart = name[FilePrefix.Length..];
                if (datePart.Length != 8
                    || !DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || date >= cutoff)
                {
                    continue;
                }
                try { File.Delete(file); } catch (Exception) { /* in use by another process */ }
            }
        }
        catch (Exception)
        {
            // Cleanup must never crash logging.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "crit",
        _ => "none",
    };
}

internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();
    private NullScope() { }
    public void Dispose() { }
}
