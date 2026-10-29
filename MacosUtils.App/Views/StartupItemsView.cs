using Avalonia.Controls;
using MacosUtils.Core.SystemInfo;

namespace MacosUtils.App.Views;

/// <summary>Launch agents and daemons that start with the Mac (read-only; the plist can be shown in Finder).</summary>
public sealed class StartupItemsView : UtilityView
{
    private sealed record Row(string Label, string Location, string Starts, string Command, string PlistPath, bool Disabled);

    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _status = StatusLine();

    public StartupItemsView()
    {
        AddColumn(_grid, "Label", nameof(Row.Label), 2);
        AddColumn(_grid, "Location", nameof(Row.Location), pixels: 180);
        AddColumn(_grid, "Starts", nameof(Row.Starts), pixels: 150);
        AddColumn(_grid, "Command", nameof(Row.Command), 3);
        StyleRows<Row>(_grid, dim: r => r.Disabled);

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await LoadAsync();
        var reveal = new Button { Content = "Show plist in Finder" };
        reveal.Click += async (_, _) =>
        {
            if (_grid.SelectedItem is Row row)
                await RevealInFinderAsync(row.PlistPath);
        };
        _grid.DoubleTapped += async (_, _) =>
        {
            if (_grid.SelectedItem is Row row)
                await RevealInFinderAsync(row.PlistPath);
        };
        var root = new DockPanel { Children = { Toolbar(refresh, reveal), _status, _grid } };
        ShowMacOnlyNotice(root, "Launch agents");
        Content = root;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var items = await Task.Run(StartupItems.GetItems);
        _grid.ItemsSource = items.Select(i => new Row(i.Label, i.Location,
            i.Disabled ? "Disabled" : string.Join(", ", new[] { i.RunAtLoad ? "At load" : null, i.KeepAlive ? "Kept alive" : null }
                .Where(s => s is not null).DefaultIfEmpty("On demand")),
            i.Command, i.PlistPath, i.Disabled)).ToList();
        _status.Text = $"{items.Count} item(s). Apple's own jobs in /System are not listed; login items added through "
            + "System Settings › General › Login Items are managed there.";
    }
}
