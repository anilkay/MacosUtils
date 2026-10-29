namespace MacosUtils.Core.SystemInfo;

/// <summary>Which set of environment variables to read.</summary>
public enum EnvironmentSource
{
    /// <summary>This app's own environment. An app opened from Finder gets launchd's short list.</summary>
    Process,
    /// <summary>What a new Terminal window sees: the login shell's environment after its profile files ran.</summary>
    LoginShell,
}

/// <summary>
/// Environment variables on macOS. Windows' User/Machine scopes do not exist here; the useful
/// difference on a Mac is between the app's own variables and the login shell's (PATH from
/// ~/.zprofile, Homebrew's shellenv...). Read-only.
/// </summary>
public static class EnvironmentInfo
{
    /// <summary>NAME → value, sorted by name.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Get(EnvironmentSource source)
    {
        var values = source == EnvironmentSource.Process ? ProcessVariables() : LoginShellVariables();
        return values.OrderBy(v => v.Key, StringComparer.Ordinal).ToList();
    }

    public static EnvironmentSource Parse(string? target) =>
        target?.Trim().ToLowerInvariant() is "shell" or "loginshell" or "login shell" or "terminal" or "user"
            ? EnvironmentSource.LoginShell
            : EnvironmentSource.Process;

    private static Dictionary<string, string> ProcessVariables()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            result[(string)entry.Key] = entry.Value as string ?? "";
        return result;
    }

    /// <summary>
    /// Runs the user's login shell once ("-l -c /usr/bin/env -0") and reads what it prints. Only a
    /// shell listed in /etc/shells is started, never one from a variable alone.
    /// </summary>
    private static Dictionary<string, string> LoginShellVariables()
    {
        if (!OperatingSystem.IsMacOS())
            return ProcessVariables();
        var shell = LoginShell();
        var result = Processes.Run(shell, ["-l", "-c", "/usr/bin/env -0"], TimeSpan.FromSeconds(10));
        if (!result.Success)
            throw new InvalidOperationException($"{shell} exited with code {result.ExitCode}: {result.Error.Trim()}");
        return ParseNullSeparated(result.Output);
    }

    /// <summary>Parses "env -0" output (NAME=value entries separated by NUL); public for tests.</summary>
    public static Dictionary<string, string> ParseNullSeparated(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = entry.IndexOf('=');
            if (equals > 0)
                result[entry[..equals].TrimStart('\n', '\r')] = entry[(equals + 1)..];
        }
        return result;
    }

    /// <summary>$SHELL when /etc/shells lists it, else /bin/zsh (the macOS default).</summary>
    public static string LoginShell()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        try
        {
            var allowed = File.ReadLines("/etc/shells")
                .Select(l => l.Trim())
                .Where(l => l.StartsWith('/'))
                .ToHashSet(StringComparer.Ordinal);
            if (shell is not null && allowed.Contains(shell) && File.Exists(shell))
                return shell;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return "/bin/zsh";
    }
}
