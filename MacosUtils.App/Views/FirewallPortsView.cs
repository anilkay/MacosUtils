using Avalonia.Controls;
using Avalonia.Layout;
using MacosUtils.Core.Net;

namespace MacosUtils.App.Views;

/// <summary>
/// The Application Firewall's switches and app rules, next to the TCP/UDP ports processes listen on.
/// (The macOS firewall works per app, not per port, unlike the Windows Firewall rules WindowsUtils lists.)
/// </summary>
public sealed class FirewallPortsView : UtilityView
{
    private sealed record PortRow(string Protocol, int Port, string Address, string Exposure, string Process, int Pid, bool Exposed);
    private sealed record AppRow(string Rule, string Application);

    private readonly TextBlock _state = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 10) };
    private readonly DataGrid _ports = CreateGrid();
    private readonly DataGrid _apps = CreateGrid();
    private readonly TextBlock _status = StatusLine();

    public FirewallPortsView()
    {
        AddColumn(_ports, "Protocol", nameof(PortRow.Protocol), pixels: 100);
        AddColumn(_ports, "Port", nameof(PortRow.Port), pixels: 80);
        AddColumn(_ports, "Address", nameof(PortRow.Address), 1);
        AddColumn(_ports, "Reachable", nameof(PortRow.Exposure), pixels: 130);
        AddColumn(_ports, "Process", nameof(PortRow.Process), 1);
        AddColumn(_ports, "PID", nameof(PortRow.Pid), pixels: 80);
        StyleRows<PortRow>(_ports, dim: r => !r.Exposed);
        AddColumn(_apps, "Rule", nameof(AppRow.Rule), pixels: 80);
        AddColumn(_apps, "Application", nameof(AppRow.Application), 1);

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await LoadAsync();
        DockPanel.SetDock(_state, Dock.Top);

        var portsHeader = new TextBlock { Text = "Listening ports", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 0, 0, 4) };
        var appsHeader = new TextBlock { Text = "Application Firewall rules", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 10, 0, 4) };
        var grids = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,2*,Auto,*"),
            Children = { portsHeader, _ports, appsHeader, _apps },
        };
        Grid.SetRow(_ports, 1);
        Grid.SetRow(appsHeader, 2);
        Grid.SetRow(_apps, 3);
        _ports.VerticalAlignment = VerticalAlignment.Stretch;

        var root = new DockPanel { Children = { Toolbar(refresh), _state, _status, grids } };
        ShowMacOnlyNotice(root, "The Application Firewall");
        Content = root;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _status.Text = "Reading the firewall and listening ports...";
        var (status, rules, ports) = await Task.Run(() => (FirewallInfo.GetStatus(), FirewallInfo.GetAppRules(), FirewallInfo.GetListeningPorts()));
        static string OnOff(bool? value) => value switch { true => "on", false => "off", _ => "unknown" };
        _state.Text = $"Firewall: {OnOff(status.Enabled)}  •  Stealth mode: {OnOff(status.StealthMode)}  •  "
            + $"Block all incoming: {OnOff(status.BlockAll)}  •  Allow signed software: {OnOff(status.AllowSigned)}";
        _ports.ItemsSource = ports.Select(p => new PortRow(p.Protocol, p.Port, p.Address,
            p.IsExposed ? "Other machines" : "This Mac only", p.Process, p.Pid, p.IsExposed)).ToList();
        _apps.ItemsSource = rules.Select(r => new AppRow(r.AllowsIncoming ? "Allow" : "Block", r.Application)).ToList();
        _status.Text = $"{ports.Count} listening socket(s), {rules.Count} app rule(s). "
            + "Ports of other users' and system processes are only visible with sudo (lsof).";
    }
}
