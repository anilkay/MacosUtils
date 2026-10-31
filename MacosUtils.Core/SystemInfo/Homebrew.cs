using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using MacosUtils.Core.IO;

namespace MacosUtils.Core.SystemInfo;

/// <summary>A Homebrew installation: /opt/homebrew on Apple silicon, /usr/local on Intel (or under Rosetta 2).</summary>
/// <param name="Version">e.g. "4.6.3", or "" when it could not be read.</param>
/// <param name="Architecture">"Apple silicon" or "Intel".</param>
/// <param name="CacheBytes">Size of the download cache, ~/Library/Caches/Homebrew (shared by both prefixes).</param>
public sealed record HomebrewInstallation(string Prefix, string Version, string Architecture, int Formulae, int Casks, long CacheBytes)
{
    /// <summary>An Intel Homebrew on an Apple silicon Mac: everything it installs runs under Rosetta 2.</summary>
    public bool UnderRosetta => Architecture == "Intel" && RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
}

public enum HomebrewKind { Formula, Cask }

/// <summary>An installed formula (a keg in Cellar) or cask (in Caskroom).</summary>
/// <param name="Versions">Installed versions, newest last; more than one until "brew cleanup" runs.</param>
/// <param name="InstalledOnRequest">True when the user asked for it, false when pulled in as a dependency, null for casks or without a receipt.</param>
/// <param name="Pinned">"brew pin" keeps it from being upgraded.</param>
/// <param name="SizeBytes">Size on disk: every installed version of a keg; a cask's apps in /Applications when they can be found.</param>
/// <param name="Path">The Cellar or Caskroom folder.</param>
public sealed record HomebrewPackage(
    string Name,
    HomebrewKind Kind,
    IReadOnlyList<string> Versions,
    bool? InstalledOnRequest,
    DateTime? InstalledAt,
    bool Pinned,
    long SizeBytes,
    string Prefix,
    string Path)
{
    public string Version => Versions.Count > 0 ? Versions[^1] : "";
}

/// <summary>
/// Reads Homebrew's own folders (Cellar, Caskroom, var/homebrew, each keg's INSTALL_RECEIPT.json)
/// instead of running brew: brew is Ruby, takes seconds to start and may decide to auto-update.
/// Only "brew --version" is run, and only when the Command Line Tools are installed, because it
/// calls git and /usr/bin/git without them opens Apple's install dialog. Read-only.
/// </summary>
public static class Homebrew
{
    private const string XcodeSelect = "/usr/bin/xcode-select";

    /// <summary>Apple silicon prefix first, then Intel.</summary>
    public static IReadOnlyList<string> Prefixes => RuntimeProbe.HomebrewPrefixes;

    public static string CacheFolder => System.IO.Path.Combine(AppPaths.Home, "Library", "Caches", "Homebrew");

    /// <summary>Prefixes with a brew in bin/; empty when Homebrew is not installed.</summary>
    public static IReadOnlyList<string> InstalledPrefixes() =>
        OperatingSystem.IsMacOS() ? Prefixes.Where(p => File.Exists(System.IO.Path.Combine(p, "bin", "brew"))).ToList() : [];

    public static IReadOnlyList<HomebrewInstallation> GetInstallations(CancellationToken cancellationToken = default)
    {
        var prefixes = InstalledPrefixes();
        if (prefixes.Count == 0)
            return [];
        var canRunBrew = !string.IsNullOrWhiteSpace(Processes.TryRun(XcodeSelect, ["-p"]));
        var cache = Directory.Exists(CacheFolder) ? FileScanner.GetDirectorySize(CacheFolder, cancellationToken) : 0;
        return prefixes.Select(prefix =>
        {
            var version = canRunBrew
                ? ParseVersion(Processes.TryRun(System.IO.Path.Combine(prefix, "bin", "brew"), ["--version"], TimeSpan.FromSeconds(10)) ?? "")
                : "";
            return new HomebrewInstallation(
                prefix,
                version,
                prefix == "/opt/homebrew" ? "Apple silicon" : "Intel",
                CountChildren(System.IO.Path.Combine(prefix, "Cellar")),
                CountChildren(System.IO.Path.Combine(prefix, "Caskroom")),
                cache);
        }).ToList();
    }

