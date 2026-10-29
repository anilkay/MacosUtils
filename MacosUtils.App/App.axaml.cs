using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MacosUtils.App;

public partial class App : Application
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>The window dialogs open over.</summary>
    public static Window? MainWindow =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }

    private void About_Click(object? sender, EventArgs e) =>
        _ = Dialogs.ShowAsync(MainWindow, "About macOS Utils",
            $"macOS Utils {Version}\n\nSmall Mac utilities in one window, with an AI assistant that can inspect "
            + "this Mac through read-only tools.\n\nhttps://github.com/anilkay/MacosUtils");
}
