using System.Buffers;
using System.Security.Cryptography;

namespace MacosUtils.Core.Hashing;

/// <summary>
/// Computes file hashes in a single streaming pass over the file.
/// UI-agnostic: usable from WinForms, console apps, services, etc.
/// </summary>
public static class FileHasher
{
    public static IReadOnlyDictionary<HashAlgorithmName, string> ComputeHashes(
        string path,
        IEnumerable<HashAlgorithmName> algorithms,
        CancellationToken cancellationToken = default)
    {
        var selected = algorithms.Distinct().ToList();
        if (selected.Count == 0)
            throw new ArgumentException("Select at least one hash algorithm.", nameof(algorithms));

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        var instances = new List<(HashAlgorithmName Algorithm, IncrementalHash Instance)>(selected.Count);
        try
        {
            foreach (var algorithm in selected)
                instances.Add((algorithm, Create(algorithm)));

            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var (_, instance) in instances)
                        instance.AppendData(buffer, 0, read);
                }

                return instances.ToDictionary(
                    x => x.Algorithm,
                    x => Convert.ToHexStringLower(x.Instance.GetHashAndReset()));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            foreach (var (_, instance) in instances)
                instance.Dispose();
        }
    }

    public static Task<IReadOnlyDictionary<HashAlgorithmName, string>> ComputeHashesAsync(
        string path,
        IEnumerable<HashAlgorithmName> algorithms,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ComputeHashes(path, algorithms, cancellationToken), cancellationToken);

    /// <summary>Returns the algorithm whose computed hash equals <paramref name="candidate"/> (case-insensitive), or null.</summary>
    public static HashAlgorithmName? FindMatch(IReadOnlyDictionary<HashAlgorithmName, string> hashes, string candidate)
    {
        var normalized = candidate.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return null;

        foreach (var (algorithm, hash) in hashes)
        {
            if (hash == normalized)
                return algorithm;
        }
        return null;
    }

    private static IncrementalHash Create(HashAlgorithmName name)
    {
        if (name == HashAlgorithmName.MD5 ||
            name == HashAlgorithmName.SHA1 ||
            name == HashAlgorithmName.SHA256 ||
            name == HashAlgorithmName.SHA384 ||
            name == HashAlgorithmName.SHA512)
            return IncrementalHash.CreateHash(name);
        throw new NotSupportedException($"Unsupported hash algorithm: '{name.Name}'.");
    }
}
