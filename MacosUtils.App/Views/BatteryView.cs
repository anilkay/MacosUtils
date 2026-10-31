using Avalonia.Controls;
using Avalonia.Threading;
using MacosUtils.Core.SystemInfo;

namespace MacosUtils.App.Views;

/// <summary>Battery health and state, refreshed every 30 seconds while the screen is shown (read-only).</summary>
public sealed class BatteryView : UtilityView
{
    private sealed record Row(string Property, string Value, bool Warn);

    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _status = StatusLine();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _loading;

    public BatteryView()
    {
        AddColumn(_grid, "Property", nameof(Row.Property), 1);
        AddColumn(_grid, "Value", nameof(Row.Value), 3);
        _grid.CanUserSortColumns = false;
        StyleRows<Row>(_grid, warn: r => r.Warn);

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await LoadAsync();
        var root = new DockPanel { Children = { Toolbar(refresh), _status, _grid } };
        var notMac = ShowMacOnlyNotice(root, "Battery information");
        Content = root;
        if (notMac)
            return;

        _timer.Tick += async (_, _) => await LoadAsync();
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading)
            return;
        _loading = true;
        try
        {
            var battery = await Task.Run(Battery.Get);
            if (battery is null)
            {
                _grid.ItemsSource = null;
                _status.Text = "This Mac has no battery.";
                _timer.Stop();
                return;
            }
            _grid.ItemsSource = Battery.Describe(battery).Select(d => new Row(d.Label, d.Value, d.Warn)).ToList();
            _status.Text = (battery.NeedsAttention
                ? "The battery needs attention; see System Settings › Battery › Battery Health. "
                : "") + $"Updated {DateTime.Now:T}.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read the battery: {ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }
}
