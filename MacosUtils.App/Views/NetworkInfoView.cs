using Avalonia.Controls;
using MacosUtils.Core.Net;

namespace MacosUtils.App.Views;

/// <summary>Network interfaces with their addresses, and a ping tool.</summary>
public sealed class NetworkInfoView : UtilityView
{
    private sealed record Row(string Name, string Type, string Status, string IPv4, string IPv6, string Mac, bool IsUp);

    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBox _hostBox = new() { Width = 260, PlaceholderText = "Host to ping (e.g. 8.8.8.8)" };
    private readonly Button _pingButton = AccentButton("Ping");
    private readonly SelectableTextBlock _pingResult = new() { Margin = new Avalonia.Thickness(0, 4, 0, 8) };
    private readonly TextBlock _status = StatusLine();

    public NetworkInfoView()
    {
        AddColumn(_grid, "Interface", nameof(Row.Name), pixels: 110);
        AddColumn(_grid, "Type", nameof(Row.Type), pixels: 130);
        AddColumn(_grid, "Status", nameof(Row.Status), pixels: 90);
        AddColumn(_grid, "IPv4", nameof(Row.IPv4), 1);
        AddColumn(_grid, "IPv6", nameof(Row.IPv6), 2);
        AddColumn(_grid, "MAC", nameof(Row.Mac), pixels: 150);
        StyleRows<Row>(_grid, dim: r => !r.IsUp);
        _pingResult.Classes.Add("mono");
        Avalonia.Controls.DockPanel.SetDock(_pingResult, Dock.Top);

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => Load();
        _pingButton.Click += async (_, _) => await PingAsync();
        _hostBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
                await PingAsync();
        };
        Content = new DockPanel
        {
            Children = { Toolbar(_hostBox, _pingButton, refresh), _pingResult, _status, _grid },
        };
        Load();
    }

    private void Load()
    {
        try
        {
            var adapters = NetworkInfo.GetAdapters();
            _grid.ItemsSource = adapters
                .OrderBy(a => a.Status == "Up" ? 0 : 1)
                .ThenBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => new Row(a.Name, a.Type, a.Status, a.IPv4, a.IPv6, a.Mac, a.Status == "Up"))
                .ToList();
            _status.Text = $"{adapters.Count} interface(s). en0 is usually Wi-Fi or the built-in Ethernet; utun interfaces are VPNs and system tunnels.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read the network interfaces: {ex.Message}";
        }
    }

    private async Task PingAsync()
    {
        var host = _hostBox.Text?.Trim() ?? "";
        if (host.Length == 0)
        {
            _pingResult.Text = "Enter a host first.";
            return;
        }
        _pingButton.IsEnabled = false;
        _pingResult.Text = $"Pinging {host}...";
        try
        {
            var results = await NetworkInfo.PingAsync(host, 4);
            var ok = results.Where(r => r.Success).ToList();
            _pingResult.Text = $"{ok.Count}/{results.Count} replies" + (ok.Count > 0 ? $", avg {ok.Average(r => r.RoundtripMs):0} ms" : "")
                + Environment.NewLine + string.Join(Environment.NewLine, results.Select(r =>
                    r.Success ? $"Reply from {r.Address}: time={r.RoundtripMs} ms" : $"Request failed: {r.Status}"));
        }
        catch (Exception ex)
        {
            _pingResult.Text = $"Ping failed: {ex.GetBaseException().Message}";
        }
        finally
        {
            _pingButton.IsEnabled = true;
        }
    }
}
