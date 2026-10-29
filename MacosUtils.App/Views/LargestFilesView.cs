using Avalonia.Controls;
using MacosUtils.Core.IO;

namespace MacosUtils.App.Views;

/// <summary>The 100 largest files under a folder or on every local volume; show them in Finder or move them to the Trash.</summary>
public sealed class LargestFilesView : UtilityView
{
    private const int MaxResults = 100;

    private sealed record Row(int Rank, string Name, string Folder, string Size, long SizeBytes, string Modified, DateTime ModifiedValue, string FullPath);

    private readonly ScanTarget _target;
    private readonly Button _scanButton = AccentButton("Scan");
    private readonly Button _cancelButton = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _revealButton = new() { Content = "Show in Finder" };
    private readonly Button _trashButton = new() { Content = "Move to Trash" };
    private readonly DataGrid _grid = CreateGrid(multiSelect: true);
    private readonly TextBlock _status = StatusLine("Pick a folder (or all local volumes) and press Scan.");
    private List<Row> _rows = [];
    private CancellationTokenSource? _cts;

    public LargestFilesView()
    {
        _target = new ScanTarget(this, PickFolderAsync);
        AddColumn(_grid, "#", nameof(Row.Rank), pixels: 50);
        AddColumn(_grid, "Name", nameof(Row.Name), 2);
        AddColumn(_grid, "Folder", nameof(Row.Folder), 3);
        AddColumn(_grid, "Size", nameof(Row.Size), pixels: 100, sortBy: nameof(Row.SizeBytes));
        AddColumn(_grid, "Modified", nameof(Row.Modified), pixels: 150, sortBy: nameof(Row.ModifiedValue));

        _scanButton.Click += async (_, _) => await ScanAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _revealButton.Click += async (_, _) => await RevealSelectedAsync();
        _trashButton.Click += async (_, _) => await TrashSelectedAsync();
        _grid.DoubleTapped += async (_, _) => await RevealSelectedAsync();

        var actions = Toolbar(_revealButton, _trashButton);
        DockPanel.SetDock(actions, Dock.Bottom);
        actions.Margin = new Avalonia.Thickness(0, 8, 0, 0);
        Content = new DockPanel
        {
            Children =
            {
                Toolbar(Label("Scan:"), _target.PathBox, _target.Browse, _target.AllVolumes, _scanButton, _cancelButton),
                _status,
                actions,
                _grid,
            },
        };
    }

    private async Task ScanAsync()
    {
        var roots = _target.Roots(out var error);
        if (roots is null)
        {
            _status.Text = error;
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _scanButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _target.SetEnabled(false);
        _status.Text = "Scanning...";
        var progress = new Progress<ScanProgress>(p =>
            _status.Text = $"Scanning... {p.Scanned:N0} files — {ShortenHome(p.CurrentDirectory ?? "")}");
        try
        {
            var (files, scanned, errors) = await Task.Run(() => FileScanner.FindLargestFiles(roots, MaxResults, progress, token), token);
            _rows = files.Select((f, i) => new Row(i + 1, Path.GetFileName(f.FullPath), ShortenHome(Path.GetDirectoryName(f.FullPath) ?? ""),
                FormatBytes(f.Size), f.Size, f.Modified.ToString("g"), f.Modified, f.FullPath)).ToList();
            _grid.ItemsSource = _rows;
            _status.Text = $"Scanned {scanned:N0} files; showing the {files.Count} largest."
                + (errors.Count > 0 ? $" Could not read: {string.Join(", ", errors.Select(e => e.Root))}." : "")
                + " Folders macOS protects are skipped unless the app has Full Disk Access.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Scan cancelled.";
        }
        finally
        {
            _scanButton.IsEnabled = true;
            _cancelButton.IsEnabled = false;
            _target.SetEnabled(true);
        }
    }

    private List<Row> SelectedRows() => _grid.SelectedItems.OfType<Row>().ToList();

    private async Task RevealSelectedAsync()
    {
        if (SelectedRows().FirstOrDefault() is not { } row)
        {
            _status.Text = "Select a file first.";
            return;
        }
        if (!File.Exists(row.FullPath))
        {
            _status.Text = "File no longer exists.";
            return;
        }
        await RevealInFinderAsync(row.FullPath);
    }

    private async Task TrashSelectedAsync()
    {
        var selected = SelectedRows();
        if (selected.Count == 0)
        {
            _status.Text = "Select one or more files first.";
            return;
        }
        var total = selected.Sum(r => r.SizeBytes);
        var names = string.Join("\n", selected.Take(10).Select(r => ShortenHome(r.FullPath)))
            + (selected.Count > 10 ? $"\n...and {selected.Count - 10} more" : "");
        if (!await Dialogs.ConfirmAsync(this, "Move to Trash",
                $"Move {selected.Count} file(s) ({FormatBytes(total)}) to the Trash?\n\n{names}\n\nYou can put them back from the Trash.",
                "Move to Trash", "Cancel"))
            return;

        var moved = 0;
        var failures = new List<string>();
        foreach (var row in selected)
        {
            try
            {
                Trash.MoveToTrash(row.FullPath);
                _rows.Remove(row);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                failures.Add($"{Path.GetFileName(row.FullPath)}: {ex.Message}");
            }
        }
        _grid.ItemsSource = _rows.ToList();
        _status.Text = $"Moved {moved} file(s) to the Trash.";
        if (failures.Count > 0)
            await Dialogs.ShowAsync(this, "Move to Trash", "These files were kept:\n\n" + string.Join("\n", failures));
    }
}
