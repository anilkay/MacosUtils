using Avalonia.Controls;
using MacosUtils.Core;
using MacosUtils.Core.IO;

namespace MacosUtils.App.Views;

/// <summary>Sizes of everything directly inside a folder, largest first; double-click a folder to open it.</summary>
public sealed class FolderSizeView : UtilityView
{
    private sealed record Row(string Name, string Kind, string Size, long SizeBytes, string Share, double ShareValue, string FullPath, bool IsFolder);

    private readonly TextBox _pathBox = new() { Width = 420, PlaceholderText = "Select a folder...", Text = AppPaths.Home };
    private readonly Button _analyzeButton = AccentButton("Analyze");
    private readonly Button _cancelButton = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _upButton = new() { Content = "Up" };
    private readonly Button _revealButton = new() { Content = "Show in Finder" };
    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _status = StatusLine("Pick a folder and press Analyze.");
    private CancellationTokenSource? _cts;

    public FolderSizeView()
    {
        AddColumn(_grid, "Name", nameof(Row.Name), 3);
        AddColumn(_grid, "Type", nameof(Row.Kind), pixels: 90);
        AddColumn(_grid, "Size", nameof(Row.Size), pixels: 110, sortBy: nameof(Row.SizeBytes));
        AddColumn(_grid, "Share", nameof(Row.Share), pixels: 90, sortBy: nameof(Row.ShareValue));
        StyleRows<Row>(_grid, dim: r => r.SizeBytes == 0);

        var browse = new Button { Content = "Browse..." };
        browse.Click += async (_, _) =>
        {
            if (await PickFolderAsync("Choose a folder to analyze") is { } folder)
            {
                _pathBox.Text = folder;
                await AnalyzeAsync();
            }
        };
        _analyzeButton.Click += async (_, _) => await AnalyzeAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _upButton.Click += async (_, _) =>
        {
            var parent = Path.GetDirectoryName((_pathBox.Text ?? "").TrimEnd('/'));
            if (!string.IsNullOrEmpty(parent))
            {
                _pathBox.Text = parent;
                await AnalyzeAsync();
            }
        };
        _revealButton.Click += async (_, _) =>
        {
            if (_grid.SelectedItem is Row row)
                await RevealInFinderAsync(row.FullPath);
            else if (Directory.Exists(_pathBox.Text))
                await RevealInFinderAsync(_pathBox.Text!);
        };
        _grid.DoubleTapped += async (_, _) =>
        {
            if (_grid.SelectedItem is Row { IsFolder: true } row)
            {
                _pathBox.Text = row.FullPath;
                await AnalyzeAsync();
            }
        };
        _pathBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
                await AnalyzeAsync();
        };

        Content = new DockPanel
        {
            Children =
            {
                Toolbar(Label("Folder:"), _pathBox, browse, _analyzeButton, _cancelButton, _upButton, _revealButton),
                _status,
                _grid,
            },
        };
    }

    private async Task AnalyzeAsync()
    {
        var folder = _pathBox.Text?.Trim() ?? "";
        if (folder.StartsWith('~'))
            folder = AppPaths.Home + folder[1..];
        if (!Directory.Exists(folder))
        {
            _status.Text = "Select an existing folder.";
            return;
        }
        if (Volumes.IsNetworkPath(Path.GetFullPath(folder)))
        {
            _status.Text = "Network shares are not analyzed: every file would be read over the network.";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _analyzeButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _status.Text = $"Analyzing {ShortenHome(folder)}...";
        try
        {
            var items = await Task.Run(() => FileScanner.GetFolderContents(folder, token), token);
            var total = items.Sum(i => i.Size);
            _grid.ItemsSource = items.Select(i =>
            {
                var share = total > 0 ? i.Size * 100.0 / total : 0;
                return new Row(i.Name, i.IsFolder ? "Folder" : "File", FormatBytes(i.Size), i.Size, $"{share:0.#}%", share,
                    Path.Combine(folder, i.Name), i.IsFolder);
            }).ToList();
            _status.Text = $"{ShortenHome(folder)}: {items.Count:N0} items, {FormatBytes(total)}. Double-click a folder to open it.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not read the folder: {ex.Message}";
        }
        finally
        {
            _analyzeButton.IsEnabled = true;
            _cancelButton.IsEnabled = false;
        }
    }
}
