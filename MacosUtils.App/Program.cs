using Avalonia;
using Microsoft.Extensions.Logging;
using MacosUtils.Core.Logging;

namespace MacosUtils.App;

internal static class Program
{
    private static ILogger Log => AppLog.Logger("MacosUtils");

    [STAThread]
    public static int Main(string[] args)
    {
        ConfigureLogging();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.LogError(e.ExceptionObject as Exception, "Unhandled exception.");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Log.LogError(e.Exception, "Unobserved task exception.");

        Log.LogInformation("MacosUtils started (runtime {Runtime}, log folder {LogDir}).",
            Environment.Version, AppLog.LogDirectory);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Log.LogInformation("Application exiting.");
            AppLog.Factory.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void ConfigureLogging()
    {
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(AppLog.LogDirectory));
        });
        AppLog.Configure(factory);
    }
}
