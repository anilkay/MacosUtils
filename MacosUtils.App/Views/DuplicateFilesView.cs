using Avalonia.Controls;
using MacosUtils.Core.IO;

namespace MacosUtils.App.Views;

/// <summary>Files with identical content (SHA-256), grouped; select every copy but the oldest and move them to the Trash.</summary>
public sealed class DuplicateFilesView : UtilityView
{
    private sealed record Row(int Group, string Name, string Folder, string Size, long SizeBytes, string Modified, DateTime ModifiedValue,
        string FullPath, bool IsOldest);

    private readonly ScanTarget _target;
    private readonly NumericUpDown _minSize = new() { Minimum = 0, Maximum = 100_000, Value = 1, Increment = 1, Width = 110, FormatString = "0" };
    private readonly Button _findButton = AccentButton("Find Duplicates");
    private readonly Button _cancelButton = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _selectCopiesButton = new() { Content = "Select Copies (Keep Oldest)" };
    private readonly Button _revealButton = new() { Content = "Show in Finder" };
    private readonly Button _trashButton = new() { Content = "Move to Trash" };
    private readonly DataGrid _grid = CreateGrid(multiSelect: true);
    private readonly TextBlock _status = StatusLine("Pick a folder (or all local volumes) and press Find Duplicates.");
    private List<Row> _rows = [];
    private CancellationTokenSource? _cts;

    public DuplicateFilesView()
    {
        _target = new ScanTarget(this, PickFolderAsync);
        AddColumn(_grid, "Group", nameof(Row.Group), pixels: 70);
        AddColumn(_grid, "Name", nameof(Row.Name), 2);
        AddColumn(_grid, "Folder", nameof(Row.Folder), 3);
        AddColumn(_grid, "Size", nameof(Row.Size), pixels: 100, sortBy: nameof(Row.SizeBytes));
        AddColumn(_grid, "Modified", nameof(Row.Modified), pixels: 150, sortBy: nameof(Row.ModifiedValue));
        // The oldest copy of each group is the one "Select Copies" keeps.
        StyleRows<Row>(_grid, dim: r => r.IsOldest);

        _findButton.Click += async (_, _) => await FindAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _selectCopiesButton.Click += (_, _) =>
        {
            _grid.SelectedItems.Clear();
            foreach (var row in _rows.Where(r => !r.IsOldest))
                _grid.SelectedItems.Add(row);
            _status.Text = $"Selected {_grid.SelectedItems.Count} copies; the oldest file of each group is kept.";
        };
        _revealButton.Click += async (_, _) => await RevealSelectedAsync();
        _trashButton.Click += async (_, _) => await TrashSelectedAsync();
        _grid.DoubleTapped += async (_, _) => await RevealSelectedAsync();

        var actions = Toolbar(_selectCopiesButton, _revealButton, _trashButton);
        DockPanel.SetDock(actions, Dock.Bottom);
        actions.Margin = new Avalonia.Thickness(0, 8, 0, 0);
        Content = new DockPanel
        {
            Children =
            {
                Toolbar(Label("Scan:"), _target.PathBox, _target.Browse, _target.AllVolumes,
                    Label("Min size (MB):"), _minSize, _findButton, _cancelButton),
                _status,
                actions,
                _grid,
            },
        };
    }

    private async Task FindAsync()
    {
        var roots = _target.Roots(out var error);
        if (roots is null)
        {
            _status.Text = error;
            return;
        }
        var minSize = (long)(_minSize.Value ?? 0) * 1024 * 1024;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _findButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _target.SetEnabled(false);
        var progress = new Progress<DuplicateScanProgress>(p => _status.Text = p.Phase switch
        {
            DuplicateScanPhase.Scanning => $"Listing files... {p.Done:N0} — {ShortenHome(p.CurrentPath ?? "")}",
            DuplicateScanPhase.Comparing => $"Comparing same-size files... {p.Done:N0} of {p.Total:N0}",
            _ => $"Hashing candidates... {p.Done:N0} of {p.Total:N0}",
        });
        try
        {
            var (groups, scanned, errors) = await Task.Run(() => DuplicateFinder.FindDuplicates(roots, minSize, progress, token), token);
            _rows = groups.SelectMany((g, i) => g.Files.Select((f, j) => new Row(i + 1, Path.GetFileName(f.FullPath),
                ShortenHome(Path.GetDirectoryName(f.FullPath) ?? ""), FormatBytes(f.Size), f.Size, f.Modified.ToString("g"), f.Modified,
                f.FullPath, j == 0))).ToList();
            _grid.ItemsSource = _rows;
            _status.Text = groups.Count == 0
                ? $"No duplicates in {scanned:N0} files."
                : $"{groups.Count:N0} groups in {scanned:N0} files; {FormatBytes(groups.Sum(g => g.WastedBytes))} could be freed by keeping one copy of each."
                    + (errors.Count > 0 ? $" Could not read: {string.Join(", ", errors.Select(e => e.Root))}." : "");
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Scan cancelled.";
        }
        finally
        {
            _findButton.IsEnabled = true;
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
        // Never let a selection remove every copy of a group without saying so.
        var wholeGroups = selected.GroupBy(r => r.Group)
            .Where(g => g.Count() == _rows.Count(r => r.Group == g.Key))
            .Select(g => g.Key)
            .ToList();
        var warning = wholeGroups.Count > 0
            ? $"\n\nWarning: every copy of group {string.Join(", ", wholeGroups)} is selected, so no copy of those files would be left outside the Trash."
            : "";
        var names = string.Join("\n", selected.Take(10).Select(r => ShortenHome(r.FullPath)))
            + (selected.Count > 10 ? $"\n...and {selected.Count - 10} more" : "");
        if (!await Dialogs.ConfirmAsync(this, "Move to Trash",
                $"Move {selected.Count} file(s) ({FormatBytes(selected.Sum(r => r.SizeBytes))}) to the Trash?\n\n{names}{warning}",
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
