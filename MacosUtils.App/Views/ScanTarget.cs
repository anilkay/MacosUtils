using Avalonia.Controls;
using MacosUtils.Core;
using MacosUtils.Core.IO;

namespace MacosUtils.App.Views;

/// <summary>
/// The "what to scan" controls shared by Largest Files and Duplicate Files: a folder, or every
/// local volume (the startup disk plus attached disks; network shares are never scanned).
/// </summary>
internal sealed class ScanTarget
{
    public TextBox PathBox { get; } = new() { Width = 360, PlaceholderText = "Folder to scan...", Text = AppPaths.Home };
    public CheckBox AllVolumes { get; } = new() { Content = "All local volumes" };
    public Button Browse { get; } = new() { Content = "Browse..." };

    public ScanTarget(UtilityView owner, Func<string, Task<string?>> pickFolder)
    {
        AllVolumes.IsCheckedChanged += (_, _) =>
        {
            PathBox.IsEnabled = AllVolumes.IsChecked != true;
            Browse.IsEnabled = AllVolumes.IsChecked != true;
        };
        Browse.Click += async (_, _) =>
        {
            if (await pickFolder("Choose a folder to scan") is { } folder)
                PathBox.Text = folder;
        };
        _ = owner;
    }

    /// <summary>The roots to scan, or null with a reason to show.</summary>
    public IReadOnlyList<string>? Roots(out string? error)
    {
        error = null;
        if (AllVolumes.IsChecked == true)
        {
            var roots = Volumes.LocalScanRoots();
            if (roots.Count == 0)
                error = "No local volumes found.";
            return roots.Count == 0 ? null : roots;
        }
        var folder = PathBox.Text?.Trim() ?? "";
        if (folder.StartsWith('~'))
            folder = AppPaths.Home + folder[1..];
        if (!Directory.Exists(folder))
        {
            error = "Pick a valid folder to scan.";
            return null;
        }
        var full = Path.GetFullPath(folder);
        if (Volumes.IsNetworkPath(full))
        {
            error = "Network shares are not scanned: every file would be read over the network.";
            return null;
        }
        return [full];
    }

    public void SetEnabled(bool enabled)
    {
        AllVolumes.IsEnabled = enabled;
        PathBox.IsEnabled = enabled && AllVolumes.IsChecked != true;
        Browse.IsEnabled = enabled && AllVolumes.IsChecked != true;
    }
}
