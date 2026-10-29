using Avalonia.Controls;
using MacosUtils.Core.SystemInfo;

namespace MacosUtils.App.Views;

/// <summary>Environment variables of this app, or of the login shell (what a new Terminal window sees), with a filter.</summary>
public sealed class EnvironmentVariablesView : UtilityView
{
    private sealed record Row(string Name, string Value);

    private readonly ComboBox _targetBox = new() { ItemsSource = new[] { "This app (Process)", "Terminal (login shell)" }, SelectedIndex = 0, Width = 200 };
    private readonly TextBox _filterBox = new() { Width = 220, PlaceholderText = "Filter by name or value" };
    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _status = StatusLine();
    private IReadOnlyList<KeyValuePair<string, string>> _all = [];

    public EnvironmentVariablesView()
    {
        AddColumn(_grid, "Name", nameof(Row.Name), 1);
        AddColumn(_grid, "Value", nameof(Row.Value), 3);

        _targetBox.SelectionChanged += async (_, _) => await LoadAsync();
        _filterBox.TextChanged += (_, _) => ApplyFilter();
        var copy = new Button { Content = "Copy Value" };
        copy.Click += async (_, _) =>
        {
            if (_grid.SelectedItem is Row row && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, row.Value);
                _status.Text = $"Copied the value of {row.Name}.";
            }
        };
        Content = new DockPanel { Children = { Toolbar(Label("Target:"), _targetBox, _filterBox, copy), _status, _grid } };
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var source = _targetBox.SelectedIndex == 1 ? EnvironmentSource.LoginShell : EnvironmentSource.Process;
        _status.Text = source == EnvironmentSource.LoginShell ? "Running the login shell once to read its environment..." : "";
        try
        {
            _all = await Task.Run(() => EnvironmentInfo.Get(source));
            ApplyFilter();
            _status.Text = source == EnvironmentSource.LoginShell
                ? $"{_all.Count} variables from {EnvironmentInfo.LoginShell()} -l (your profile files ran once)."
                : $"{_all.Count} variables. An app opened from Finder gets launchd's short list, not your shell's.";
        }
        catch (Exception ex)
        {
            _all = [];
            ApplyFilter();
            _status.Text = $"Could not read the variables: {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var filter = _filterBox.Text?.Trim() ?? "";
        _grid.ItemsSource = _all
            .Where(v => filter.Length == 0
                || v.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || v.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(v => new Row(v.Key, v.Value))
            .ToList();
    }
}
