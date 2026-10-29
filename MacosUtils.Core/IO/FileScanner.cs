using System.IO.Enumeration;

namespace MacosUtils.Core.IO;

/// <summary>A file found by <see cref="FileScanner.FindLargestFiles"/>.</summary>
public sealed record LargestFileEntry(string FullPath, long Size, DateTime Modified);

/// <summary>A direct child of a folder, from <see cref="FileScanner.GetFolderContents"/>.</summary>
public sealed record FolderItem(string Name, bool IsFolder, long Size);

/// <summary>Progress reported while scanning.</summary>
public sealed record ScanProgress(long Scanned, string? CurrentDirectory);

/// <summary>A scan root that could not be read (missing, access denied, drive removed mid-scan).</summary>
public sealed record ScanError(string Root, string Message);

/// <summary>
/// Safe, cancellable file-system scanning. Skips inaccessible paths, symbolic links and the macOS
/// system folders that would count files twice or reach other machines (<see cref="ScanExclusions"/>),
/// so scans never crash, loop or wander onto network mounts.
/// Methods are synchronous; callers that need a responsive UI wrap them in <c>Task.Run</c>.
/// </summary>
public static class FileScanner
{
    private static EnumerationOptions CreateOptions(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    // Size and timestamp come straight from the directory listing, so no extra stat call per file.
    internal static FileSystemEnumerable<(string Path, long Size, DateTime Modified)> EnumerateFiles(string directory, bool recursive) =>
        new(directory,
            (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.LocalDateTime),
            CreateOptions(recursive))
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !ScanExclusions.ShouldSkip(entry.ToFullPath()),
        };

    /// <summary>
    /// Finds the <paramref name="maxResults"/> largest files under <paramref name="roots"/>, sorted largest first.
    /// A root that cannot be read is skipped and reported in <c>Errors</c>; the other roots are still scanned.
    /// </summary>
    public static (IReadOnlyList<LargestFileEntry> Files, long Scanned, IReadOnlyList<ScanError> Errors) FindLargestFiles(
        IEnumerable<string> roots,
        int maxResults,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool recursive = true)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        // Min-heap by size: keeps only the N largest files seen so far (constant memory).
        var heap = new PriorityQueue<LargestFileEntry, long>();
        var errors = new List<ScanError>();

        long scanned = 0;
        var lastReport = Environment.TickCount64;

        foreach (var root in roots)
        {
            try
            {
                foreach (var (path, size, modified) in EnumerateFiles(root, recursive))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    scanned++;

                    if (heap.Count < maxResults)
                    {
                        heap.Enqueue(new LargestFileEntry(path, size, modified), size);
                    }
                    else if (size > heap.Peek().Size)
                    {
                        heap.Dequeue();
                        heap.Enqueue(new LargestFileEntry(path, size, modified), size);
                    }

                    var now = Environment.TickCount64;
                    if (progress is not null && now - lastReport > 250)
                    {
                        progress.Report(new ScanProgress(scanned, Path.GetDirectoryName(path)));
                        lastReport = now;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new ScanError(root, ex.Message));
            }
        }

        var result = new List<LargestFileEntry>(heap.Count);
        while (heap.Count > 0)
            result.Add(heap.Dequeue());
        result.Reverse(); // min-heap yields ascending -> flip to descending
        return (result, scanned, errors);
    }

    /// <summary>
    /// Direct children of <paramref name="directory"/> with their sizes (folders include everything below them),
    /// sorted largest first.
    /// </summary>
    public static IReadOnlyList<FolderItem> GetFolderContents(string directory, CancellationToken cancellationToken = default)
    {
        var items = new List<FolderItem>();
        foreach (var subdirectory in Directory.EnumerateDirectories(directory, "*", CreateOptions(recursive: false)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ScanExclusions.ShouldSkip(subdirectory))
                continue;
            items.Add(new FolderItem(Path.GetFileName(subdirectory), true, GetDirectorySize(subdirectory, cancellationToken)));
        }
        foreach (var (path, size, _) in EnumerateFiles(directory, recursive: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(new FolderItem(Path.GetFileName(path), false, size));
        }
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        return items;
    }

    /// <summary>Total size of all files under <paramref name="directory"/>, recursively. Inaccessible files are skipped.</summary>
    public static long GetDirectorySize(string directory, CancellationToken cancellationToken = default)
    {
        long total = 0;
        try
        {
            foreach (var (_, size, _) in EnumerateFiles(directory, recursive: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += size;
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return total;
    }
}
