using System.Runtime.InteropServices;
using Avalonia.Controls;
using MacosUtils.Core.SystemInfo;

namespace MacosUtils.App.Views;

/// <summary>macOS version, Mac model, chip, memory, uptime, installed runtimes and security settings.</summary>
public sealed class SystemInfoView : UtilityView
{
    private sealed record Row(string Property, string Value, bool Muted = false);

    private readonly DataGrid _grid = CreateGrid();
    private readonly TextBlock _status = StatusLine();

    public SystemInfoView()
    {
        AddColumn(_grid, "Property", nameof(Row.Property), 1);
        AddColumn(_grid, "Value", nameof(Row.Value), 3);
        _grid.CanUserSortColumns = false;
        StyleRows<Row>(_grid, dim: r => r.Muted);

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await LoadAsync();
        var root = new DockPanel { Children = { Toolbar(refresh), _status, _grid } };
        ShowMacOnlyNotice(root, "Most of this information");
        Content = root;
        _ = LoadAsync();
    }

    // Runtimes, features and vm_stat run tools and walk folders: done off the UI thread.
    private async Task LoadAsync()
    {
        _status.Text = "Reading system information...";
        var rows = await Task.Run(() =>
        {
            var list = Collect().ToList();
            AddRuntimeRows(list, ".NET", DotNetDetector.Find());
            AddRuntimeRows(list, "Java", JavaDetector.Find());
            AddRuntimeRows(list, "Python", PythonDetector.Find());
            AddRuntimeRows(list, "Node.js", NodeDetector.Find());
            AddRuntimeRows(list, "Go", GoDetector.Find());
            AddRuntimeRows(list, "Rust", RustDetector.Find());
            AddRuntimeRows(list, "Ruby", RubyDetector.Find());
            AddRuntimeRows(list, "PHP", PhpDetector.Find());
            AddRuntimeRows(list, "Xcode", XcodeDetector.Find());
            foreach (var feature in MacFeatures.Get())
                list.Add(new Row(feature.Name, feature.Display, feature.State != FeatureState.Enabled));
            return list;
        });
        _grid.ItemsSource = rows;
        _status.Text = $"Updated {DateTime.Now:T}.";
    }

    private static void AddRuntimeRows(List<Row> rows, string name, IReadOnlyList<RuntimeInstallation> installs)
    {
        if (installs.Count == 0)
        {
            rows.Add(new Row(name, "Not installed", true));
            return;
        }
        var main = installs[0];
        rows.Add(new Row(name, name == ".NET" ? $"{main.Vendor} {main.Version}" : main.Display));
        rows.Add(new Row($"{name} Location", $"{ShortenHome(main.Home)}  ({main.Source})"));
        if (installs.Count > 1)
        {
            rows.Add(new Row($"Other {name} Installs", string.Join(", ", installs.Skip(1).Take(12)
                .Select(i => name == ".NET" ? $"{i.Vendor} {i.Version}" : i.Display))));
        }
    }

    private static IEnumerable<Row> Collect()
    {
        yield return new("Operating System", SystemDetails.MacOSDisplayName());
        yield return new("Kernel", RuntimeInformation.OSDescription);
        var model = SystemDetails.ModelIdentifier();
        if (model.Length > 0)
            yield return new("Mac Model", model);
        yield return new("Chip", SystemDetails.CpuName());
        var (performance, efficiency) = SystemDetails.CoreCounts();
        yield return new("Cores", performance > 0
            ? $"{Environment.ProcessorCount} ({performance} performance, {efficiency} efficiency)"
            : Environment.ProcessorCount.ToString());
        yield return new("Architecture", RuntimeInformation.OSArchitecture
            + (SystemDetails.IsTranslated() ? " (this app runs under Rosetta 2)" : ""));
        yield return new("Computer Name", Environment.MachineName);
        yield return new("Current User", Environment.UserName);

        var (total, available) = SystemDetails.Memory();
        if (total > 0)
        {
            yield return new("Memory", FormatBytes((long)total));
            if (available > 0)
            {
                yield return new("Available Memory", FormatBytes((long)available));
                yield return new("Memory In Use", $"{100 - available * 100.0 / total:0.#}%");
            }
        }
        var uptime = SystemDetails.Uptime();
        var boot = SystemDetails.BootTime();
        yield return new("Uptime", $"{(int)uptime.TotalDays} days, {uptime.Hours}h {uptime.Minutes}m"
            + (boot is { } b ? $" (since {b.LocalDateTime:g})" : ""));
        var (width, height) = SystemDetails.ScreenSize();
        if (width > 0 && height > 0)
            yield return new("Main Display", $"{width} x {height}");
        yield return new("App Runtime", RuntimeInformation.FrameworkDescription);
    }
}
