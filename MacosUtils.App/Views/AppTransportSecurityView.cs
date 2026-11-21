using Avalonia.Controls;
using MacosUtils.Core.SystemInfo;

namespace MacosUtils.App.Views;

/// <summary>Apps whose App Transport Security settings allow plain HTTP or TLS below 1.2 (read-only).</summary>
public sealed class AppTransportSecurityView : UtilityView
{
    private sealed record Row(string App, string Scope, string Setting, string Risk, int RiskOrder, string Path, bool High, bool Low);

    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _status = StatusLine();
    private readonly Button _scanButton = AccentButton("Scan");
    private CancellationTokenSource? _cts;

    public AppTransportSecurityView()
    {
        AddColumn(_grid, "App", nameof(Row.App), 1.2);
        AddColumn(_grid, "Applies to", nameof(Row.Scope), 1.4);
        AddColumn(_grid, "Setting", nameof(Row.Setting), 3);
        AddColumn(_grid, "Risk", nameof(Row.Risk), pixels: 90, sortBy: nameof(Row.RiskOrder));
        StyleRows<Row>(_grid, warn: r => r.High, dim: r => r.Low);

        _scanButton.Click += async (_, _) => await ScanAsync();
        var reveal = new Button { Content = "Show in Finder" };
        reveal.Click += async (_, _) =>
        {
            if (_grid.SelectedItem is Row row)
                await RevealInFinderAsync(row.Path);
        };
        var root = new DockPanel { Children = { Toolbar(_scanButton, reveal), _status, _grid } };
        if (!ShowMacOnlyNotice(root, "The App Transport Security scan"))
            _ = ScanAsync();
        Content = root;
    }

    private async Task ScanAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _scanButton.IsEnabled = false;
        var progress = new Progress<string>(name => _status.Text = $"Reading {name}...");
        try
        {
            var scan = await Task.Run(() => AppTransportSecurity.Scan(progress, cts.Token));
            _grid.ItemsSource = scan.Exceptions.Select(e => new Row(e.App, e.Scope, e.Setting, e.Risk.ToString(), (int)e.Risk,
                e.AppPath, e.Risk == AtsRisk.High, e.Risk == AtsRisk.Low)).ToList();
            var high = scan.Exceptions.Where(e => e.Risk == AtsRisk.High).Select(e => e.AppPath).Distinct().Count();
            _status.Text = $"{scan.AppsWithExceptions} of {scan.AppsRead} apps loosen App Transport Security"
                + (high > 0 ? $"; {high} allow plain HTTP or TLS below 1.2 broadly or for a domain." : ".")
                + " macOS has no system-wide TLS switch: Apple's networking requires TLS 1.2+ unless an app opts out here. "
                + "Browsers and tools with their own TLS (Chrome, Firefox, curl) are not covered"
                + (scan.SearchedSpotlight ? "." : "; Spotlight found no apps, so only the Applications folders were read.");
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
}
