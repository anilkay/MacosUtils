using Avalonia.Controls;
using MacosUtils.Core.SystemInfo;

namespace MacosUtils.App.Views;

/// <summary>Whether Homebrew is installed, and its formulae and casks with version, reason and size (read-only).</summary>
public sealed class HomebrewView : UtilityView
{
    private sealed record Row(string Name, string Kind, string Version, string Reason, string Installed, string Size, long SizeBytes,
        string Prefix, string Path, bool Dependency);

    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _summary = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 8) };
    private readonly TextBlock _status = StatusLine();
    private readonly Button _refresh = new() { Content = "Refresh" };
    private CancellationTokenSource? _cts;

    public HomebrewView()
    {
        AddColumn(_grid, "Name", nameof(Row.Name), 2);
        AddColumn(_grid, "Type", nameof(Row.Kind), pixels: 90);
        AddColumn(_grid, "Version", nameof(Row.Version), 1);
        AddColumn(_grid, "Installed", nameof(Row.Reason), pixels: 130);
        AddColumn(_grid, "Date", nameof(Row.Installed), pixels: 110);
        AddColumn(_grid, "Size", nameof(Row.Size), pixels: 100, sortBy: nameof(Row.SizeBytes));
        StyleRows<Row>(_grid, dim: r => r.Dependency);

        _refresh.Click += async (_, _) => await LoadAsync();
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
        DockPanel.SetDock(_summary, Dock.Top);
        var root = new DockPanel { Children = { Toolbar(_refresh, reveal), _summary, _status, _grid } };
        ShowMacOnlyNotice(root, "Homebrew");
        Content = root;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _refresh.IsEnabled = false;
        _status.Text = "Reading Homebrew's folders...";
        try
        {
            var installations = await Task.Run(() => Homebrew.GetInstallations(cts.Token));
            if (installations.Count == 0)
            {
                _summary.Text = "Homebrew is not installed. Get it from https://brew.sh; this screen then lists what it installs.";
                _grid.ItemsSource = null;
                _status.Text = "";
                return;
            }
            _summary.Text = string.Join(Environment.NewLine, installations.Select(i =>
                $"Homebrew{(i.Version.Length > 0 ? " " + i.Version : "")} at {i.Prefix} ({i.Architecture}{(i.UnderRosetta ? ", runs under Rosetta 2" : "")}): "
                + $"{i.Formulae} formulae, {i.Casks} casks."));
            _status.Text = "Measuring sizes...";

            var packages = await Task.Run(() => Homebrew.GetPackages(includeSizes: true, cts.Token));
            var several = installations.Count > 1;
            _grid.ItemsSource = packages.Select(p => new Row(
                p.Name,
                p.Kind == HomebrewKind.Cask ? "Cask" : "Formula",
                p.Versions.Count > 1 ? $"{p.Version} (+{p.Versions.Count - 1} older)" : p.Version,
                (p.InstalledOnRequest switch { true => "On request", false => "Dependency", null => "" }) + (p.Pinned ? ", pinned" : ""),
                p.InstalledAt?.ToString("d") ?? "",
                FormatBytes(p.SizeBytes),
                p.SizeBytes,
                p.Prefix,
                p.Path,
                p.InstalledOnRequest == false)).ToList();

            var total = packages.Sum(p => p.SizeBytes);
            var older = packages.Count(p => p.Versions.Count > 1);
            _status.Text = $"{packages.Count} package(s), {FormatBytes(total)}. Download cache {FormatBytes(installations[0].CacheBytes)} in "
                + $"{ShortenHome(Homebrew.CacheFolder)}" + (older > 0 ? $"; {older} keep older versions" : "")
                + ". \"brew cleanup\" in Terminal frees both. Dependencies are dimmed.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read Homebrew: {ex.Message}";
        }
        finally
        {
            if (_cts == cts)
                _refresh.IsEnabled = true;
        }
    }
}
