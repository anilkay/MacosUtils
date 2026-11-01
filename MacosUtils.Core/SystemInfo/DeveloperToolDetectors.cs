using System.Text.RegularExpressions;

namespace MacosUtils.Core.SystemInfo;

/// <summary>Shared pieces of the Go, Rust, Ruby and PHP detectors.</summary>
internal static partial class ToolProbe
{
    /// <summary>Install folders asdf and mise keep for <paramref name="tool"/> (their plugin name).</summary>
    public static IEnumerable<(string Home, string Source)> VersionManagerInstalls(string tool)
    {
        foreach (var version in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, ".asdf", "installs", tool)))
            yield return (version, "asdf");
        foreach (var version in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, ".local", "share", "mise", "installs", tool)))
        {
            // mise links "latest" and "3" to the full version folder.
            if (!new DirectoryInfo(version).Attributes.HasFlag(FileAttributes.ReparsePoint))
                yield return (version, "mise");
        }
    }

    /// <summary>Every version folder of Homebrew formulae <paramref name="formula"/> and <paramref name="formula"/>@N.</summary>
    public static IEnumerable<string> HomebrewKegs(string formula)
    {
        foreach (var prefix in RuntimeProbe.HomebrewPrefixes)
        {
            foreach (var keg in RuntimeProbe.SubDirectories(Path.Combine(prefix, "Cellar"))
                         .Where(d => Path.GetFileName(d) is var n && (n == formula || n.StartsWith(formula + "@", StringComparison.Ordinal))))
            {
                foreach (var version in RuntimeProbe.SubDirectories(keg))
                    yield return version;
            }
        }
    }

    /// <summary>The value of the first <c>KEY = "value"</c> or <c>#define KEY "value"</c> line (leading spaces allowed), or null.</summary>
    public static string? QuotedValue(string file, string startsWith, int maxLines = 200_000)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            foreach (var line in File.ReadLines(file).Take(maxLines))
            {
                if (line.TrimStart().StartsWith(startsWith, StringComparison.Ordinal))
                    // The last quoted string: rbconfig.rb lines read CONFIG["KEY"] = "value".
                    return QuotedRegex().Matches(line) is { Count: > 0 } m ? m[^1].Groups[1].Value : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>"1.23.2" from folder names such as "go1.23.2", "ruby-3.3.5", "php8.2.0", "1.82.0-aarch64-apple-darwin".</summary>
    public static string? FolderVersion(string home)
    {
        foreach (var name in new[] { Path.GetFileName(home), Path.GetFileName(Path.GetDirectoryName(home) ?? "") })
        {
            var match = FolderVersionRegex().Match(name);
            if (match.Success)
                return match.Groups[1].Value;
        }
        return null;
    }

    public static string VendorFromPath(string resolved, string fallback = "") => resolved switch
    {
        _ when resolved.Contains("/Cellar/", StringComparison.Ordinal) => "Homebrew",
        _ when resolved.Contains("/.asdf/", StringComparison.Ordinal) => "asdf",
        _ when resolved.Contains("/mise/installs/", StringComparison.Ordinal) => "mise",
        _ => fallback,
    };

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedRegex();

    [GeneratedRegex(@"^(?:[A-Za-z]+[-@]?)?v?(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex FolderVersionRegex();
}

/// <summary>
/// Installed Go: GOROOT, go on PATH and in Homebrew, the go.dev installer (/usr/local/go),
/// golang.org/dl downloads (~/sdk), toolchains Go downloads itself (GOTOOLCHAIN), goenv, asdf and
/// mise. The version comes from each GOROOT's VERSION file. The first item is the one a terminal runs.
/// </summary>
public static class GoDetector
{
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var collector = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsMacOS())
            return collector.Items;

        foreach (var (exe, onPath) in RuntimeProbe.FindExecutables("go"))
            TryAdd(collector, Path.GetDirectoryName(Path.GetDirectoryName(exe)), onPath ? "PATH" : "Homebrew");
        TryAdd(collector, RuntimeProbe.LocalFullPath(Environment.GetEnvironmentVariable("GOROOT")), "GOROOT");
        var start = collector.Items.Count;

        TryAdd(collector, "/usr/local/go", "go.dev installer");
        foreach (var keg in ToolProbe.HomebrewKegs("go"))
            TryAdd(collector, Path.Combine(keg, "libexec"), "Homebrew");
        foreach (var sdk in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, "sdk")).Where(d => Path.GetFileName(d).StartsWith("go", StringComparison.Ordinal)))
            TryAdd(collector, sdk, "golang.org/dl");
        var goPath = RuntimeProbe.LocalFullPath(Environment.GetEnvironmentVariable("GOPATH")?.Split(':')[0]) ?? Path.Combine(AppPaths.Home, "go");
        foreach (var toolchain in RuntimeProbe.SubDirectories(Path.Combine(goPath, "pkg", "mod", "golang.org"))
                     .Where(d => Path.GetFileName(d).StartsWith("toolchain@", StringComparison.Ordinal)))
            TryAdd(collector, toolchain, "GOTOOLCHAIN download");
        foreach (var version in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, ".goenv", "versions")))
            TryAdd(collector, version, "goenv");
        foreach (var (home, source) in ToolProbe.VersionManagerInstalls("golang").Concat(ToolProbe.VersionManagerInstalls("go")))
            TryAdd(collector, Directory.Exists(Path.Combine(home, "go", "bin")) ? Path.Combine(home, "go") : home, source);

        collector.SortNewestFirst(start);
        return collector.Items;
    }

    /// <summary>"go1.23.2" (first line of a GOROOT's VERSION file) → "1.23.2". Public for tests.</summary>
    public static string? ParseVersionFile(string text)
    {
        var first = text.Split('\n', 2)[0].Trim();
        if (first.Length == 0)
            return null;
        return first.StartsWith("go", StringComparison.Ordinal) && first.Length > 2 && char.IsAsciiDigit(first[2]) ? first[2..] : first;
    }

    private static void TryAdd(RuntimeProbe.Collector collector, string? home, string source)
    {
        if (home is not null && File.Exists(Path.Combine(home, "bin", "go")))
            collector.TryAdd(home, h => Describe(h, source));
    }

    private static RuntimeInstallation Describe(string home, string source)
    {
        var resolved = RuntimeProbe.ResolveDirectory(home);
        string? version = null;
        try
        {
            var file = Path.Combine(resolved, "VERSION");
            if (File.Exists(file) && new FileInfo(file).Length < 64 * 1024)
                version = ParseVersionFile(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        version ??= ToolProbe.FolderVersion(resolved) ?? "Unknown";
        var vendor = ToolProbe.VendorFromPath(resolved, resolved.Contains("toolchain@", StringComparison.Ordinal) ? "auto-downloaded" : "");
        return new RuntimeInstallation(version, vendor, resolved, source);
    }
}

/// <summary>
/// Installed Rust: rustup toolchains (~/.rustup, or RUSTUP_HOME) with the default first, Homebrew's
/// rust, and rustc on PATH outside ~/.cargo/bin (those are rustup's proxies). The version comes
/// from the channel manifest rustup keeps in each toolchain; rustc is never run.
/// </summary>
public static class RustDetector
{
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var collector = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsMacOS())
            return collector.Items;

        var rustupHome = RuntimeProbe.LocalFullPath(Environment.GetEnvironmentVariable("RUSTUP_HOME")) ?? Path.Combine(AppPaths.Home, ".rustup");
        var toolchains = RuntimeProbe.SubDirectories(Path.Combine(rustupHome, "toolchains")).ToList();
        var defaultName = ToolProbe.QuotedValue(Path.Combine(rustupHome, "settings.toml"), "default_toolchain");
        foreach (var toolchain in toolchains.Where(t => defaultName is not null && Path.GetFileName(t) == defaultName))
            TryAdd(collector, toolchain, "rustup default");

        foreach (var (exe, onPath) in RuntimeProbe.FindExecutables("rustc"))
        {
            // ~/.cargo/bin/rustc is a link to rustup, which picks a toolchain; that toolchain is listed above.
            if (Path.GetFileName(exe) != "rustc")
                continue;
            TryAdd(collector, Path.GetDirectoryName(Path.GetDirectoryName(exe)), onPath ? "PATH" : "Homebrew");
        }
        var start = collector.Items.Count;
        foreach (var toolchain in toolchains)
            TryAdd(collector, toolchain, "rustup");
        foreach (var keg in ToolProbe.HomebrewKegs("rust"))
            TryAdd(collector, keg, "Homebrew");
        foreach (var (home, source) in ToolProbe.VersionManagerInstalls("rust"))
            TryAdd(collector, home, source);
        collector.SortNewestFirst(start);
        return collector.Items;
    }

    /// <summary>
    /// The rustc version from a channel manifest: the <c>version = "1.82.0 (f6e511eec 2024-10-15)"</c>
    /// line of the <c>[pkg.rust]</c> table → "1.82.0". Public for tests.
    /// </summary>
    public static string? ParseChannelManifest(IEnumerable<string> lines)
    {
        var inRust = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (inRust)
                    return null;
                inRust = line == "[pkg.rust]";
            }
            else if (inRust && line.StartsWith("version", StringComparison.Ordinal) && line.Split('"') is { Length: >= 2 } parts)
            {
                return parts[1].Split(' ')[0];
            }
        }
        return null;
    }

    private static void TryAdd(RuntimeProbe.Collector collector, string? home, string source)
    {
        if (home is not null && File.Exists(Path.Combine(home, "bin", "rustc")))
            collector.TryAdd(home, h => Describe(h, source));
    }

    private static RuntimeInstallation Describe(string home, string source)
    {
        var resolved = RuntimeProbe.ResolveDirectory(home);
        string? version = null;
        try
        {
            var manifest = Path.Combine(resolved, "lib", "rustlib", "multirust-channel-manifest.toml");
            if (File.Exists(manifest))
                version = ParseChannelManifest(File.ReadLines(manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        var name = Path.GetFileName(resolved);
        version ??= ToolProbe.FolderVersion(resolved) ?? "Unknown";
        // rustup names toolchains "stable-aarch64-apple-darwin", "nightly-2024-10-01-...", "1.82.0-...".
        var channel = name.Split('-')[0] is var c && c is "stable" or "beta" or "nightly" ? c : "";
        return new RuntimeInstallation(version, ToolProbe.VendorFromPath(resolved, channel), resolved, source);
    }
}

/// <summary>
/// Installed Ruby: ruby on PATH and in Homebrew, rbenv, rvm, chruby (~/.rubies, /opt/rubies),
/// asdf, mise, and the old Ruby 2.6 macOS still carries. The version comes from each install's
/// rbconfig.rb. The first item is the one a terminal runs.
/// </summary>
public static class RubyDetector
{
    private const string SystemRuby = "/System/Library/Frameworks/Ruby.framework/Versions";

    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var collector = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsMacOS())
            return collector.Items;

        foreach (var (exe, onPath) in RuntimeProbe.FindExecutables("ruby"))
            TryAdd(collector, Path.GetDirectoryName(Path.GetDirectoryName(exe)), onPath ? "PATH" : "Homebrew");
        var start = collector.Items.Count;

        foreach (var keg in ToolProbe.HomebrewKegs("ruby"))
            TryAdd(collector, keg, "Homebrew");
        foreach (var version in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, ".rbenv", "versions")))
            TryAdd(collector, version, "rbenv");
        foreach (var version in RuntimeProbe.SubDirectories(Path.Combine(AppPaths.Home, ".rvm", "rubies")))
            TryAdd(collector, version, "rvm");
        foreach (var root in (string[])[Path.Combine(AppPaths.Home, ".rubies"), "/opt/rubies"])
        {
            foreach (var version in RuntimeProbe.SubDirectories(root))
                TryAdd(collector, version, "chruby");
        }
        foreach (var (home, source) in ToolProbe.VersionManagerInstalls("ruby"))
            TryAdd(collector, home, source);
        foreach (var version in RuntimeProbe.SubDirectories(SystemRuby).Where(d => Path.GetFileName(d) != "Current"))
            TryAdd(collector, Path.Combine(version, "usr"), "macOS");

        collector.SortNewestFirst(start);
        return collector.Items;
    }

    /// <summary>The full version from rbconfig.rb, found under lib/ruby/&lt;api&gt;/&lt;arch&gt;/.</summary>
    public static string? RbConfigVersion(string home)
    {
        foreach (var api in RuntimeProbe.SubDirectories(Path.Combine(home, "lib", "ruby")).Where(d => char.IsAsciiDigit(Path.GetFileName(d)[0])))
        {
            foreach (var arch in RuntimeProbe.SubDirectories(api))
            {
                var version = ToolProbe.QuotedValue(Path.Combine(arch, "rbconfig.rb"), "CONFIG[\"RUBY_PROGRAM_VERSION\"]");
                if (version is not null)
                    return version;
            }
        }
        return null;
    }

    private static void TryAdd(RuntimeProbe.Collector collector, string? home, string source)
    {
        if (home is not null && File.Exists(Path.Combine(home, "bin", "ruby")))
            collector.TryAdd(home, h => Describe(h, source));
    }

    private static RuntimeInstallation Describe(string home, string source)
    {
        var resolved = RuntimeProbe.ResolveDirectory(home);
        var version = RbConfigVersion(resolved) ?? ToolProbe.FolderVersion(resolved)
            ?? (resolved.StartsWith(SystemRuby, StringComparison.Ordinal) ? Path.GetFileName(Path.GetDirectoryName(resolved)) : null) ?? "Unknown";
        var vendor = resolved switch
        {
            _ when resolved.StartsWith(SystemRuby, StringComparison.Ordinal) => "Apple, deprecated",
            _ when resolved.Contains("/.rbenv/", StringComparison.Ordinal) => "rbenv",
            _ when resolved.Contains("/.rvm/", StringComparison.Ordinal) => "rvm",
            _ => ToolProbe.VendorFromPath(resolved),
        };
        return new RuntimeInstallation(version, vendor, resolved, source);
    }
}

