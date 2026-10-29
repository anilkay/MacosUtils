using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MacosUtils.Core;
using MacosUtils.Core.IO;

namespace MacosUtils.App.Views;

/// <summary>Base class for all utility screens, with the shared UI helpers.</summary>
public abstract class UtilityView : UserControl
{
    /// <summary>A read-only grid with explicit columns (add them with <see cref="AddColumn"/>).</summary>
    protected static DataGrid CreateGrid(bool multiSelect = false) => new()
    {
        IsReadOnly = true,
        AutoGenerateColumns = false,
        CanUserReorderColumns = false,
        CanUserResizeColumns = true,
        CanUserSortColumns = true,
        SelectionMode = multiSelect ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        HeadersVisibility = DataGridHeadersVisibility.Column,
    };

    /// <summary>
    /// Adds a text column bound to <paramref name="property"/>. <paramref name="sortBy"/> names a
    /// numeric property to sort on (sizes are shown as "1.5 GB" but must sort by bytes).
    /// </summary>
    protected static DataGridTextColumn AddColumn(DataGrid grid, string header, string property,
        double star = 1, string? sortBy = null, double? pixels = null)
    {
        var column = new DataGridTextColumn
        {
            Header = header,
            Binding = new ReflectionBinding(property) { Mode = BindingMode.OneWay },
            Width = pixels is { } px ? new DataGridLength(px) : new DataGridLength(star, DataGridLengthUnitType.Star),
            SortMemberPath = sortBy ?? property,
        };
        grid.Columns.Add(column);
        return column;
    }

    /// <summary>Puts <c>warn</c>/<c>dim</c> classes on rows, from the row item (styled in App.axaml).</summary>
    protected static void StyleRows<T>(DataGrid grid, Func<T, bool>? warn = null, Func<T, bool>? dim = null)
    {
        grid.LoadingRow += (_, e) =>
        {
            if (e.Row.DataContext is not T item)
                return;
            e.Row.Classes.Set("warn", warn?.Invoke(item) ?? false);
            e.Row.Classes.Set("dim", dim?.Invoke(item) ?? false);
        };
    }

    protected static WrapPanel Toolbar(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        panel.Classes.Add("toolbar");
        foreach (var control in controls)
            panel.Children.Add(control);
        DockPanel.SetDock(panel, Dock.Top);
        return panel;
    }

    protected static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text };
        label.Classes.Add("label");
        return label;
    }

    protected static Button AccentButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add("accent");
        return button;
    }

    /// <summary>A muted status line docked at the bottom.</summary>
    protected static TextBlock StatusLine(string text = "")
    {
        var status = new TextBlock { Text = text, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
        status.Classes.Add("muted");
        DockPanel.SetDock(status, Dock.Bottom);
        return status;
    }

    /// <summary>A screen that only works on macOS says so instead of showing an empty grid.</summary>
    protected static bool ShowMacOnlyNotice(DockPanel root, string what)
    {
        if (OperatingSystem.IsMacOS())
            return false;
        var notice = new TextBlock { Text = $"{what} is only available on macOS.", Margin = new Thickness(0, 0, 0, 8) };
        notice.Classes.Add("muted");
        DockPanel.SetDock(notice, Dock.Top);
        root.Children.Insert(0, notice);
        return true;
    }

    protected async Task<string?> PickFolderAsync(string title)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    protected async Task<string?> PickFileAsync(string title)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    /// <summary>Shows a file in Finder; reports a failure in a dialog.</summary>
    protected async Task RevealInFinderAsync(string path)
    {
        try
        {
            Finder.Reveal(path);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowAsync(this, "Show in Finder", $"Could not open Finder: {ex.Message}");
        }
    }

    protected static string FormatBytes(long bytes) => ByteFormatter.FormatBytes(bytes);

    /// <summary>"~/Documents" instead of "/Users/me/Documents", as Finder shows paths.</summary>
    protected static string ShortenHome(string path)
    {
        var home = AppPaths.Home;
        return path.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }
}
