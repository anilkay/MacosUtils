using System.Text.RegularExpressions;

namespace MacosUtils.Core.SystemInfo;

/// <summary>
/// Installed Java (JDK/JRE): JAVA_HOME, the JVMs macOS knows (/Library/Java/JavaVirtualMachines,
/// the same folders /usr/libexec/java_home reads), Homebrew's openjdk, SDKMAN and java on PATH.
/// Version and vendor come from each install's "release" file. The first item is the default.
/// </summary>
public static class JavaDetector
{
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var collector = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsMacOS())
            return collector.Items;

        if (RuntimeProbe.LocalFullPath(Environment.GetEnvironmentVariable("JAVA_HOME")) is { } javaHome && IsJavaHome(javaHome))
            collector.TryAdd(javaHome, h => Describe(h, "JAVA_HOME"));

        // java_home is a system tool, not Java itself: it only reads the JVM folders.
        var defaultHome = RuntimeProbe.LocalFullPath(Processes.TryRun("/usr/libexec/java_home", [])?.Trim());
        if (defaultHome is not null && IsJavaHome(defaultHome))
            collector.TryAdd(defaultHome, h => Describe(h, "java_home default"));

        var start = collector.Items.Count;
        foreach (var root in (string[])["/Library/Java/JavaVirtualMachines", Path.Combine(AppPaths.Home, "Library", "Java", "JavaVirtualMachines")])
        {
            foreach (var jvm in RuntimeProbe.SubDirectories(root))
            {
                var home = Path.Combine(jvm, "Contents", "Home");
                if (IsJavaHome(home))
                    collector.TryAdd(home, h => Describe(h, "JavaVirtualMachines"));
            }
        }
        foreach (var prefix in RuntimeProbe.HomebrewPrefixes)
        {
            foreach (var formula in RuntimeProbe.SubDirectories(Path.Combine(prefix, "Cellar")).Where(d => Path.GetFileName(d).StartsWith("openjdk", StringComparison.Ordinal)))
            {
                foreach (var version in RuntimeProbe.SubDirectories(formula))
                {
                    var home = Path.Combine(version, "libexec", "openjdk.jdk", "Contents", "Home");
                    if (IsJavaHome(home))
                        collector.TryAdd(home, h => Describe(h, "Homebrew"));
                }
            }
        }
        foreach (var candidate in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, ".sdkman", "candidates", "java")))
        {
            if (Path.GetFileName(candidate) != "current" && IsJavaHome(candidate))
                collector.TryAdd(candidate, h => Describe(h, "SDKMAN"));
        }
        foreach (var (exe, _) in RuntimeProbe.FindExecutables("java"))
        {
            var home = Path.GetDirectoryName(Path.GetDirectoryName(exe));
            if (home is not null && IsJavaHome(home))
                collector.TryAdd(home, h => Describe(h, "PATH"));
        }
        collector.SortNewestFirst(start);
        return collector.Items;
    }

    private static bool IsJavaHome(string home) => File.Exists(Path.Combine(home, "bin", "java"));

    private static RuntimeInstallation Describe(string home, string source)
    {
        var release = Path.Combine(home, "release");
        var version = Unquote(RuntimeProbe.ReadLineStartingWith(release, "JAVA_VERSION=")) ?? "Unknown";
        var vendor = Unquote(RuntimeProbe.ReadLineStartingWith(release, "IMPLEMENTOR=")) ?? "";
        return new RuntimeInstallation(version, vendor, home, source);
    }

    private static string? Unquote(string? line) =>
        line is null ? null : line[(line.IndexOf('=') + 1)..].Trim().Trim('"');
}