/// <summary>
/// Installed PHP: php on PATH and in Homebrew (php, php@8.x), MAMP, asdf and mise. The version
/// comes from include/php/main/php_version.h, or the folder name. The first item is the one a
/// terminal runs.
/// </summary>
public static class PhpDetector
{
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var collector = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsMacOS())
            return collector.Items;

        foreach (var (exe, onPath) in RuntimeProbe.FindExecutables("php"))
            TryAdd(collector, Path.GetDirectoryName(Path.GetDirectoryName(exe)), onPath ? "PATH" : "Homebrew");
        var start = collector.Items.Count;

        foreach (var keg in ToolProbe.HomebrewKegs("php"))
            TryAdd(collector, keg, "Homebrew");
        foreach (var version in RuntimeProbe.SubDirectories("/Applications/MAMP/bin/php"))
            TryAdd(collector, version, "MAMP");
        foreach (var (home, source) in ToolProbe.VersionManagerInstalls("php"))
            TryAdd(collector, home, source);

        collector.SortNewestFirst(start);
        return collector.Items;
    }

    private static void TryAdd(RuntimeProbe.Collector collector, string? home, string source)
    {
        if (home is not null && File.Exists(Path.Combine(home, "bin", "php")))
            collector.TryAdd(home, h => Describe(h, source));
    }

    private static RuntimeInstallation Describe(string home, string source)
    {
        var resolved = RuntimeProbe.ResolveDirectory(home);
        var version = ToolProbe.QuotedValue(Path.Combine(resolved, "include", "php", "main", "php_version.h"), "#define PHP_VERSION ")
            ?? ToolProbe.FolderVersion(resolved) ?? "Unknown";
        var vendor = resolved.StartsWith("/Applications/MAMP/", StringComparison.Ordinal) ? "MAMP" : ToolProbe.VendorFromPath(resolved);
        return new RuntimeInstallation(version, vendor, resolved, source);
    }
}

