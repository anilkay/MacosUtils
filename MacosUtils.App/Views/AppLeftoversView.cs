using Avalonia.Controls;
using MacosUtils.Core.IO;

namespace MacosUtils.App.Views;

/// <summary>Files deleted apps left in ~/Library; selected apps' files go to the Trash.</summary>
public sealed class AppLeftoversView : UtilityView
{
    private sealed record Row(string BundleId, string Size, long SizeBytes, string Places, string LastChanged, DateTime LastWriteUtc,
        string Location, string Path, bool CanTrash);

    private readonly DataGrid _grid = CreateGrid(multiSelect: true);
    private readonly TextBlock _status = StatusLine();
    private readonly Button _scanButton = AccentButton("Scan");
    private readonly Button _trashButton = new() { Content = "Move to Trash", IsEnabled = false };
    private CancellationTokenSource? _cts;

    public AppLeftoversView()
    {
        AddColumn(_grid, "App", nameof(Row.BundleId), 1.6);
        AddColumn(_grid, "Size", nameof(Row.Size), pixels: 100, sortBy: nameof(Row.SizeBytes));
        AddColumn(_grid, "Found in", nameof(Row.Places), 2);
        AddColumn(_grid, "Last changed", nameof(Row.LastChanged), pixels: 120, sortBy: nameof(Row.LastWriteUtc));
        AddColumn(_grid, "Location", nameof(Row.Location), 2);
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
        if (!ShowMacOnlyNotice(root, "The leftover scan"))
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
        var progress = new Progress<string>(name => _status.Text = $"Looking at {name}...");
        try
        {
            var scan = await Task.Run(() => AppLeftovers.Scan(progress, cts.Token));
            _grid.ItemsSource = scan.Apps.Select(a => new Row(a.BundleId, a.CanTrash ? FormatBytes(a.SizeBytes) : "—", a.SizeBytes, a.Places,
                a.LastWriteUtc == DateTime.MinValue ? "—" : a.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd"), a.LastWriteUtc,
                ShortenHome(a.Items[0].Path), a.Items[0].Path, a.CanTrash)).ToList();
            var compared = $"Compared with {scan.Installed.Count} installed apps"
                + (scan.Installed.SearchedSpotlight ? "." : " in the Applications folders only, because Spotlight found none: "
                   + "check the list for apps you keep somewhere else.");
            _status.Text = scan.Apps.Count == 0
                ? $"No leftovers found. {compared}"
                : $"{scan.Apps.Count} removed apps left {FormatBytes(scan.Apps.Sum(a => a.SizeBytes))} behind. {compared} "
                  + "Files of a vendor that still has an app installed are not listed. Dimmed rows are in containers "
                  + "macOS protects; remove those in Finder.";
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
            _status.Text = "Select one or more apps first.";
            return;
        }
        var names = string.Join("\n", selected.Select(r => $"{r.BundleId} ({r.Size}): {r.Places}"));
        if (!await Dialogs.ConfirmAsync(this, "Move to Trash",
                $"Move the files of {selected.Count} removed app(s) ({FormatBytes(selected.Sum(r => r.SizeBytes))}) to the Trash?\n\n{names}\n\n"
                + "Their settings and data are gone if you install the app again. Files in protected containers stay. "
                + "You can put files back from the Trash until you empty it.",
                "Move to Trash", "Cancel"))
            return;

        var ids = selected.Select(r => r.BundleId).ToList();
        _status.Text = "Moving to the Trash...";
        try
        {
            var (moved, kept) = await Task.Run(() => AppLeftovers.MoveToTrash(ids));
            await ScanAsync();
            _status.Text = $"Moved {moved} item(s) to the Trash. Empty the Trash to free the space. " + _status.Text;
            if (kept.Count > 0)
                await Dialogs.ShowAsync(this, "Move to Trash", "These were kept:\n\n" + string.Join("\n", kept));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _status.Text = $"Nothing was moved: {ex.Message}";
        }
    }
}