/// <summary>
/// Installed Python: python3 on PATH and in Homebrew, the python.org framework, Apple's copy in the
/// Command Line Tools / Xcode, Homebrew's Cellar, pyenv and uv. The full version comes from each
/// install's include/pythonX.Y/patchlevel.h. The first item is the one a terminal runs.
/// </summary>
public static partial class PythonDetector
{
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var collector = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsMacOS())
            return collector.Items;

        foreach (var (exe, onPath) in RuntimeProbe.FindExecutables("python3"))
        {
            if (HomeOf(exe) is { } home)
                collector.TryAdd(home, h => Describe(h, onPath ? "PATH" : "Homebrew"));
        }
        var start = collector.Items.Count;

        AddVersions(collector, "/Library/Frameworks/Python.framework/Versions", "python.org");
        AddVersions(collector, "/Library/Developer/CommandLineTools/Library/Frameworks/Python3.framework/Versions", "Command Line Tools");
        AddVersions(collector, "/Applications/Xcode.app/Contents/Developer/Library/Frameworks/Python3.framework/Versions", "Xcode");
        foreach (var prefix in RuntimeProbe.HomebrewPrefixes)
        {
            foreach (var formula in RuntimeProbe.SubDirectories(Path.Combine(prefix, "Cellar")).Where(d => Path.GetFileName(d).StartsWith("python@", StringComparison.Ordinal)))
            {
                foreach (var version in RuntimeProbe.SubDirectories(formula))
                    AddVersions(collector, Path.Combine(version, "Frameworks", "Python.framework", "Versions"), "Homebrew");
            }
        }
        foreach (var version in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, ".pyenv", "versions")))
            TryAdd(collector, version, "pyenv");
        foreach (var version in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, ".local", "share", "uv", "python")))
            TryAdd(collector, version, "uv");

        collector.SortNewestFirst(start);
        return collector.Items;
    }

    private static void AddVersions(RuntimeProbe.Collector collector, string versionsDir, string source)
    {
        foreach (var version in RuntimeProbe.SubDirectories(versionsDir))
        {
            if (Path.GetFileName(version) != "Current")
                TryAdd(collector, version, source);
        }
    }

    private static void TryAdd(RuntimeProbe.Collector collector, string home, string source)
    {
        if (Directory.Exists(Path.Combine(home, "bin")) && (File.Exists(Path.Combine(home, "bin", "python3")) || PatchLevel(home) is not null))
            collector.TryAdd(home, h => Describe(h, source));
    }

    // .../bin/python3.12 -> the folder that holds bin/ and include/.
    private static string? HomeOf(string exe) => Path.GetDirectoryName(Path.GetDirectoryName(exe));

    private static RuntimeInstallation Describe(string home, string source)
    {
        var resolved = RuntimeProbe.ResolveDirectory(home);
        var version = PatchLevel(resolved) ?? PatchLevel(home) ?? "Unknown";
        var vendor = resolved switch
        {
            _ when resolved.Contains("/Cellar/", StringComparison.Ordinal) => "Homebrew",
            _ when resolved.StartsWith("/Library/Frameworks/Python.framework", StringComparison.Ordinal) => "python.org",
            _ when resolved.Contains("CommandLineTools", StringComparison.Ordinal) || resolved.Contains("Xcode.app", StringComparison.Ordinal) => "Apple",
            _ when resolved.Contains("/.pyenv/", StringComparison.Ordinal) => "pyenv",
            _ when resolved.Contains("/uv/python/", StringComparison.Ordinal) => "uv",
            _ => "",
        };
        return new RuntimeInstallation(version, vendor, resolved, source);
    }

    // #define PY_VERSION              "3.12.4"
    private static string? PatchLevel(string home)
    {
        foreach (var include in RuntimeProbe.SubDirectories(Path.Combine(home, "include")))
        {
            var line = RuntimeProbe.ReadLineStartingWith(Path.Combine(include, "patchlevel.h"), "#define PY_VERSION");
            var match = line is null ? null : QuotedRegex().Match(line);
            if (match is { Success: true })
                return match.Groups[1].Value;
        }
        return null;
    }

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedRegex();
}

