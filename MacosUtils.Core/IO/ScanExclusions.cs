namespace MacosUtils.Core.IO;

/// <summary>
/// Folders a scan must not descend into on macOS:
/// <list type="bullet">
/// <item>/System/Volumes holds the firmlinked Data volume (/System/Volumes/Data/Users is /Users),
/// so scanning "/" would count every user file twice.</item>
/// <item>/dev, /private/var/vm (swap files) and /.Spotlight-V100, /.fseventsd are system internals.</item>
/// <item>/net, /Network and /home are autofs mount points: looking inside /net/&lt;name&gt; makes macOS
/// mount an NFS export from that host, so a scan (or an AI tool path) would reach other machines.</item>
/// <item>/Volumes is scanned as its own roots ("All volumes"), never through "/".</item>
/// </list>
/// Network file systems mounted elsewhere are refused by <see cref="Volumes.IsNetworkPath"/>.
/// </summary>
public static class ScanExclusions
{
    private static readonly string[] SkippedRoots =
    [
        "/System/Volumes",
        "/dev",
        "/net",
        "/Network",
        "/home",
        "/Volumes",
        "/private/var/vm",
        "/.Spotlight-V100",
        "/.fseventsd",
        "/.DocumentRevisions-V100",
    ];

    /// <summary>True for a directory that a recursive scan should not enter.</summary>
    public static bool ShouldSkip(string fullPath)
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        var path = fullPath.TrimEnd('/');
        foreach (var root in SkippedRoots)
        {
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // A network share mounted anywhere else (e.g. /Volumes/share from Finder's Connect to Server).
        return Volumes.IsNetworkMountPoint(path);
    }
}
