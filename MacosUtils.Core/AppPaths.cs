namespace MacosUtils.Core;

/// <summary>
/// Where the app keeps its files, following the macOS conventions:
/// settings in ~/Library/Application Support/MacosUtils, logs in ~/Library/Logs/MacosUtils.
/// .NET maps SpecialFolder.ApplicationData to ~/.config on macOS, so the folders are built from
/// the home directory instead. Elsewhere (a developer build on Windows or Linux) the local
/// application-data folder is used.
/// </summary>
public static class AppPaths
{
    public const string AppName = "MacosUtils";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>~/Library/Application Support/MacosUtils.</summary>
    public static string SettingsDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Application Support", AppName)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>~/Library/Logs/MacosUtils (shown by Console.app under Log Reports).</summary>
    public static string LogDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Logs", AppName)
        : Path.Combine(SettingsDirectory, "logs");

    /// <summary>Creates <paramref name="directory"/> readable by the current user only (0700).</summary>
    public static void EnsurePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