    /// <summary>Every formula and cask of every installation, by name. Sizes take a moment on large Cellars.</summary>
    public static IReadOnlyList<HomebrewPackage> GetPackages(bool includeSizes = true, CancellationToken cancellationToken = default)
    {
        var packages = new List<HomebrewPackage>();
        foreach (var prefix in InstalledPrefixes())
        {
            var pinned = System.IO.Path.Combine(prefix, "var", "homebrew", "pinned");
            foreach (var keg in Children(System.IO.Path.Combine(prefix, "Cellar")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var versions = Children(keg).Select(System.IO.Path.GetFileName).OfType<string>().ToList();
                if (versions.Count == 0)
                    continue;
                versions.Sort(CompareVersions);
                var receipt = ReadText(System.IO.Path.Combine(keg, versions[^1], "INSTALL_RECEIPT.json"));
                var (onRequest, installedAt) = receipt is null ? (null, null) : ParseReceipt(receipt);
                var name = System.IO.Path.GetFileName(keg);
                packages.Add(new HomebrewPackage(name, HomebrewKind.Formula, versions, onRequest,
                    installedAt ?? CreationTime(System.IO.Path.Combine(keg, versions[^1])),
                    IsLink(System.IO.Path.Combine(pinned, name)),
                    includeSizes ? FileScanner.GetDirectorySize(keg, cancellationToken) : 0,
                    prefix, keg));
            }

            foreach (var cask in Children(System.IO.Path.Combine(prefix, "Caskroom")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var versions = Children(cask).Select(System.IO.Path.GetFileName).OfType<string>()
                    .Where(v => !v.StartsWith('.')).ToList();
                if (versions.Count == 0)
                    continue;
                versions.Sort(CompareVersions);
                var name = System.IO.Path.GetFileName(cask);
                long size = 0;
                if (includeSizes)
                {
                    var apps = FindCaskApps(cask, name).Select(a => System.IO.Path.Combine("/Applications", a)).Where(Directory.Exists).ToList();
                    size = apps.Count > 0
                        ? apps.Sum(a => FileScanner.GetDirectorySize(a, cancellationToken))
                        : FileScanner.GetDirectorySize(cask, cancellationToken);
                }
                packages.Add(new HomebrewPackage(name, HomebrewKind.Cask, versions, null,
                    CreationTime(System.IO.Path.Combine(cask, versions[^1])), false, size, prefix, cask));
            }
        }
        packages.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return packages;
    }

    /// <summary>"Homebrew 4.6.3" (first line of brew --version) → "4.6.3". Public for tests.</summary>
    public static string ParseVersion(string output)
    {
        var first = output.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return first.StartsWith("Homebrew ", StringComparison.Ordinal) ? first["Homebrew ".Length..].Trim() : "";
    }

    /// <summary>
    /// A keg's INSTALL_RECEIPT.json: <c>installed_on_request</c> and <c>time</c> (Unix seconds).
    /// Missing or malformed fields come back null. Public for tests.
    /// </summary>
    public static (bool? OnRequest, DateTime? InstalledAt) ParseReceipt(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);
            bool? onRequest = root.TryGetProperty("installed_on_request", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? r.GetBoolean()
                : null;
            DateTime? time = root.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var seconds) && seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
                : null;
            return (onRequest, time);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The app bundles a cask installs, from the cask definition Homebrew keeps in
    /// Caskroom/&lt;cask&gt;/.metadata/&lt;version&gt;/&lt;timestamp&gt;/Casks/&lt;cask&gt;.json. An "app" artifact is
    /// either "Foo.app" or ["Foo.app", {"target": "Bar.app"}]. Public for tests.
    /// </summary>
    public static IReadOnlyList<string> ParseCaskApps(string json)
    {
        var apps = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
                return apps;
            foreach (var artifact in artifacts.EnumerateArray())
            {
                if (artifact.ValueKind != JsonValueKind.Object || !artifact.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.Array)
                    continue;
                string? source = null, target = null;
                foreach (var part in app.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                        source ??= part.GetString();
                    else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String)
                        target = t.GetString();
                }
                // Only a bare bundle name: a target such as "~/Applications/x.app" or "../" is not looked up.
                var name = System.IO.Path.GetFileName(target ?? source ?? "");
                if (name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && name == (target ?? source))
                    apps.Add(name);
            }
        }
        catch (JsonException) { }
        return apps;
    }

    /// <summary>Orders versions by their numeric parts ("1.10" after "1.9", "2.0_1" after "2.0"). Public for tests.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var left = Parts(a ?? "");
        var right = Parts(b ?? "");
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : "";
            var y = i < right.Length ? right[i] : "";
            var c = long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var nx)
                    && long.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var ny)
                ? nx.CompareTo(ny)
                : string.CompareOrdinal(x, y);
            if (c != 0)
                return c;
        }
        return 0;

        static string[] Parts(string v) => v.Split(['.', '_', '-', ','], StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<string> FindCaskApps(string caskFolder, string name)
    {
        var metadata = System.IO.Path.Combine(caskFolder, ".metadata");
        try
        {
            var definition = Directory.Exists(metadata)
                ? Directory.EnumerateFiles(metadata, name + ".json", SearchOption.AllDirectories).Max(StringComparer.Ordinal)
                : null;
            return definition is not null && ReadText(definition) is { } json ? ParseCaskApps(json) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> Children(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder).Order(StringComparer.Ordinal).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int CountChildren(string folder) => Children(folder).Count;

    private static string? ReadText(string path)
    {
        try
        {
            // Receipts and cask definitions are a few KB; anything far larger is not one.
            return File.Exists(path) && new FileInfo(path).Length < 4 * 1024 * 1024 ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime? CreationTime(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetCreationTime(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
