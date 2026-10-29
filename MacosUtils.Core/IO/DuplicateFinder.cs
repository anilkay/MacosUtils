using System.Buffers;
using System.Security.Cryptography;
using MacosUtils.Core.Hashing;

namespace MacosUtils.Core.IO;

/// <summary>A file that belongs to a <see cref="DuplicateGroup"/>.</summary>
public sealed record DuplicateFile(string FullPath, long Size, DateTime Modified);

/// <summary>Files with identical content (same size and SHA-256). Files are sorted oldest first.</summary>
public sealed record DuplicateGroup(string Hash, long Size, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>Bytes that would be freed by keeping only one copy.</summary>
    public long WastedBytes => Size * (Files.Count - 1);
}

public enum DuplicateScanPhase { Scanning, Comparing, Hashing }

/// <summary>Progress reported while searching for duplicates.</summary>
public sealed record DuplicateScanProgress(DuplicateScanPhase Phase, long Done, long Total, string? CurrentPath);

/// <summary>
/// Finds files with identical content. Files are first grouped by size (from the directory listing,
/// no extra I/O), then same-size files are compared by a hash of their first bytes, and only files that
/// still match are fully hashed with SHA-256. Most files are never read at all.
/// Synchronous; callers that need a responsive UI wrap it in <c>Task.Run</c>.
/// </summary>
public static class DuplicateFinder
{
    private const int PrefixLength = 64 * 1024;

    public static (IReadOnlyList<DuplicateGroup> Groups, long Scanned, IReadOnlyList<ScanError> Errors) FindDuplicates(
        IEnumerable<string> roots,
        long minSize = 1,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        minSize = Math.Max(1, minSize); // empty files are all "identical"; never useful

        var errors = new List<ScanError>();
        var lastReport = Environment.TickCount64;

        void Report(DuplicateScanPhase phase, long done, long total, string? path, bool force = false)
        {
            var now = Environment.TickCount64;
            if (progress is not null && (force || now - lastReport > 250))
            {
                progress.Report(new DuplicateScanProgress(phase, done, total, path));
                lastReport = now;
            }
        }

        // ---- 1. Group by size ----
        var bySize = new Dictionary<long, List<DuplicateFile>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // overlapping roots
        long scanned = 0;
        foreach (var root in roots)
        {
            try
            {
                foreach (var (path, size, modified) in FileScanner.EnumerateFiles(root, recursive: true))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanned++;
                    Report(DuplicateScanPhase.Scanning, scanned, 0, Path.GetDirectoryName(path));

                    if (size < minSize || !seen.Add(path))
                        continue;
                    if (!bySize.TryGetValue(size, out var list))
                        bySize[size] = list = [];
                    list.Add(new DuplicateFile(path, size, modified));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new ScanError(root, ex.Message));
            }
        }

        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        bySize.Clear();

        // ---- 2. Narrow same-size files by a hash of their first bytes ----
        var prefixGroups = new List<List<DuplicateFile>>();
        long total = candidates.Sum(l => (long)l.Count);
        long done = 0;
        foreach (var group in candidates)
        {
            if (group[0].Size <= PrefixLength)
            {
                // The prefix would be the whole file; the full hash below is just as cheap.
                prefixGroups.Add(group);
                done += group.Count;
                continue;
            }

            var byPrefix = new Dictionary<string, List<DuplicateFile>>();
            foreach (var file in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                Report(DuplicateScanPhase.Comparing, done, total, file.FullPath);
                var prefix = TryHashPrefix(file.FullPath);
                if (prefix is null)
                    continue; // locked or deleted since the listing
                if (!byPrefix.TryGetValue(prefix, out var list))
                    byPrefix[prefix] = list = [];
                list.Add(file);
            }
            prefixGroups.AddRange(byPrefix.Values.Where(l => l.Count > 1));
        }

        // ---- 3. Confirm with a full SHA-256 ----
        var result = new List<DuplicateGroup>();
        total = prefixGroups.Sum(l => (long)l.Count);
        done = 0;
        foreach (var group in prefixGroups)
        {
            var byHash = new Dictionary<string, List<DuplicateFile>>();
            foreach (var file in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                Report(DuplicateScanPhase.Hashing, done, total, file.FullPath);
                string hash;
                try
                {
                    hash = FileHasher.ComputeHashes(file.FullPath, [HashAlgorithmName.SHA256], cancellationToken)[HashAlgorithmName.SHA256];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (!byHash.TryGetValue(hash, out var list))
                    byHash[hash] = list = [];
                list.Add(file);
            }

            foreach (var (hash, files) in byHash)
            {
                if (files.Count > 1)
                    result.Add(new DuplicateGroup(hash, files[0].Size, files.OrderBy(f => f.Modified).ToList()));
            }
        }

        Report(DuplicateScanPhase.Hashing, done, total, null, force: true);
        result.Sort((a, b) => b.WastedBytes.CompareTo(a.WastedBytes));
        return (result, scanned, errors);
    }

    private static string? TryHashPrefix(string path)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(PrefixLength);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
            var read = stream.ReadAtLeast(buffer.AsSpan(0, PrefixLength), PrefixLength, throwOnEndOfStream: false);
            return Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
