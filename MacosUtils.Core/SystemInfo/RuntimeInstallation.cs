using MacosUtils.Core.IO;

namespace MacosUtils.Core.SystemInfo;

/// <summary>An installed language runtime (Java, Python, Node.js, .NET) found on this Mac.</summary>
/// <param name="Version">e.g. "21.0.4", or "Unknown" if it cannot be read.</param>
/// <param name="Vendor">e.g. "Eclipse Adoptium", "Homebrew", or "" if unknown.</param>
/// <param name="Home">Installation folder.</param>
/// <param name="Source">Where it was found, e.g. JAVA_HOME, PATH, Homebrew, pyenv, nvm.</param>
public sealed record RuntimeInstallation(string Version, string Vendor, string Home, string Source)
{
    /// <summary>Short display text, e.g. "21.0.4 (Eclipse Adoptium)".</summary>
    public string Display => Vendor.Length > 0 ? $"{Version} ({Vendor})" : Version;
}

/// <summary>
/// Shared helpers for the runtime detectors. They only read the file system and environment;
/// they never start the runtime itself. /usr/bin/java and /usr/bin/python3 are left alone on
/// purpose: they are Apple's stubs, and running (or even resolving through) them can open the
/// "install the Command Line Tools / Java" dialog.
/// </summary>
internal static class RuntimeProbe
{
    /// <summary>Homebrew prefixes: Apple silicon, then Intel.</summary>
    public static readonly string[] HomebrewPrefixes = ["/opt/homebrew", "/usr/local"];

    /// <summary>Absolute path without a trailing slash, or null when empty, relative or on a network mount.</summary>
    public static string? LocalFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var trimmed = path.Trim().Trim('"');
            if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal))
                trimmed = AppPaths.Home + trimmed[1..];
            if (!Path.IsPathRooted(trimmed))
                return null;
            var full = Path.GetFullPath(trimmed);
            if (full.Length > 1)
                full = full.TrimEnd('/');
            return Volumes.IsNetworkPath(full) ? null : full;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Follows symlinks (Homebrew opt/ links, nvm "current") to the real folder.</summary>
    public static string ResolveDirectory(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true)?.FullName.TrimEnd('/') ?? dir;
        }
        catch (Exception)
        {
            return dir;
        }
    }

    /// <summary>The real file behind a symlink chain, or the path itself.</summary>
    public static string ResolveFile(string file)
    {
        try
        {
            return new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file;
        }
        catch (Exception)
        {
            return file;
        }
    }

    /// <summary>
    /// Full paths of every <paramref name="exeName"/> on PATH and in the Homebrew bin folders
    /// (an app opened from Finder only has the system PATH), in that order, links resolved.
    /// /usr/bin is skipped (Apple's stubs, see the class remarks).
    /// </summary>
    public static IEnumerable<(string Exe, bool OnPath)> FindExecutables(string exeName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => (Dir: d, OnPath: true));
        var brewDirs = HomebrewPrefixes.Select(p => (Dir: Path.Combine(p, "bin"), OnPath: false));
        foreach (var (entry, onPath) in pathDirs.Concat(brewDirs))
        {
            var dir = LocalFullPath(entry);
            if (dir is null || dir is "/usr/bin" or "/bin" || !seen.Add(dir))
                continue;
            string? found = null;
            try
            {
                var exe = Path.Combine(dir, exeName);
                if (File.Exists(exe))
                    found = ResolveFile(exe);
            }
            catch (Exception)
            {
                // Unreadable PATH entry - skip it.
            }
            if (found is not null)
                yield return (found, onPath);
        }
    }

    /// <summary>Subfolders of <paramref name="dir"/>, or none if it does not exist or cannot be read.</summary>
    public static IEnumerable<string> SubDirectories(string? dir)
    {
        var local = LocalFullPath(dir);
        if (local is null || !Directory.Exists(local))
            return [];
        try
        {
            return Directory.GetDirectories(local).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The first line of a small text file that starts with <paramref name="prefix"/>, or null.</summary>
    public static string? ReadLineStartingWith(string file, string prefix)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024)
                return null;
            return File.ReadLines(file).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Sort key for version strings such as "1.8.0_402", "21.0.4", "v20.11.1" or "3.12.4".
    /// Java's legacy "1.x" numbering is mapped to "x".
    /// </summary>
    public static Version SortKey(string version)
    {
        var parts = version.TrimStart('v', 'V').Split(['.', '_', '+', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsAsciiDigit).ToArray()), out var n) ? n : 0)
            .Concat([0, 0, 0, 0])
            .Take(4)
            .ToArray();
        if (parts[0] == 1 && parts[1] >= 2)
            parts = [parts[1], 0, parts[2], parts[3]]; // 1.8.0_402 -> 8.0.0.402
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    /// <summary>Collects installs, skipping folders already seen (compared after resolving links).</summary>
    public sealed class Collector
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public List<RuntimeInstallation> Items { get; } = [];

        /// <summary>Adds the install if its folder is new; <paramref name="home"/> must already be validated.</summary>
        public bool TryAdd(string home, Func<string, RuntimeInstallation?> describe)
        {
            var resolved = ResolveDirectory(home);
            if (!_seen.Add(resolved))
                return false;
            var item = describe(home);
            if (item is null)
                return false;
            Items.Add(item);
            return true;
        }

        /// <summary>Sorts the items from <paramref name="start"/> on by version, newest first.</summary>
        public void SortNewestFirst(int start)
        {
            if (start >= Items.Count)
                return;
            var tail = Items.Skip(start).OrderByDescending(i => SortKey(i.Version)).ToList();
            Items.RemoveRange(start, Items.Count - start);
            Items.AddRange(tail);
        }
    }
}
