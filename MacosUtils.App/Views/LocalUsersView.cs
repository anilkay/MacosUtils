using Avalonia.Controls;
using MacosUtils.Core.SystemInfo;

namespace MacosUtils.App.Views;

/// <summary>Local user accounts (UID 500 and up), whether they are administrators, and their home and shell.</summary>
public sealed class LocalUsersView : UtilityView
{
    private sealed record Row(string Name, string FullName, string Type, int Uid, string Home, string Shell, bool CanLogIn);

    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _status = StatusLine();

    public LocalUsersView()
    {
        AddColumn(_grid, "Account", nameof(Row.Name), 1);
        AddColumn(_grid, "Full Name", nameof(Row.FullName), 2);
        AddColumn(_grid, "Type", nameof(Row.Type), pixels: 120);
        AddColumn(_grid, "UID", nameof(Row.Uid), pixels: 70);
        AddColumn(_grid, "Home", nameof(Row.Home), 2);
        AddColumn(_grid, "Shell", nameof(Row.Shell), 1);
        StyleRows<Row>(_grid, dim: r => !r.CanLogIn);

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await LoadAsync();
        var root = new DockPanel { Children = { Toolbar(refresh), _status, _grid } };
        ShowMacOnlyNotice(root, "The user list");
        Content = root;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var accounts = await Task.Run(LocalUsers.GetAccounts);
            _grid.ItemsSource = accounts.Select(a => new Row(a.Name, a.FullName, LocalUsers.FormatType(a), a.Uid, a.Home, a.Shell, a.CanLogIn)).ToList();
            _status.Text = $"{accounts.Count} account(s). Add or remove users in System Settings › Users & Groups.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read the accounts: {ex.Message}";
        }
    }
}
