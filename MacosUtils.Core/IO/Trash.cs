using MacosUtils.Core.Interop;

namespace MacosUtils.Core.IO;

/// <summary>
/// Moves files to the Trash with NSFileManager trashItemAtURL:resultingItemURL:error:, the call
/// Finder uses, so "Put Back" works afterwards. Nothing is ever deleted permanently: a volume
/// without a Trash (network shares, some USB disks) makes the call fail and the file stays.
/// macOS only.
/// </summary>
public static class Trash
{
    /// <summary>Moves <paramref name="path"/> to the Trash; throws <see cref="IOException"/> when it cannot.</summary>
    public static void MoveToTrash(string path)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Moving files to the Trash is only available on macOS.");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("The file no longer exists.", full);

        using var pool = new ObjC.AutoreleasePool();
        var url = ObjC.FileUrl(full);
        var manager = ObjC.Send(ObjC.Class("NSFileManager"), ObjC.Selector("defaultManager"));
        var ok = ObjC.SendBool(manager, ObjC.Selector("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, out var error);
        if (!ok)
            throw new IOException($"Could not move the file to the Trash: {ObjC.ErrorDescription(error)}");
    }
}

/// <summary>Opens Finder at a file. Starts /usr/bin/open by its full path, never through PATH.</summary>
public static class Finder
{
    private const string OpenTool = "/usr/bin/open";

    /// <summary>Shows <paramref name="path"/> selected in a Finder window.</summary>
    public static void Reveal(string path)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Finder is only available on macOS.");
        Processes.Start(OpenTool, ["-R", Path.GetFullPath(path)]);
    }

    /// <summary>Opens a folder in Finder (or a file in its default app).</summary>
    public static void Open(string path)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Finder is only available on macOS.");
        Processes.Start(OpenTool, [Path.GetFullPath(path)]);
    }
}
