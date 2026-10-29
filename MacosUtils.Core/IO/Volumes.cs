namespace MacosUtils.Core.IO;

/// <summary>A mounted volume as shown on the Disk Info screen.</summary>
public sealed record VolumeInfo(
    string MountPoint,
    string Name,
    string FileSystem,
    bool IsNetwork,
    bool IsRemovable,
    long TotalBytes,
    long FreeBytes)
{
    public double FreePercent => TotalBytes > 0 ? FreeBytes * 100.0 / TotalBytes : 0;
}

/// <summary>
/// Mounted volumes and the checks that keep scans and AI tool paths on this Mac.
/// <see cref="DriveInfo.GetDrives"/> on macOS lists every mount (devfs, autofs, the hidden
/// /System/Volumes/* APFS volumes...); <see cref="GetVolumes"/> keeps the ones a user thinks of
/// as disks: the startup volume "/" and everything under /Volumes.
/// </summary>
public static class Volumes
{
    // File systems that live on another machine. Touching a path on them sends requests (and for
    // smbfs possibly credentials from the Keychain) to that host.
    private static readonly HashSet<string> NetworkFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "smbfs", "nfs", "afpfs", "webdav", "cifs", "ftp", "autofs", "macfuse", "osxfuse", "sshfs",
    };

    private static readonly object CacheLock = new();
    private static (long Tick, HashSet<string> Mounts) _networkMounts = (long.MinValue, []);

    public static IReadOnlyList<VolumeInfo> GetVolumes()
    {
        var result = new List<VolumeInfo>();
        foreach (var drive in SafeDrives())
        {
            var mount = drive.Name.Length > 1 ? drive.Name.TrimEnd('/') : drive.Name;
            if (OperatingSystem.IsMacOS() && mount != "/" && !mount.StartsWith("/Volumes/", StringComparison.Ordinal))
                continue;
            try
            {
                if (!drive.IsReady)
                    continue;
                var format = drive.DriveFormat;
                var name = mount == "/" ? StartupVolumeName() : Path.GetFileName(mount);
                result.Add(new VolumeInfo(
                    mount,
                    name,
                    format,
                    NetworkFileSystems.Contains(format) || drive.DriveType == DriveType.Network,
                    drive.DriveType == DriveType.Removable,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ejected while listing, or not readable: leave it out.
            }
        }
        return result;
    }

    /// <summary>Local volumes worth scanning for "All volumes": the startup disk plus attached local disks.</summary>
    public static IReadOnlyList<string> LocalScanRoots() =>
        GetVolumes().Where(v => !v.IsNetwork).Select(v => v.MountPoint).ToList();

    /// <summary>
    /// True when <paramref name="fullPath"/> lies on a network file system or an automount point.
    /// Lexical checks (/net, /Network, /home) run first so no file-system call happens for them.
    /// </summary>
    public static bool IsNetworkPath(string fullPath)
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        foreach (var autofs in (string[])["/net", "/Network", "/home"])
        {
            if (fullPath.Equals(autofs, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(autofs + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var mounts = NetworkMounts();
        foreach (var mount in mounts)
        {
            if (fullPath.Equals(mount, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(mount + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="path"/> is itself the mount point of a network share.</summary>
    public static bool IsNetworkMountPoint(string path) =>
        OperatingSystem.IsMacOS() && NetworkMounts().Contains(path.TrimEnd('/'));

    // Mounts change rarely; a scan asks for every directory, so the list is cached for a few seconds.
    private static HashSet<string> NetworkMounts()
    {
        lock (CacheLock)
        {
            var now = Environment.TickCount64;
            if (now - _networkMounts.Tick < 5000)
                return _networkMounts.Mounts;
            var mounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in SafeDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Network || NetworkFileSystems.Contains(drive.DriveFormat))
                        mounts.Add(drive.Name.Length > 1 ? drive.Name.TrimEnd('/') : drive.Name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            _networkMounts = (now, mounts);
            return mounts;
        }
    }

    private static DriveInfo[] SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // "/" has no folder name; Finder shows the startup disk by its volume name.
    private static string StartupVolumeName()
    {
        if (!OperatingSystem.IsMacOS())
            return "/";
        try
        {
            // /Volumes/<startup name> is a symlink back to "/".
            foreach (var entry in Directory.EnumerateFileSystemEntries("/Volumes"))
            {
                var info = new DirectoryInfo(entry);
                if (info.LinkTarget == "/")
                    return info.Name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return "Macintosh HD";
    }
}
