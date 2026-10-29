using Avalonia.Controls;
using MacosUtils.Core.IO;

namespace MacosUtils.App.Views;

/// <summary>Every mounted volume with its capacity and free space; volumes under 10% free are highlighted.</summary>
public sealed class DiskInfoView : UtilityView
{
    private sealed record Row(string Name, string MountPoint, string FileSystem, string Kind, string Total, long TotalBytes,
        string Free, long FreeBytes, string FreePercent, double FreePercentValue);

    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _status = StatusLine();

    public DiskInfoView()
    {
        AddColumn(_grid, "Volume", nameof(Row.Name), 2);
        AddColumn(_grid, "Mounted At", nameof(Row.MountPoint), 2);
        AddColumn(_grid, "File System", nameof(Row.FileSystem), pixels: 100);
        AddColumn(_grid, "Kind", nameof(Row.Kind), pixels: 100);
        AddColumn(_grid, "Total", nameof(Row.Total), pixels: 100, sortBy: nameof(Row.TotalBytes));
        AddColumn(_grid, "Free", nameof(Row.Free), pixels: 100, sortBy: nameof(Row.FreeBytes));
        AddColumn(_grid, "Free %", nameof(Row.FreePercent), pixels: 80, sortBy: nameof(Row.FreePercentValue));
        StyleRows<Row>(_grid, warn: r => r.FreePercentValue < 10);

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => Load();
        var finder = new Button { Content = "Show in Finder" };
        finder.Click += async (_, _) =>
        {
            if (_grid.SelectedItem is Row row)
                await RevealInFinderAsync(row.MountPoint);
        };
        Content = new DockPanel { Children = { Toolbar(refresh, finder), _status, _grid } };
        Load();
    }

    private void Load()
    {
        var volumes = Volumes.GetVolumes();
        _grid.ItemsSource = volumes.Select(v => new Row(v.Name, v.MountPoint, v.FileSystem,
            v.IsNetwork ? "Network" : v.IsRemovable ? "Removable" : "Local",
            FormatBytes(v.TotalBytes), v.TotalBytes, FormatBytes(v.FreeBytes), v.FreeBytes,
            $"{v.FreePercent:0.#}%", v.FreePercent)).ToList();
        var low = volumes.Count(v => v.FreePercent < 10);
        _status.Text = $"{volumes.Count} volume(s)." + (low > 0 ? $" {low} with less than 10% free space (in red)." : "")
            + " Free space does not count purgeable space macOS can reclaim.";
    }
}
