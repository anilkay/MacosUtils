using Avalonia.Controls;
using MacosUtils.Core.Net;

namespace MacosUtils.App.Views;

/// <summary>A server's TLS: what a handshake negotiates, its certificate chain, and which TLS versions it accepts.</summary>
public sealed class TlsCheckView : UtilityView
{
    private sealed record Row(string Property, string Value, bool Warn);

    private readonly TextBox _hostBox = new() { Width = 240, PlaceholderText = "Host (e.g. apple.com)" };
    private readonly NumericUpDown _portBox = new() { Minimum = 1, Maximum = 65535, Value = 443, Width = 120, FormatString = "0" };
    private readonly Button _checkButton = AccentButton("Check");
    private readonly Button _cancelButton = new() { Content = "Cancel", IsEnabled = false };
    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _status = StatusLine(
        "Connects to the server and does TLS handshakes only; no data is sent. Each TLS version is tried with macOS's own openssl.");
    private CancellationTokenSource? _cts;

    public TlsCheckView()
    {
        AddColumn(_grid, "Property", nameof(Row.Property), 1);
        AddColumn(_grid, "Value", nameof(Row.Value), 4);
        _grid.CanUserSortColumns = false;
        StyleRows<Row>(_grid, warn: r => r.Warn);

        _checkButton.Click += async (_, _) => await CheckAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _hostBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
                await CheckAsync();
        };
        Content = new DockPanel
        {
            Children = { Toolbar(Label("Host:"), _hostBox, Label("Port:"), _portBox, _checkButton, _cancelButton), _status, _grid },
        };
    }

    private async Task CheckAsync()
    {
        var host = _hostBox.Text?.Trim() ?? "";
        var port = (int)(_portBox.Value ?? 443);
        try
        {
            (host, port) = TlsProbe.Validate(host, port);
        }
        catch (ArgumentException ex)
        {
            _status.Text = ex.Message;
            return;
        }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _checkButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _grid.ItemsSource = null;
        _status.Text = $"Checking {host}:{port}...";
        try
        {
            var versions = TlsProbe.CheckVersionsAsync(host, port, cts.Token);
            TlsProbeResult? result = null;
            string? failure = null;
            try
            {
                result = await TlsProbe.ProbeAsync(host, port, 10000, cts.Token);
            }
            catch (IOException ex)
            {
                failure = ex.Message;
            }
            var rows = TlsProbe.Describe(result, await versions).Select(d => new Row(d.Label, d.Value, d.Warn)).ToList();
            _grid.ItemsSource = rows;
            _status.Text = failure ?? (rows.Any(r => r.Warn)
                ? "Highlighted rows need a look."
                : $"{host}:{port} looks fine.");
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"The check failed: {ex.GetBaseException().Message}";
        }
        finally
        {
            if (_cts == cts)
            {
                _checkButton.IsEnabled = true;
                _cancelButton.IsEnabled = false;
            }
        }
    }
}
