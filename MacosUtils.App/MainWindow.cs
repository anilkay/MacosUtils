using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MacosUtils.App.Views;

namespace MacosUtils.App;

/// <summary>The shell: a sidebar with the utilities, and the selected one with its title and description.</summary>
public sealed class MainWindow : Window
{
    /// <param name="Icon">An emoji; Apple Color Emoji draws it on macOS (SF Symbols are not available to Avalonia).</param>
    private sealed record Utility(string Name, string Icon, string Description, Func<Control> Factory);

    private static readonly Utility[] Utilities =
    [
        new("Folder Size Analyzer", "📁", "See what takes up space inside a folder.", () => new FolderSizeView()),
        new("Largest Files", "📄", "Find the biggest files on your volumes.", () => new LargestFilesView()),
        new("Duplicate Files", "🗂️", "Find identical files and free up space.", () => new DuplicateFilesView()),
        new("System Information", "💻", "macOS, chip, memory, installed runtimes and security settings.", () => new SystemInfoView()),
        new("Disk Info", "💽", "Capacity and free space for every volume.", () => new DiskInfoView()),
        new("Network Info", "🌐", "Network interfaces, addresses and a quick ping.", () => new NetworkInfoView()),
        new("TCP Traceroute", "🛰️", "Trace the network path to a host over TCP.", () => new TcpTracerouteView()),
        new("Firewall & Ports", "🛡️", "Application Firewall rules and the ports processes listen on.", () => new FirewallPortsView()),
        new("File Hash Calculator", "#️⃣", "Compute and verify MD5, SHA-1, SHA-256 and SHA-512 hashes.", () => new FileHashView()),
        new("Environment Variables", "🔧", "This app's variables and what a Terminal window sees.", () => new EnvironmentVariablesView()),
        new("Startup Items", "🚀", "Launch agents and daemons that start with the Mac.", () => new StartupItemsView()),
        new("Battery Health", "🔋", "Charge, maximum capacity, cycle count and Apple's battery condition.", () => new BatteryView()),
        new("Homebrew", "🍺", "Is Homebrew installed, and what it installed: formulae, casks and their sizes.", () => new HomebrewView()),
        new("Local Users", "👥", "Local user accounts, administrators and their shells.", () => new LocalUsersView()),
        new("AI Chat", "💬", "Ask an AI assistant that can inspect this Mac.", () => new ChatView()),
    ];

    private readonly Dictionary<Utility, Control> _cache = [];
    private readonly ContentControl _content = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _description = new();

    public MainWindow()
    {
        Title = "macOS Utils";
        Width = 1200;
        Height = 760;
        MinWidth = 960;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            Icon = new WindowIcon(new Bitmap(AssetLoader.Open(new Uri("avares://MacosUtils/Assets/logo.png"))));
        }
        catch (Exception)
        {
            // The Dock shows the bundle's icon on macOS anyway.
        }

        var nav = new ListBox
        {
            ItemsSource = Utilities,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<Utility>((u, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = u?.Icon, Classes = { "navIcon" } },
                    new TextBlock { Text = u?.Name, VerticalAlignment = VerticalAlignment.Center },
                },
            }),
        };
        nav.Classes.Add("nav");
        nav.SelectionChanged += (_, _) =>
        {
            if (nav.SelectedItem is Utility utility)
                Navigate(utility);
        };

        var appTitle = new TextBlock { Text = "macOS Utils", Margin = new Thickness(18, 18, 12, 14) };
        appTitle.Classes.Add("appTitle");
        DockPanel.SetDock(appTitle, Dock.Top);
        var sidebar = new Border
        {
            Width = 250,
            Child = new DockPanel { Children = { appTitle, nav } },
        };
        sidebar.Classes.Add("sidebar");
        DockPanel.SetDock(sidebar, Dock.Left);

        _title.Classes.Add("pageTitle");
        _description.Classes.Add("muted");
        _description.Margin = new Thickness(0, 2, 0, 14);
        var header = new StackPanel { Children = { _title, _description } };
        DockPanel.SetDock(header, Dock.Top);
        var page = new DockPanel
        {
            Margin = new Thickness(24, 16, 24, 20),
            Children = { header, _content },
        };

        Content = new DockPanel { Children = { sidebar, page } };
        nav.SelectedIndex = 0;
    }

    // Screens are created on first visit and kept, so a running scan or a chat survives switching.
    private void Navigate(Utility utility)
    {
        if (!_cache.TryGetValue(utility, out var view))
            _cache[utility] = view = utility.Factory();
        _title.Text = utility.Name;
        _description.Text = utility.Description;
        _content.Content = view;
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var view in _cache.Values.OfType<IDisposable>())
            view.Dispose();
        base.OnClosed(e);
    }
}
