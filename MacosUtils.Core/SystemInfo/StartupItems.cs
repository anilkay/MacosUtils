namespace MacosUtils.Core.SystemInfo;

/// <summary>A program launchd starts at login or boot.</summary>
/// <param name="Label">The launchd label, e.g. com.example.updater.</param>
/// <param name="Command">The program and its arguments.</param>
/// <param name="Location">Which folder the plist is in, e.g. "~/Library/LaunchAgents".</param>
/// <param name="RunAtLoad">Starts as soon as it is loaded (login or boot).</param>
/// <param name="KeepAlive">launchd restarts it when it exits.</param>
/// <param name="Disabled">The plist itself marks the job disabled.</param>
public sealed record StartupItem(
    string Label,
    string Command,
    string Location,
    string PlistPath,
    bool RunAtLoad,
    bool KeepAlive,
    bool Disabled);

/// <summary>
/// Read-only view of the launchd jobs that start with the Mac: per-user LaunchAgents and the
/// system-wide LaunchAgents and LaunchDaemons that apps install (Apple's own jobs in /System are
/// left out). This is what Login Items &amp; Extensions › "Allow in the Background" lists.
/// Login items added by apps through SMAppService are kept in a private database that needs root
/// (sfltool dumpbtm), so they are not listed.
/// </summary>
public static class StartupItems
{
    public static IReadOnlyList<(string Label, string Path)> Locations => [
        ("~/Library/LaunchAgents", Path.Combine(AppPaths.Home, "Library", "LaunchAgents")),
        ("/Library/LaunchAgents", "/Library/LaunchAgents"),
        ("/Library/LaunchDaemons", "/Library/LaunchDaemons"),
    ];

    /// <summary>All jobs, by location then label; unreadable folders and plists are skipped.</summary>
    public static IReadOnlyList<StartupItem> GetItems()
    {
        var items = new List<StartupItem>();
        if (!OperatingSystem.IsMacOS())
            return items;

        foreach (var (label, folder) in Locations)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.plist");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                var item = Describe(file, label);
                if (item is not null)
                    items.Add(item);
            }
        }
        return items;
    }

    /// <summary>Reads one launchd plist; null when it cannot be parsed.</summary>
    public static StartupItem? Describe(string plistPath, string location) =>
        Plist.LoadDictionary(plistPath) is { } dict ? FromDictionary(dict, plistPath, location) : null;

    /// <summary>Builds the item from a parsed plist; public for tests.</summary>
    public static StartupItem FromDictionary(Dictionary<string, object?> dict, string plistPath, string location)
    {
        var label = dict.GetValueOrDefault("Label") as string ?? Path.GetFileNameWithoutExtension(plistPath);
        var program = dict.GetValueOrDefault("Program") as string;
        var arguments = (dict.GetValueOrDefault("ProgramArguments") as List<object?>)?.OfType<string>().ToList() ?? [];
        string command;
        if (program is not null)
            command = string.Join(' ', new[] { program }.Concat(arguments.Skip(1)).Select(Quote));
        else
            command = string.Join(' ', arguments.Select(Quote));
        // Jobs an app registers through SMAppService name their program inside the app bundle.
        if (command.Length == 0 && dict.GetValueOrDefault("BundleProgram") is string bundleProgram)
        {
            var bundles = (dict.GetValueOrDefault("AssociatedBundleIdentifiers") as List<object?>)?.OfType<string>().ToList() ?? [];
            if (dict.GetValueOrDefault("AssociatedBundleIdentifiers") is string single)
                bundles = [single];
            command = bundles.Count > 0 ? $"{bundleProgram} (in {string.Join(", ", bundles)})" : bundleProgram;
        }
        if (command.Length == 0)
            command = "(no program in the plist)";

        // KeepAlive is either a bool or a dictionary of conditions (which also means "restart it").
        var keepAlive = dict.GetValueOrDefault("KeepAlive") switch
        {
            bool b => b,
            Dictionary<string, object?> => true,
            _ => false,
        };
        return new StartupItem(
            label,
            command,
            location,
            plistPath,
            dict.GetValueOrDefault("RunAtLoad") is true,
            keepAlive,
            dict.GetValueOrDefault("Disabled") is true);
    }

    private static string Quote(string argument) =>
        argument.Contains(' ') ? $"\"{argument}\"" : argument;
}