/// <summary>
/// Installed Xcode copies (/Applications and ~/Applications, any "Xcode*.app", as Xcodes.app
/// names them) and the Command Line Tools. The one xcode-select points at comes first. Versions
/// come from Contents/version.plist and the Command Line Tools' install receipt.
/// </summary>
public static class XcodeDetector
{
    private const string XcodeSelect = "/usr/bin/xcode-select";
    private const string CommandLineTools = "/Library/Developer/CommandLineTools";
    private const string CltReceipt = "/Library/Apple/System/Library/Receipts/com.apple.pkg.CLTools_Executables.plist";

    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var collector = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsMacOS())
            return collector.Items;

        var apps = new List<string>();
        foreach (var folder in (string[])["/Applications", Path.Combine(AppPaths.Home, "Applications")])
        {
            try
            {
                if (Directory.Exists(folder))
                    apps.AddRange(Directory.GetDirectories(folder, "Xcode*.app").Order(StringComparer.Ordinal));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // ".../Xcode.app/Contents/Developer" or the Command Line Tools folder.
        var selected = RuntimeProbe.LocalFullPath(Processes.TryRun(XcodeSelect, ["-p"])?.Trim());
        if (selected is not null)
        {
            var app = apps.FirstOrDefault(a => selected.StartsWith(a + "/", StringComparison.Ordinal));
            if (app is not null)
                TryAddApp(collector, app, "xcode-select");
            else if (selected.StartsWith(CommandLineTools, StringComparison.Ordinal))
                TryAddCommandLineTools(collector, "xcode-select");
        }
        var start = collector.Items.Count;
        foreach (var app in apps)
            TryAddApp(collector, app, "Applications");
        collector.SortNewestFirst(start);
        TryAddCommandLineTools(collector, "installed");
        return collector.Items;
    }

    /// <summary>Version and build from an Xcode's Contents/version.plist. Public for tests.</summary>
    public static (string Version, string Build) FromVersionPlist(Dictionary<string, object?> plist) =>
        (plist.GetValueOrDefault("CFBundleShortVersionString") as string ?? "Unknown", plist.GetValueOrDefault("ProductBuildVersion") as string ?? "");

    /// <summary>"16.0.0.0.1.1724870825" (the receipt's PackageVersion) → "16.0"; "14.3.1.0.1.1683849156" → "14.3.1". Public for tests.</summary>
    public static string CommandLineToolsVersion(string packageVersion)
    {
        var parts = packageVersion.Split('.').Take(3).ToList();
        while (parts.Count > 2 && parts[^1] == "0")
            parts.RemoveAt(parts.Count - 1);
        return string.Join('.', parts);
    }

    private static void TryAddApp(RuntimeProbe.Collector collector, string app, string source)
    {
        if (Plist.LoadDictionary(Path.Combine(app, "Contents", "version.plist")) is not { } plist)
            return;
        var (version, build) = FromVersionPlist(plist);
        collector.TryAdd(app, h => new RuntimeInstallation(version, build.Length > 0 ? $"Xcode, build {build}" : "Xcode", h, source));
    }

    private static void TryAddCommandLineTools(RuntimeProbe.Collector collector, string source)
    {
        if (!Directory.Exists(Path.Combine(CommandLineTools, "usr", "bin")))
            return;
        var package = Plist.LoadDictionary(CltReceipt)?.GetValueOrDefault("PackageVersion") as string;
        collector.TryAdd(CommandLineTools, h => new RuntimeInstallation(
            package is null ? "Unknown" : CommandLineToolsVersion(package), "Command Line Tools", h, source));
    }
}
