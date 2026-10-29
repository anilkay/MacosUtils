using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MacosUtils.App;

/// <summary>
/// Small modal dialogs (Avalonia has no MessageBox). All are async: they open as a sheet-like
/// modal window over <paramref name="owner"/>'s window and complete when it closes.
/// </summary>
public static class Dialogs
{
    /// <summary>A message with an OK button.</summary>
    public static async Task ShowAsync(Visual? owner, string title, string message)
    {
        var window = CreateWindow(title, message, out var buttons, out _);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80 };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => window.Close();
        buttons.Children.Add(ok);
        await ShowCoreAsync(window, owner);
    }

    /// <summary>
    /// A yes/no question. With <paramref name="defaultNo"/> (the default) Return and Escape both
    /// answer no, so a key pressed by accident never confirms a deletion or a permission.
    /// </summary>
    public static async Task<bool> ConfirmAsync(Visual? owner, string title, string message,
        string yes = "Yes", string no = "No", bool defaultNo = true)
    {
        var result = false;
        var window = CreateWindow(title, message, out var buttons, out _);
        var noButton = new Button { Content = no, IsCancel = true, IsDefault = defaultNo, MinWidth = 80 };
        var yesButton = new Button { Content = yes, IsDefault = !defaultNo, MinWidth = 80 };
        if (!defaultNo)
            yesButton.Classes.Add("accent");
        noButton.Click += (_, _) => window.Close();
        yesButton.Click += (_, _) =>
        {
            result = true;
            window.Close();
        };
        buttons.Children.Add(noButton);
        buttons.Children.Add(yesButton);
        window.Opened += (_, _) => (defaultNo ? noButton : yesButton).Focus();
        await ShowCoreAsync(window, owner);
        return result;
    }

    /// <summary>Read-only monospaced text (a config snippet) with a copy button.</summary>
    public static async Task ShowTextAsync(Visual? owner, string title, string intro, string text)
    {
        var window = CreateWindow(title, intro, out var buttons, out var body);
        window.Width = 560;
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 200,
            Margin = new Thickness(0, 10, 0, 0),
        };
        box.Classes.Add("mono");
        body.Children.Add(box);

        var copied = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        copied.Classes.Add("muted");
        var copy = new Button { Content = "Copy", MinWidth = 80 };
        copy.Classes.Add("accent");
        copy.Click += async (_, _) =>
        {
            try
            {
                if (window.Clipboard is { } clipboard)
                {
                    await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
                    copied.Text = "Copied to the clipboard.";
                }
            }
            catch (Exception ex)
            {
                copied.Text = $"Could not copy ({ex.Message}); select the text and press Command-C.";
            }
        };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 80 };
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(copied);
        buttons.Children.Add(close);
        buttons.Children.Add(copy);
        await ShowCoreAsync(window, owner);
    }

    private static Window CreateWindow(string title, string message, out StackPanel buttons, out StackPanel body)
    {
        buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var titleBlock = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(titleBlock, Dock.Top);
        body = new StackPanel
        {
            Children =
            {
                // Selectable: users copy paths and error texts out of these dialogs.
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            },
        };
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                Margin = new Thickness(20),
                LastChildFill = true,
                Children = { buttons, titleBlock, body },
            },
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                window.Close();
        };
        return window;
    }

    private static async Task ShowCoreAsync(Window window, Visual? owner)
    {
        var ownerWindow = (owner is null ? null : TopLevel.GetTopLevel(owner) as Window) ?? App.MainWindow;
        if (ownerWindow is { IsVisible: true })
        {
            await window.ShowDialog(ownerWindow);
            return;
        }
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        await closed.Task;
    }
}