/// <summary>
/// Installed Node.js: node on PATH and in Homebrew, nvm, Volta and fnm. The version comes from
/// include/node/node_version.h, or the folder name for version managers. The first item is the
/// one a terminal runs.
/// </summary>
public static partial class NodeDetector
{
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var collector = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsMacOS())
            return collector.Items;

        foreach (var (exe, onPath) in RuntimeProbe.FindExecutables("node"))
        {
            var home = Path.GetDirectoryName(Path.GetDirectoryName(exe));
            if (home is not null)
                collector.TryAdd(home, h => Describe(h, onPath ? "PATH" : "Homebrew"));
        }
        var start = collector.Items.Count;

        foreach (var version in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, ".nvm", "versions", "node")))
            TryAdd(collector, version, "nvm");
        foreach (var version in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, ".volta", "tools", "image", "node")))
            TryAdd(collector, version, "Volta");
        foreach (var fnmRoot in (string[])[
                     Path.Combine(AppPaths.Home, "Library", "Application Support", "fnm", "node-versions"),
                     Path.Combine(AppPaths.Home, ".local", "share", "fnm", "node-versions"),
                     Path.Combine(AppPaths.Home, ".fnm", "node-versions")])
        {
            foreach (var version in RuntimeProbe.SubDirectories(fnmRoot))
                TryAdd(collector, Path.Combine(version, "installation"), "fnm");
        }
        foreach (var prefix in RuntimeProbe.HomebrewPrefixes)
        {
            foreach (var formula in RuntimeProbe.SubDirectories(Path.Combine(prefix, "Cellar")).Where(d => Path.GetFileName(d) is var n && (n == "node" || n.StartsWith("node@", StringComparison.Ordinal))))
            {
                foreach (var version in RuntimeProbe.SubDirectories(formula))
                    TryAdd(collector, version, "Homebrew");
            }
        }
        collector.SortNewestFirst(start);
        return collector.Items;
    }

    private static void TryAdd(RuntimeProbe.Collector collector, string home, string source)
    {
        if (File.Exists(Path.Combine(home, "bin", "node")))
            collector.TryAdd(home, h => Describe(h, source));
    }

    private static RuntimeInstallation Describe(string home, string source)
    {
        var resolved = RuntimeProbe.ResolveDirectory(home);
        var version = HeaderVersion(resolved) ?? FolderVersion(resolved) ?? "Unknown";
        var vendor = resolved.Contains("/Cellar/", StringComparison.Ordinal) ? "Homebrew" : "";
        return new RuntimeInstallation(version, vendor, resolved, source);
    }

    // #define NODE_MAJOR_VERSION 20 / NODE_MINOR_VERSION 11 / NODE_PATCH_VERSION 1
    private static string? HeaderVersion(string home)
    {
        var header = Path.Combine(home, "include", "node", "node_version.h");
        string? Part(string name)
        {
            var line = RuntimeProbe.ReadLineStartingWith(header, "#define " + name + " ");
            return line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        }
        var (major, minor, patch) = (Part("NODE_MAJOR_VERSION"), Part("NODE_MINOR_VERSION"), Part("NODE_PATCH_VERSION"));
        return major is null || minor is null || patch is null ? null : $"{major}.{minor}.{patch}";
    }

    // nvm: .../v20.11.1, Volta: .../20.11.1, fnm: .../v20.11.1/installation, Homebrew: Cellar/node/22.3.0
    private static string? FolderVersion(string home)
    {
        foreach (var name in new[] { Path.GetFileName(home), Path.GetFileName(Path.GetDirectoryName(home) ?? "") })
        {
            var match = VersionRegex().Match(name);
            if (match.Success)
                return match.Groups[1].Value;
        }
        return null;
    }

    [GeneratedRegex(@"^v?(\d+\.\d+\.\d+)")]
    private static partial Regex VersionRegex();
}

/// <summary>
/// Installed .NET SDKs and runtimes — the Mac stand-in for WindowsUtils' .NET Framework check.
/// Reads the version folders under each dotnet root (DOTNET_ROOT, /usr/local/share/dotnet and its
/// x64 subfolder on Apple silicon, ~/.dotnet, Homebrew); dotnet itself is never started.
/// </summary>
public static class DotNetDetector
{
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var items = new List<RuntimeInstallation>();
        if (!OperatingSystem.IsMacOS())
            return items;

        var roots = new List<(string Path, string Source)>();
        if (RuntimeProbe.LocalFullPath(Environment.GetEnvironmentVariable("DOTNET_ROOT")) is { } dotnetRoot)
            roots.Add((dotnetRoot, "DOTNET_ROOT"));
        roots.Add(("/usr/local/share/dotnet", "Installer"));
        roots.Add(("/usr/local/share/dotnet/x64", "Installer (x64)"));
        roots.Add((Path.Combine(AppPaths.Home, ".dotnet"), "~/.dotnet"));
        foreach (var prefix in RuntimeProbe.HomebrewPrefixes)
            roots.Add((Path.Combine(prefix, "opt", "dotnet", "libexec"), "Homebrew"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, source) in roots)
        {
            if (!Directory.Exists(root) || !seen.Add(RuntimeProbe.ResolveDirectory(root)))
                continue;
            foreach (var sdk in RuntimeProbe.SubDirectories(Path.Combine(root, "sdk")).Where(d => char.IsAsciiDigit(Path.GetFileName(d)[0])))
                items.Add(new RuntimeInstallation(Path.GetFileName(sdk), "SDK", sdk, source));
            foreach (var runtime in RuntimeProbe.SubDirectories(Path.Combine(root, "shared", "Microsoft.NETCore.App")))
                items.Add(new RuntimeInstallation(Path.GetFileName(runtime), "Runtime", runtime, source));
            foreach (var runtime in RuntimeProbe.SubDirectories(Path.Combine(root, "shared", "Microsoft.AspNetCore.App")))
                items.Add(new RuntimeInstallation(Path.GetFileName(runtime), "ASP.NET Core runtime", runtime, source));
        }
        return items
            .OrderBy(i => i.Vendor == "SDK" ? 0 : i.Vendor == "Runtime" ? 1 : 2)
            .ThenByDescending(i => RuntimeProbe.SortKey(i.Version))
            .ToList();
    }
}
