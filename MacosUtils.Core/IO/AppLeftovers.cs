using System.Text.RegularExpressions;

namespace MacosUtils.Core.IO;

/// <summary>A folder in ~/Library where apps keep files named after their bundle id.</summary>
/// <param name="Protected">
/// macOS privacy protects other apps' containers: they are listed by name only, never opened,
/// measured or moved, so a scan raises no permission prompt.
/// </param>
public sealed record LeftoverPlace(string Name, string Path, bool Protected = false);

/// <summary>One file or folder an app left behind.</summary>
/// <param name="SizeBytes">0 for protected places, which are not measured.</param>
/// <param name="LastWriteUtc">DateTime.MinValue for protected places.</param>
public sealed record LeftoverItem(LeftoverPlace Place, string Path, long SizeBytes, DateTime LastWriteUtc);

/// <summary>Everything one app that is no longer installed left in ~/Library.</summary>
/// <param name="BundleId">The app's id; helpers (com.foo.app.helper) are grouped under it.</param>
public sealed record AppLeftover(string BundleId, IReadOnlyList<LeftoverItem> Items)
{
    public long SizeBytes => Items.Sum(i => i.SizeBytes);

    public DateTime LastWriteUtc => Items.Max(i => i.LastWriteUtc);

    /// <summary>False when everything is in a protected place (only Finder can remove it).</summary>
    public bool CanTrash => Items.Any(i => !i.Place.Protected);

    public string Places => string.Join(", ", Items.Select(i => i.Place.Name).Distinct());
}

/// <summary>The apps found on this Mac, which the leftovers are compared with.</summary>
/// <param name="SearchedSpotlight">False when Spotlight found nothing (indexing off): only the Applications folders were looked at.</param>
public sealed record InstalledApps(IReadOnlySet<string> BundleIds, int Count, bool SearchedSpotlight);

public sealed record LeftoverScan(IReadOnlyList<AppLeftover> Apps, InstalledApps Installed);

/// <summary>
/// Files that deleted apps leave in ~/Library (Application Support, Caches, Preferences,
/// containers...). Only entries named after a bundle id are considered, and an entry is a
/// leftover only when no installed app comes from the same vendor (com.google.*, us.zoom.*):
/// a deleted Chrome's data stays hidden while Google Drive is installed. Apple's own entries,
/// shared frameworks (Sparkle, Sentry) and the developer caches are never listed.
/// </summary>
public static partial class AppLeftovers
{
    private const string SpotlightTool = "/usr/bin/mdfind";

    // Vendors whose files are system parts or frameworks shared by many apps, not one app's data.
    private static readonly string[] SharedVendors =
        ["com.apple", "org.cups", "org.sparkle-project", "com.plausiblelabs", "io.sentry", "com.crashlytics", "org.swift"];

    // File names add these to the bundle id: com.foo.app.plist, com.foo.app.savedState.
    private static readonly string[] Suffixes = [".plist", ".savedState", ".binarycookies"];

    /// <summary>The places looked at under <paramref name="home"/>. Public for tests.</summary>
    public static IReadOnlyList<LeftoverPlace> Places(string home)
    {
        var library = Path.Combine(home, "Library");
        return
        [
            new("Application Support", Path.Combine(library, "Application Support")),
            new("Caches", Path.Combine(library, "Caches")),
            new("Preferences", Path.Combine(library, "Preferences")),
            new("Saved Application State", Path.Combine(library, "Saved Application State")),
            new("HTTPStorages", Path.Combine(library, "HTTPStorages")),
            new("WebKit", Path.Combine(library, "WebKit")),
            new("Logs", Path.Combine(library, "Logs")),
            new("Containers", Path.Combine(library, "Containers"), Protected: true),
            new("Group Containers", Path.Combine(library, "Group Containers"), Protected: true),
        ];
    }

    /// <summary>Compares ~/Library with the installed apps; sizes are measured. macOS only (empty elsewhere).</summary>
    public static LeftoverScan Scan(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            return new LeftoverScan([], new InstalledApps(new HashSet<string>(), 0, false));
        progress?.Report("installed apps");
        var installed = FindInstalledApps(cancellationToken);
        return new LeftoverScan(Find(AppPaths.Home, installed.BundleIds, measure: true, progress, cancellationToken), installed);
    }

