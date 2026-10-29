using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MacosUtils.Core.Logging;

/// <summary>
/// Process-wide logging entry point. Call <see cref="Configure"/> once at startup, then ask for
/// loggers anywhere via <see cref="Logger{T}"/> / <see cref="Logger"/>. Until configured, every
/// logger is a no-op, so the library is safe to use without any logging setup.
/// </summary>
public static class AppLog
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    /// <summary>The log folder: ~/Library/Logs/MacosUtils.</summary>
    public static string LogDirectory => AppPaths.LogDirectory;

    /// <summary>The current factory (a no-op factory before <see cref="Configure"/>).</summary>
    public static ILoggerFactory Factory => _factory;

    /// <summary>Replaces the current factory. Call once at startup.</summary>
    public static void Configure(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public static ILogger Logger(string category) => _factory.CreateLogger(category);

    public static ILogger Logger<T>() => _factory.CreateLogger<T>();
}
