using System.Collections.ObjectModel;
using Avalonia.Controls;
using MacosUtils.Core.Net;

namespace MacosUtils.App.Views;

/// <summary>Traces the path to a host over TCP, one row per hop as it answers.</summary>
public sealed class TcpTracerouteView : UtilityView
{
    private sealed record Row(int Ttl, string Address, string HostName, string Rtt1, string Rtt2, string Rtt3, string Note, bool NoAnswer);

    private readonly TextBox _hostBox = new() { Width = 220, PlaceholderText = "Host (e.g. apple.com)" };
    private readonly NumericUpDown _portBox = new() { Minimum = 1, Maximum = 65535, Value = 443, Width = 120, FormatString = "0" };
    private readonly NumericUpDown _hopsBox = new() { Minimum = 1, Maximum = 64, Value = 30, Width = 110, FormatString = "0" };
    private readonly NumericUpDown _timeoutBox = new() { Minimum = 500, Maximum = 10000, Value = 2000, Increment = 500, Width = 130, FormatString = "0" };
    private readonly CheckBox _namesCheck = new() { Content = "Host names", IsChecked = true };
    private readonly Button _traceButton = AccentButton("Trace");
    private readonly Button _cancelButton = new() { Content = "Cancel", IsEnabled = false };
    private readonly ObservableCollection<Row> _rows = [];
    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _status = StatusLine("TCP connects with rising TTL; routers answer over ICMP. No administrator password is needed.");
    private CancellationTokenSource? _cts;

    public TcpTracerouteView()
    {
        AddColumn(_grid, "Hop", nameof(Row.Ttl), pixels: 60);
        AddColumn(_grid, "Address", nameof(Row.Address), pixels: 140);
        AddColumn(_grid, "Host Name", nameof(Row.HostName), 2);
        AddColumn(_grid, "RTT 1", nameof(Row.Rtt1), pixels: 80);
        AddColumn(_grid, "RTT 2", nameof(Row.Rtt2), pixels: 80);
        AddColumn(_grid, "RTT 3", nameof(Row.Rtt3), pixels: 80);
        AddColumn(_grid, "Note", nameof(Row.Note), 1);
        _grid.CanUserSortColumns = false;
        _grid.ItemsSource = _rows;
        StyleRows<Row>(_grid, dim: r => r.NoAnswer);

        _traceButton.Click += async (_, _) => await TraceAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _hostBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
                await TraceAsync();
        };
        var root = new DockPanel
        {
            Children =
            {
                Toolbar(Label("Host:"), _hostBox, Label("Port:"), _portBox, Label("Hops:"), _hopsBox,
                    Label("Timeout (ms):"), _timeoutBox, _namesCheck, _traceButton, _cancelButton),
                _status,
                _grid,
            },
        };
        ShowMacOnlyNotice(root, "TCP traceroute");
        Content = root;
    }

    private async Task TraceAsync()
    {
        var host = _hostBox.Text?.Trim() ?? "";
        if (host.Length == 0)
        {
            _status.Text = "Enter a host first.";
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _rows.Clear();
        _traceButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _status.Text = $"Tracing {host}...";
        var progress = new Progress<TracerouteHop>(h => _rows.Add(new Row(h.Ttl, h.Address ?? "*", h.HostName ?? "",
            Rtt(h.Rtt1Ms), Rtt(h.Rtt2Ms), Rtt(h.Rtt3Ms), h.Note ?? "", h.Address is null)));
        try
        {
            var hops = await TcpTraceroute.TraceAsync(host, (int)(_portBox.Value ?? 443), (int)(_hopsBox.Value ?? 30),
                (int)(_timeoutBox.Value ?? 2000), _namesCheck.IsChecked == true, progress, token);
            _status.Text = hops.Count == 0 ? "No hops." : hops[^1].Note == "Reached"
                ? $"Reached {host} in {hops.Count} hops."
                : $"Stopped after {hops.Count} hops without reaching {host}.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Traceroute failed: {ex.GetBaseException().Message}";
        }
        finally
        {
            _traceButton.IsEnabled = true;
            _cancelButton.IsEnabled = false;
        }
    }

    private static string Rtt(long? ms) => ms is { } value ? $"{value} ms" : "*";
}
