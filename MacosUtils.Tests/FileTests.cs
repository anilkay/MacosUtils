using System.Security.Cryptography;
using MacosUtils.Core.Hashing;
using MacosUtils.Core.IO;
using Xunit;

namespace MacosUtils.Tests;

/// <summary>The file scanning, hashing and duplicate logic, on a temporary folder.</summary>
public sealed class FileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "macosutils-tests-" + Guid.NewGuid().ToString("N"));

    public FileTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Largest_files_are_sorted_by_size_and_capped()
    {
        Write("a.bin", new byte[10]);
        Write("sub/b.bin", new byte[300]);
        Write("sub/deeper/c.bin", new byte[2000]);

        var (files, scanned, errors) = FileScanner.FindLargestFiles([_root], 2);

        Assert.Empty(errors);
        Assert.Equal(3, scanned);
        Assert.Equal([2000L, 300L], files.Select(f => f.Size).ToArray());
    }

    [Fact]
    public void Folder_contents_include_the_size_of_everything_below_a_folder()
    {
        Write("big/one.bin", new byte[500]);
        Write("big/two/two.bin", new byte[500]);
        Write("small.bin", new byte[100]);

        var items = FileScanner.GetFolderContents(_root);

        Assert.Equal("big", items[0].Name);
        Assert.True(items[0].IsFolder);
        Assert.Equal(1000, items[0].Size);
        Assert.Equal(100, items[1].Size);
    }

    [Fact]
    public void Symbolic_links_are_not_followed()
    {
        Write("real/file.bin", new byte[1000]);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // creating symlinks needs a privilege on Windows
        }

        var (_, scanned, _) = FileScanner.FindLargestFiles([_root], 10);
        Assert.Equal(1, scanned);
    }

    [Fact]
    public void Hashes_match_the_well_known_values()
    {
        var path = Write("abc.txt", "abc"u8.ToArray());

        var hashes = FileHasher.ComputeHashes(path, [HashAlgorithmName.MD5, HashAlgorithmName.SHA256]);

        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", hashes[HashAlgorithmName.MD5]);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hashes[HashAlgorithmName.SHA256]);
        Assert.Equal(HashAlgorithmName.MD5, FileHasher.FindMatch(hashes, " 900150983CD24FB0D6963F7D28E17F72 "));
        Assert.Null(FileHasher.FindMatch(hashes, "deadbeef"));
    }

    [Fact]
    public void Hashing_without_an_algorithm_is_rejected()
    {
        var path = Write("x.txt", [1]);
        Assert.Throws<ArgumentException>(() => FileHasher.ComputeHashes(path, []));
    }

    [Fact]
    public void Duplicates_are_grouped_by_content_not_by_name_or_size_alone()
    {
        var same = new byte[200_000];
        new Random(1).NextBytes(same);
        var almost = (byte[])same.Clone();
        almost[^1] ^= 0xFF; // same size and same first 64 KB, different end

        var first = Write("one.bin", same);
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddDays(-2));
        Write("copy/two.bin", same);
        Write("other.bin", almost);
        Write("tiny1.txt", "hi"u8.ToArray());
        Write("tiny2.txt", "hi"u8.ToArray());

        var (groups, scanned, errors) = DuplicateFinder.FindDuplicates([_root], minSize: 100);

        Assert.Empty(errors);
        Assert.Equal(5, scanned);
        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(first, group.Files[0].FullPath); // oldest first
        Assert.Equal(200_000, group.WastedBytes);
    }

    [Fact]
    public void Empty_files_are_never_duplicates()
    {
        Write("a", []);
        Write("b", []);
        var (groups, _, _) = DuplicateFinder.FindDuplicates([_root], minSize: 0);
        Assert.Empty(groups);
    }

    [Fact]
    public void A_missing_scan_root_is_reported_not_thrown()
    {
        var (_, _, errors) = FileScanner.FindLargestFiles([Path.Combine(_root, "nope")], 5);
        Assert.Single(errors);
    }

    [Fact]
    public void Leftovers_are_grouped_per_removed_app_and_skip_installed_vendors()
    {
        Write("Library/Caches/com.gone.app/cache.db", new byte[1000]);
        Write("Library/Caches/com.gone.app.helper/x", new byte[24]);
        Write("Library/Preferences/com.gone.app.plist", new byte[100]);
        Write("Library/Application Support/com.gone.app/data", new byte[500]);
        Directory.CreateDirectory(Path.Combine(_root, "Library", "Containers", "com.gone.app"));
        Directory.CreateDirectory(Path.Combine(_root, "Library", "Group Containers", "ABCDE12345.com.gone.app.shared"));
        Write("Library/Caches/com.kept.other/x", new byte[10]);          // same vendor as an installed app
        Write("Library/Caches/com.apple.Safari/x", new byte[10]);         // Apple
        Write("Library/Preferences/systemgroup.com.apple.icloud.searchpartyd.sharedsettings.plist", new byte[10]);
        Directory.CreateDirectory(Path.Combine(_root, "Library", "Group Containers", "243LU875E5.groups.com.apple.podcasts"));
        Directory.CreateDirectory(Path.Combine(_root, "Library", "Group Containers", "group.is.workflow.my.app")); // Shortcuts
        Write("Library/Caches/org.sparkle-project.Sparkle/x", new byte[10]); // shared framework
        Write("Library/Caches/org.swift.swiftpm/x", new byte[10]);       // a developer cache
        Write("Library/Application Support/Slack/x", new byte[10]);      // not a bundle id
        Write("Library/Logs/io.lone.tool/log.txt", new byte[7]);

        var leftovers = AppLeftovers.Find(_root, ["com.kept.main"]);

        Assert.Equal(["com.gone.app", "io.lone.tool"], leftovers.Select(a => a.BundleId).ToArray());
        var gone = leftovers[0];
        Assert.Equal(6, gone.Items.Count);
        Assert.Equal(1624, gone.SizeBytes);
        Assert.True(gone.CanTrash);
        Assert.Equal(2, gone.Items.Count(i => i.Place.Protected));
        Assert.All(gone.Items.Where(i => i.Place.Protected), i => Assert.Equal(0, i.SizeBytes));
        Assert.Equal("Application Support, Caches, Containers, Group Containers, Preferences", gone.Places);
    }
}
