using System.Text.RegularExpressions;

namespace MacosUtils.Core.SystemInfo;

/// <summary>An app bundle on this Mac.</summary>
/// <param name="BundleId">Null when neither Spotlight nor the Info.plist has one.</param>
public sealed record InstalledApp(string Path, string? BundleId)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <param name="SearchedSpotlight">False when Spotlight found nothing (indexing off): only the Applications folders were looked at.</param>
public sealed record InstalledAppList(IReadOnlyList<InstalledApp> Apps, bool SearchedSpotlight);

/// <summary>
/// The app bundles Spotlight knows (anywhere on the Mac) plus those in /Applications and
/// ~/Applications, one level deep, for when Spotlight indexing is off.
/// </summary>
public static partial class InstalledApplications
{
    private const string SpotlightTool = "/usr/bin/mdfind";

    /// <summary>macOS only; empty elsewhere.</summary>
    public static InstalledAppList Find(CancellationToken cancellationToken = default)
    {
        var apps = new List<InstalledApp>();
        if (!OperatingSystem.IsMacOS())
            return new InstalledAppList(apps, false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = Processes.TryRun(SpotlightTool,
            ["-attr", "kMDItemCFBundleIdentifier", "kMDItemContentType == 'com.apple.application-bundle'"], TimeSpan.FromSeconds(30));
        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (path, id) = ParseSpotlightLine(line);
            if (path is not null && seen.Add(path))
                apps.Add(new InstalledApp(path, id ?? InfoPlistBundleId(path)));
        }
        var searchedSpotlight = apps.Count > 0;

        foreach (var folder in new[] { "/Applications", Path.Combine(AppPaths.Home, "Applications") })
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var app in AppBundles(folder).Where(seen.Add))
                apps.Add(new InstalledApp(app, InfoPlistBundleId(app)));
        }
        return new InstalledAppList(apps, searchedSpotlight);
    }

    /// <summary>The app's Contents/Info.plist, or null when it cannot be read.</summary>
    public static Dictionary<string, object?>? InfoPlist(string app) =>
        Plist.LoadDictionary(Path.Combine(app, "Contents", "Info.plist"));

    /// <summary>Splits a line of "mdfind -attr kMDItemCFBundleIdentifier": the app's path and its id (null when Spotlight has none). Public for tests.</summary>
    public static (string? Path, string? BundleId) ParseSpotlightLine(string line)
    {
        var match = SpotlightLine().Match(line);
        if (!match.Success)
            return line.EndsWith(".app", StringComparison.Ordinal) && line.StartsWith('/') ? (line, null) : (null, null);
        var id = match.Groups["id"].Value.Trim().Trim('"');
        return (match.Groups["path"].Value, id.Length == 0 || id == "(null)" ? null : id);
    }

    private static string? InfoPlistBundleId(string app) => InfoPlist(app)?.GetValueOrDefault("CFBundleIdentifier") as string;

    // *.app in a folder and one level below it (/Applications/Utilities, vendor folders).
    private static IEnumerable<string> AppBundles(string folder)
    {
        foreach (var entry in Entries(folder))
        {
            if (entry.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                yield return entry;
            else if (Directory.Exists(entry))
            {
                foreach (var nested in Entries(entry).Where(e => e.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
                    yield return nested;
            }
        }
    }

    private static string[] Entries(string folder)
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

    [GeneratedRegex(@"^(?<path>/.+?\.app)\s+kMDItemCFBundleIdentifier\s*=\s*(?<id>.*)$")]
    private static partial Regex SpotlightLine();
}
