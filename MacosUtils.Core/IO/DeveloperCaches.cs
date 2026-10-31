namespace MacosUtils.Core.IO;

/// <summary>A folder that developer tools fill and rebuild on their own.</summary>
/// <param name="Category">Groups the list: "Xcode", "JavaScript", "Python"...</param>
/// <param name="Note">What happens after it is cleared, or how to clean it instead.</param>
/// <param name="CanTrash">False for folders that must be cleaned with their tool (simulators) or are only reported.</param>
public sealed record CacheLocation(string Category, string Name, string Path, string Note, bool CanTrash = true);

/// <summary>A location found on this Mac, with its size.</summary>
public sealed record CacheItem(CacheLocation Location, long SizeBytes);

/// <summary>
/// Build products, download caches and package stores that Xcode, Homebrew and the language
/// toolchains rebuild or download again: what can go to the Trash to free space. Only folders in
/// the user's home are listed. Folders that macOS privacy protects (other apps' containers, iOS
/// backups, the Trash) are left out, so a scan never raises a permission prompt.
/// </summary>
public static class DeveloperCaches
{
    /// <summary>The known locations under <paramref name="home"/>, in display order. Public for tests.</summary>
    public static IReadOnlyList<CacheLocation> Locations(string home)
    {
        string H(params string[] parts) => System.IO.Path.Combine([home, .. parts]);
        var developer = H("Library", "Developer");
        var caches = H("Library", "Caches");
        return
        [
            new("Xcode", "DerivedData", System.IO.Path.Combine(developer, "Xcode", "DerivedData"),
                "Build products and indexes; Xcode rebuilds them (quit Xcode first)."),
            new("Xcode", "Archives", System.IO.Path.Combine(developer, "Xcode", "Archives"),
                "Archived app builds. Keep the ones you still need for crash symbols or App Store uploads."),
            new("Xcode", "iOS DeviceSupport", System.IO.Path.Combine(developer, "Xcode", "iOS DeviceSupport"),
                "Symbols copied from devices; copied again when a device connects."),
            new("Xcode", "watchOS DeviceSupport", System.IO.Path.Combine(developer, "Xcode", "watchOS DeviceSupport"),
                "Symbols copied from watches; copied again when one connects."),
            new("Xcode", "Xcode caches", System.IO.Path.Combine(caches, "com.apple.dt.Xcode"), "Xcode's download and module caches."),
            new("Xcode", "Simulator caches", System.IO.Path.Combine(developer, "CoreSimulator", "Caches"), "Rebuilt when a simulator starts."),
            new("Xcode", "Simulator devices", System.IO.Path.Combine(developer, "CoreSimulator", "Devices"),
                "Simulators and their apps. Remove them in Xcode › Settings › Components, or run "
                + "\"xcrun simctl delete unavailable\" in Terminal; moving the folder breaks the simulator list.", CanTrash: false),
            new("Swift", "Swift Package Manager", System.IO.Path.Combine(caches, "org.swift.swiftpm"), "Downloaded packages; fetched again on the next build."),
            new("Swift", "CocoaPods", System.IO.Path.Combine(caches, "CocoaPods"), "Downloaded pods; fetched again by pod install."),
            new("Homebrew", "Homebrew downloads", System.IO.Path.Combine(caches, "Homebrew"),
                "Bottles and casks already installed; \"brew cleanup\" also removes old versions."),
            new("JavaScript", "npm cache", H(".npm", "_cacache"), "Downloaded packages; npm fetches them again."),
            new("JavaScript", "Yarn cache", System.IO.Path.Combine(caches, "Yarn"), "Downloaded packages; Yarn fetches them again."),
            new("JavaScript", "Bun cache", H(".bun", "install", "cache"), "Downloaded packages; Bun fetches them again."),
            new("Python", "pip cache", System.IO.Path.Combine(caches, "pip"), "Downloaded wheels; pip fetches them again."),
            new("Python", "uv cache", H(".cache", "uv"), "Downloaded packages and built wheels; uv fetches them again."),
            new(".NET", "NuGet packages", H(".nuget", "packages"), "Restored packages; dotnet restore downloads them again."),
            new("Java", "Gradle caches", H(".gradle", "caches"), "Dependencies and build caches; Gradle downloads them again."),
            new("Java", "Maven repository", H(".m2", "repository"), "Downloaded artifacts; Maven downloads them again."),
            new("Go", "Go build cache", System.IO.Path.Combine(caches, "go-build"), "Compiled packages; go build rebuilds them."),
            new("Rust", "Cargo registry", H(".cargo", "registry"), "Downloaded crates; Cargo fetches them again."),
        ];
    }

    /// <summary>The locations that exist on this Mac with their sizes, largest first.</summary>
    public static IReadOnlyList<CacheItem> Scan(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var items = new List<CacheItem>();
        if (!OperatingSystem.IsMacOS())
            return items;
        foreach (var location in Locations(AppPaths.Home))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRealDirectory(location.Path))
                continue;
            progress?.Report(location.Name);
            items.Add(new CacheItem(location, FileScanner.GetDirectorySize(location.Path, cancellationToken)));
        }
        items.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        return items;
    }

    /// <summary>
    /// Moves one known location to the Trash. Anything that is not on the list for this home
    /// folder, is marked report-only, or is a symlink (which could point anywhere) is refused.
    /// </summary>
    public static void MoveToTrash(string path)
    {
        var location = Locations(AppPaths.Home).FirstOrDefault(l => l.Path == path)
            ?? throw new InvalidOperationException("Only the listed cache folders can be moved to the Trash here.");
        if (!location.CanTrash)
            throw new InvalidOperationException($"{location.Name} is cleaned with its own tool: {location.Note}");
        if (!IsRealDirectory(path))
            throw new IOException($"{location.Name} is not a folder (or is a link) and was left alone.");
        Trash.MoveToTrash(path);
    }

    private static bool IsRealDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.LinkTarget is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