    /// <summary>
    /// The leftovers under <paramref name="home"/> for apps not in <paramref name="installedBundleIds"/>,
    /// largest first. Public for tests, which pass a temporary home.
    /// </summary>
    public static IReadOnlyList<AppLeftover> Find(string home, IEnumerable<string> installedBundleIds, bool measure = true,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var installedVendors = installedBundleIds.Select(Vendor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var developerCaches = DeveloperCaches.Locations(home).Select(l => l.Path).ToHashSet(StringComparer.Ordinal);
        var found = new List<(string Id, LeftoverItem Item)>();
        foreach (var place in Places(home))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in EntryNames(place.Path))
            {
                var id = BundleIdFromEntry(place.Name, Path.GetFileName(path));
                if (id is null || IsShared(id) || installedVendors.Contains(Vendor(id)) || developerCaches.Contains(path))
                    continue;
                if (place.Protected)
                {
                    found.Add((id, new LeftoverItem(place, path, 0, DateTime.MinValue)));
                    continue;
                }
                var info = Describe(path);
                if (info is null)
                    continue;
                progress?.Report(id);
                var size = !measure ? 0 : info is DirectoryInfo ? FileScanner.GetDirectorySize(path, cancellationToken) : ((FileInfo)info).Length;
                found.Add((id, new LeftoverItem(place, path, size, info.LastWriteTimeUtc)));
            }
        }

        // A helper's entries (com.foo.app.helper) go under the app's (com.foo.app) when both are there.
        var keys = new List<string>();
        foreach (var id in found.Select(f => f.Id).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(i => i.Length))
        {
            if (!keys.Any(k => BelongsTo(id, k)))
                keys.Add(id);
        }
        return found
            .GroupBy(f => keys.First(k => BelongsTo(f.Id, k)), StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppLeftover(g.Key, g.Select(f => f.Item).OrderBy(i => i.Place.Name, StringComparer.Ordinal).ToList()))
            .OrderByDescending(a => a.SizeBytes)
            .ThenBy(a => a.BundleId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Moves the leftovers of the given apps to the Trash. Nothing from the caller is trusted: the
    /// installed apps and ~/Library are scanned again, and only what is still a leftover of one of
    /// these ids, outside the protected places, is moved. Returns how many items moved and what was kept.
    /// </summary>
    public static (int Moved, IReadOnlyList<string> Kept) MoveToTrash(IReadOnlyCollection<string> bundleIds, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Moving files to the Trash is only available on macOS.");
        var installed = FindInstalledApps(cancellationToken);
        var leftovers = Find(AppPaths.Home, installed.BundleIds, measure: false, cancellationToken: cancellationToken)
            .ToDictionary(a => a.BundleId, StringComparer.OrdinalIgnoreCase);
        var moved = 0;
        var kept = new List<string>();
        foreach (var id in bundleIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!leftovers.TryGetValue(id, out var app))
            {
                kept.Add($"{id}: not a leftover any more (the app is installed, or the files are gone).");
                continue;
            }
            foreach (var item in app.Items)
            {
                if (item.Place.Protected)
                {
                    kept.Add($"{item.Path}: macOS protects {item.Place.Name}; drag it to the Trash in Finder.");
                    continue;
                }
                try
                {
                    Trash.MoveToTrash(item.Path);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    kept.Add($"{item.Path}: {ex.Message}");
                }
            }
        }
        return (moved, kept);
    }

    /// <summary>Bundle ids of the apps Spotlight knows plus those in /Applications and ~/Applications.</summary>
    public static InstalledApps FindInstalledApps(CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new HashSet<string>(StringComparer.Ordinal);
        var output = Processes.TryRun(SpotlightTool,
            ["-attr", "kMDItemCFBundleIdentifier", "kMDItemContentType == 'com.apple.application-bundle'"], TimeSpan.FromSeconds(30));
        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (path, id) = ParseSpotlightLine(line);
            if (path is null || !apps.Add(path))
                continue;
            id ??= InfoPlistBundleId(path);
            if (id is not null)
                ids.Add(id);
        }
        var searchedSpotlight = apps.Count > 0;

