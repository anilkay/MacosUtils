using Avalonia.Controls;
using MacosUtils.Core.IO;

namespace MacosUtils.App.Views;

/// <summary>Xcode, Homebrew and package-manager caches with their sizes; selected ones go to the Trash.</summary>
public sealed class DeveloperCachesView : UtilityView
{
    private sealed record Row(string Category, string Name, string Size, long SizeBytes, string Location, string Note, string Path, bool CanTrash);

    private readonly DataGrid _grid = CreateGrid(multiSelect: true);
    private readonly TextBlock _status = StatusLine();
    private readonly Button _scanButton = AccentButton("Scan");
    private readonly Button _trashButton = new() { Content = "Move to Trash", IsEnabled = false };
    private CancellationTokenSource? _cts;

    public DeveloperCachesView()
    {
        AddColumn(_grid, "Category", nameof(Row.Category), pixels: 110);
        AddColumn(_grid, "Name", nameof(Row.Name), 1.2);
        AddColumn(_grid, "Size", nameof(Row.Size), pixels: 100, sortBy: nameof(Row.SizeBytes));
        AddColumn(_grid, "Location", nameof(Row.Location), 1.6);
        AddColumn(_grid, "After clearing", nameof(Row.Note), 3);
        StyleRows<Row>(_grid, dim: r => !r.CanTrash);

        _scanButton.Click += async (_, _) => await ScanAsync();
        _trashButton.Click += async (_, _) => await TrashSelectedAsync();
        var reveal = new Button { Content = "Show in Finder" };
        reveal.Click += async (_, _) =>
        {
            if (_grid.SelectedItem is Row row)
                await RevealInFinderAsync(row.Path);
        };
        _grid.DoubleTapped += async (_, _) =>
        {
            if (_grid.SelectedItem is Row row)
                await RevealInFinderAsync(row.Path);
        };
        _grid.SelectionChanged += (_, _) => _trashButton.IsEnabled = SelectedRows().Any(r => r.CanTrash);

        var root = new DockPanel { Children = { Toolbar(_scanButton, _trashButton, reveal), _status, _grid } };
        if (!ShowMacOnlyNotice(root, "The cache scan"))
            _ = ScanAsync();
        Content = root;
    }

    private List<Row> SelectedRows() => _grid.SelectedItems.OfType<Row>().ToList();

    private async Task ScanAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _scanButton.IsEnabled = false;
        var progress = new Progress<string>(name => _status.Text = $"Measuring {name}...");
        try
        {
            var items = await Task.Run(() => DeveloperCaches.Scan(progress, cts.Token));
            _grid.ItemsSource = items.Select(i => new Row(i.Location.Category, i.Location.Name, FormatBytes(i.SizeBytes), i.SizeBytes,
                ShortenHome(i.Location.Path), i.Location.Note, i.Location.Path, i.Location.CanTrash)).ToList();
            var freeable = items.Where(i => i.Location.CanTrash).Sum(i => i.SizeBytes);
            _status.Text = items.Count == 0
                ? "None of the known developer cache folders exist on this Mac."
                : $"{FormatBytes(freeable)} can go to the Trash. Select rows (⌘-click for several) and choose Move to Trash; "
                  + "dimmed rows are cleaned with their own tool.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _status.Text = $"The scan failed: {ex.Message}";
        }
        finally
        {
            if (_cts == cts)
                _scanButton.IsEnabled = true;
        }
    }

    private async Task TrashSelectedAsync()
    {
        var selected = SelectedRows().Where(r => r.CanTrash).ToList();
        if (selected.Count == 0)
        {
            _status.Text = "Select one or more caches first.";
            return;
        }
        var total = selected.Sum(r => r.SizeBytes);
        var names = string.Join("\n", selected.Select(r => $"{r.Name} ({r.Size}): {r.Location}"));
        if (!await Dialogs.ConfirmAsync(this, "Move to Trash",
                $"Move {selected.Count} cache folder(s) ({FormatBytes(total)}) to the Trash?\n\n{names}\n\n"
                + "The tools rebuild or download them again when needed. Quit Xcode and running builds first. "
                + "The space is freed when you empty the Trash.",
                "Move to Trash", "Cancel"))
            return;

        var failures = new List<string>();
        foreach (var row in selected)
        {
            try
            {
                DeveloperCaches.MoveToTrash(row.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
            {
                failures.Add($"{row.Name}: {ex.Message}");
            }
        }
        await ScanAsync();
        _status.Text = $"Moved {selected.Count - failures.Count} folder(s) to the Trash. Empty the Trash to free the space. " + _status.Text;
        if (failures.Count > 0)
            await Dialogs.ShowAsync(this, "Move to Trash", "These folders were kept:\n\n" + string.Join("\n", failures));
    }
}