        var home = AppPaths.Home;
        foreach (var folder in new[] { "/Applications", Path.Combine(home, "Applications") })
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var app in AppBundles(folder).Where(apps.Add))
            {
                if (InfoPlistBundleId(app) is { } id)
                    ids.Add(id);
            }
        }
        return new InstalledApps(ids, apps.Count, searchedSpotlight);
    }

    /// <summary>
    /// The bundle id an entry of ~/Library is named after, or null when the name is not one
    /// (com.foo.app.plist → com.foo.app; "Slack" → null). Group containers start with a team id
    /// or "group.", which are dropped. Public for tests.
    /// </summary>
    public static string? BundleIdFromEntry(string placeName, string entryName)
    {
        if (entryName.StartsWith('.'))
            return null;
        var name = entryName;
        foreach (var suffix in Suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }
        if (placeName == "Group Containers")
        {
            name = TeamIdPrefix().Replace(name, "");
            if (name.StartsWith("group.", StringComparison.OrdinalIgnoreCase))
                name = name["group.".Length..];
        }
        return BundleIdPattern().IsMatch(name) ? name : null;
    }

    /// <summary>The first two parts of a bundle id: com.google.Chrome → com.google.</summary>
    public static string Vendor(string bundleId)
    {
        var first = bundleId.IndexOf('.');
        var second = first < 0 ? -1 : bundleId.IndexOf('.', first + 1);
        return second < 0 ? bundleId : bundleId[..second];
    }

    /// <summary>Splits a line of "mdfind -attr kMDItemCFBundleIdentifier": the app's path and its id (null when Spotlight has none). Public for tests.</summary>
    public static (string? Path, string? BundleId) ParseSpotlightLine(string line)
    {
        var match = SpotlightLine().Match(line);
        if (!match.Success)
            return line.EndsWith(".app", StringComparison.Ordinal) && line.StartsWith('/') ? (line, null) : (null, null);
        var id = match.Groups["id"].Value.Trim().Trim('"');
        return (match.Groups["path"].Value, id.Length == 0 || id == "(null)" ? null : id);
    }

    private static bool IsShared(string id)
    {
        var vendor = Vendor(id);
        return SharedVendors.Any(v => vendor.Equals(v, StringComparison.OrdinalIgnoreCase));
    }

    private static bool BelongsTo(string id, string key) =>
        id.Equals(key, StringComparison.OrdinalIgnoreCase) || id.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase);

    // Names only: protected places must not be opened beyond listing them.
    private static IEnumerable<string> EntryNames(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFileSystemEntries(folder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // A file or a real folder; links (which could point anywhere) are skipped.
    private static FileSystemInfo? Describe(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.Exists && info.LinkTarget is null ? info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // *.app in a folder and one level below it (/Applications/Utilities, vendor folders).
    private static IEnumerable<string> AppBundles(string folder)
    {
        foreach (var entry in EntryNames(folder))
        {
            if (entry.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                yield return entry;
            else if (Directory.Exists(entry))
            {
                foreach (var nested in EntryNames(entry).Where(e => e.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
                    yield return nested;
            }
        }
    }

    private static string? InfoPlistBundleId(string app) =>
        Plist.ReadTopLevelStrings(Path.Combine(app, "Contents", "Info.plist")).GetValueOrDefault("CFBundleIdentifier");

    // At least three parts and a domain-like first part: com.foo.app, us.zoom.xos. "notion.id" and "Office" are not matched.
    [GeneratedRegex(@"^[A-Za-z]{2,}(\.[A-Za-z0-9_-]+){2,}$")]
    private static partial Regex BundleIdPattern();

    [GeneratedRegex(@"^[A-Z0-9]{10}\.")]
    private static partial Regex TeamIdPrefix();

    [GeneratedRegex(@"^(?<path>/.+?\.app)\s+kMDItemCFBundleIdentifier\s*=\s*(?<id>.*)$")]
    private static partial Regex SpotlightLine();
}
