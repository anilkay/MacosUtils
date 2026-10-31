using System.ClientModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using MacosUtils.Core;
using MacosUtils.Core.Hashing;
using MacosUtils.Core.IO;
using MacosUtils.Core.Net;
using MacosUtils.Core.SystemInfo;

namespace MacosUtils.AI;

/// <summary>
/// Read-only Mac inspection tools exposed to the AI agent, the MCP server and the CLI. All methods
/// are plain static functions, so they are also directly usable from console apps and tests.
/// Every method catches its own errors and returns them as text. (The class keeps WindowsUtils'
/// name so both apps read the same.)
/// </summary>
public static class PcTools
{
    // Concrete OpenAI client (not IChatClient): the 7-argument AsAIAgent overload
    // used by ChatSession is defined on this type.
    internal static ChatClient CreateChatClient(string model, string apiKey, Uri endpoint) =>
        new(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint });

    // One tool for the AI agent: the function itself, plus whether it reads private data
    // (file contents or environment variable values) and must ask the user before each run,
    // and whether it talks to the outside world (network/LLM) rather than this Mac only.
    internal readonly record struct PcTool(AIFunction Function, bool NeedsApproval, bool OpenWorld = false);

    // AIFunctionFactory marshals results to JsonElement by default. Over MCP the SDK would then
    // serialize that JsonElement a second time, so text arrived quoted with visible \n escapes
    // (McpServerHost.cs). Pass the raw result through instead; every consumer gets the plain string.
    // Use this factory for every tool exposed to the agent or over MCP.
    internal static AIFunction CreateFunction(Delegate method, string? name = null, string? description = null) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            MarshalResult = static (result, _, _) => new ValueTask<object?>(result),
        });

    /// <summary>The read-only Mac inspection tools. Single source of truth: the chat agent
    /// (<see cref="ChatSession"/>) and the MCP server (<see cref="McpServerHost"/>) both use this list.</summary>
    internal static PcTool[] CreateTools() =>
    [
        new(CreateFunction(new Func<string>(GetCurrentTime)), false),
        new(CreateFunction(new Func<string>(GetSystemInfo)), false),
        new(CreateFunction(new Func<string>(GetDotNetVersions)), false),
        new(CreateFunction(new Func<string>(GetJavaVersion)), false),
        new(CreateFunction(new Func<string>(GetPythonVersion)), false),
        new(CreateFunction(new Func<string>(GetNodeVersion)), false),
        new(CreateFunction(new Func<string>(GetMacFeatures)), false),
        new(CreateFunction(new Func<int, string>(GetProcesses)), false),
        new(CreateFunction(new Func<string>(GetVolumes)), false),
        new(CreateFunction(new Func<string, string, int, string>(ListFiles)), false),
        new(CreateFunction(new Func<string, string, int, string>(ListFolders)), false),
        new(CreateFunction(new Func<string, int, bool, string>(GetLargestFiles)), false),
        new(CreateFunction(new Func<string, int, int, CancellationToken, string>(FindDuplicateFiles)), false),
        new(CreateFunction(new Func<string, int, CancellationToken, string>(GetFolderSizes)), false),
        new(CreateFunction(new Func<string>(GetStartupItems)), false),
        new(CreateFunction(new Func<int, CancellationToken, string>(GetHomebrewPackages)), false),
        new(CreateFunction(new Func<string>(GetBatteryHealth)), false),
        new(CreateFunction(new Func<string>(GetLocalUsers)), false),
        new(CreateFunction(new Func<string>(GetNetworkAdapters)), false),
        new(CreateFunction(new Func<string>(GetFirewallStatus)), false),
        new(CreateFunction(new Func<string>(GetListeningPorts)), false),
        // Outbound network calls deserve the same gate as private-data reads: the host name is
        // resolved via DNS, so embedded data (e.g. exfiltrated from an approved file) reaches the
        // attacker's own server, and traceroute probes inner networks. Reads of file contents and
        // environment values (which can hold secrets) come with the same rule. Chat wraps these in
        // ApprovalRequiredAIFunction; the MCP server in its approval gate.
        new(CreateFunction(new Func<string, int, CancellationToken, Task<string>>(PingHost)), true, true),
        new(CreateFunction(new Func<string, int, int, bool, int, CancellationToken, Task<string>>(TraceTcpRoute)), true, true),
        new(CreateFunction(new Func<string, int, string>(ReadTextFile)), true),
        new(CreateFunction(new Func<string, string>(GetEnvironmentVariable)), true),
        new(CreateFunction(new Func<string, int, string>(ListEnvironmentVariables)), true),
        new(CreateFunction(new Func<string, string, string>(ComputeFileHash)), false),
        new(CreateFunction(new Func<string, string, string>(VerifyFileHash)), false),
    ];

    [Description("Gets the current date and time on this Mac.")]
    public static string GetCurrentTime()
    {
        try
        {
            return DateTime.Now.ToString("F");
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Gets system info: computer name, user, macOS version, Mac model, chip and core counts, architecture, "
        + "total/available memory and memory in use, uptime, main display resolution and the .NET runtime.")]
    public static string GetSystemInfo()
    {
        try
        {
            var lines = new List<string>
            {
                $"Computer: {Environment.MachineName}",
                $"User: {Environment.UserName}",
                $"OS: {SystemDetails.MacOSDisplayName()}",
                $"Architecture: {RuntimeInformation.OSArchitecture}"
                    + (SystemDetails.IsTranslated() ? " (this app runs under Rosetta 2)" : ""),
            };
            var model = SystemDetails.ModelIdentifier();
            if (model.Length > 0)
                lines.Add($"Model: {model}");
            lines.Add($"Chip: {SystemDetails.CpuName()}");
            var (performance, efficiency) = SystemDetails.CoreCounts();
            lines.Add(performance > 0
                ? $"Cores: {Environment.ProcessorCount} ({performance} performance, {efficiency} efficiency)"
                : $"Cores: {Environment.ProcessorCount}");

            var (total, available) = SystemDetails.Memory();
            if (total > 0)
            {
                lines.Add($"Memory: {ByteFormatter.FormatBytes((long)total)}");
                if (available > 0)
                {
                    lines.Add($"Available Memory: {ByteFormatter.FormatBytes((long)available)}");
                    lines.Add($"Memory In Use: {100 - available * 100.0 / total:0.#}%");
                }
            }

            var uptime = SystemDetails.Uptime();
            lines.Add($"Uptime: {(int)uptime.TotalDays} days, {uptime.Hours}h {uptime.Minutes}m");

            var (width, height) = SystemDetails.ScreenSize();
            if (width > 0 && height > 0)
                lines.Add($"Main Display: {width} x {height}");

            lines.Add($"Runtime: {Environment.Version}");
            lines.Add($"Framework: {RuntimeInformation.FrameworkDescription}");
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Gets the installed .NET SDKs and runtimes (version, kind and folder), newest first. Read-only.")]
    public static string GetDotNetVersions()
    {
        try
        {
            var installs = DotNetDetector.Find();
            if (installs.Count == 0)
                return ".NET is not installed (nothing found in /usr/local/share/dotnet, ~/.dotnet, DOTNET_ROOT or Homebrew).";
            return string.Join(Environment.NewLine, installs.Take(40).Select(r => $"{r.Vendor} {r.Version} at {r.Home} [{r.Source}]"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Gets the installed Java version(s) (JDK/JRE): version, vendor, install folder and where it was found "
        + "(JAVA_HOME, java_home default, /Library/Java/JavaVirtualMachines, Homebrew, SDKMAN or PATH). The first one listed is the default. Read-only.")]
    public static string GetJavaVersion() =>
        DescribeRuntimes(JavaDetector.Find, "Java", "JAVA_HOME, /Library/Java/JavaVirtualMachines, Homebrew, SDKMAN or PATH");

    [Description("Gets the installed Python version(s): version, vendor, install folder and where it was found "
        + "(PATH, Homebrew, python.org, the Command Line Tools, pyenv or uv). The first one listed is what a terminal runs. Read-only.")]
    public static string GetPythonVersion() =>
        DescribeRuntimes(PythonDetector.Find, "Python", "PATH, Homebrew, python.org, the Command Line Tools, pyenv or uv");

    [Description("Gets the installed Node.js version(s): version, install folder and where it was found "
        + "(PATH, Homebrew, nvm, Volta or fnm). The first one listed is what a terminal runs. Read-only.")]
    public static string GetNodeVersion() =>
        DescribeRuntimes(NodeDetector.Find, "Node.js", "PATH, Homebrew, nvm, Volta or fnm");

    private static string DescribeRuntimes(Func<IReadOnlyList<RuntimeInstallation>> find, string name, string searched)
    {
        try
        {
            var installs = find();
            if (installs.Count == 0)
                return $"{name} is not installed (nothing found via {searched}).";
            return string.Join(Environment.NewLine, installs.Take(20).Select((r, i) =>
                $"{(i == 0 ? "Default: " : "")}{r.Display} at {r.Home} [{r.Source}]"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Checks macOS security and sharing settings: System Integrity Protection, Gatekeeper, FileVault, "
        + "the Application Firewall, Remote Login (SSH), Screen Sharing, File Sharing, Remote Management, "
        + "and whether the Command Line Tools, Rosetta 2 and Homebrew are installed. Read-only.")]
    public static string GetMacFeatures()
    {
        try
        {
            var features = MacFeatures.Get();
            if (features.Count == 0)
                return "(could not read the macOS features)";
            return string.Join(Environment.NewLine, features.Select(f => $"{f.Name}: {f.Display}"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists the top running processes by memory usage.")]
    public static string GetProcesses(
        [Description("Maximum number of processes to return (1-30).")] int maxProcesses = 10)
    {
        try
        {
            maxProcesses = Math.Clamp(maxProcesses, 1, 30);
            var rows = new List<(string Name, int Id, long Mb)>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    long mb;
                    try
                    {
                        mb = process.WorkingSet64 / 1024 / 1024;
                    }
                    catch
                    {
                        continue;
                    }
                    rows.Add((process.ProcessName, process.Id, mb));
                }
            }
            return string.Join(Environment.NewLine,
                rows.OrderByDescending(r => r.Mb).Take(maxProcesses).Select(r => $"{r.Name} (PID {r.Id}, {r.Mb} MB)"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists mounted volumes (the startup disk and everything under /Volumes) with file system, "
        + "local/network/removable, and free/total space (and free %).")]
    public static string GetVolumes()
    {
        try
        {
            var volumes = Volumes.GetVolumes();
            if (volumes.Count == 0)
                return "(no volumes)";
            return string.Join(Environment.NewLine, volumes.Select(v =>
                $"{v.Name} at {v.MountPoint} ({v.FileSystem}{(v.IsNetwork ? ", network" : v.IsRemovable ? ", removable" : "")}) "
                + $"free {ByteFormatter.FormatBytes(v.FreeBytes)} of {ByteFormatter.FormatBytes(v.TotalBytes)} ({v.FreePercent:0.#}% free)"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists files and folders in a directory. Read-only.")]
    public static string ListFiles(
        [Description("Directory path (e.g. /Users/me/Projects or ~/Projects) or a shortcut: Home, Documents, Desktop, Downloads, Applications.")] string directory,
        [Description("Search pattern, e.g. *.log.")] string pattern = "*",
        [Description("Maximum entries to return (1-100).")] int maxResults = 50)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found (network and automount paths are not allowed): {directory}";
            maxResults = Math.Clamp(maxResults, 1, 100);
            if (string.IsNullOrWhiteSpace(pattern) || pattern == "*.*")
                pattern = "*";
            var entries = Directory.EnumerateFileSystemEntries(resolved, pattern).Take(maxResults).ToArray();
            return entries.Length == 0 ? "(empty)" : string.Join(Environment.NewLine, entries);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists subfolders in a directory. Read-only.")]
    public static string ListFolders(
        [Description("Directory path (e.g. /Users/me/Projects or ~/Projects) or a shortcut: Home, Documents, Desktop, Downloads, Applications.")] string directory,
        [Description("Search pattern, e.g. Proj*.")] string pattern = "*",
        [Description("Maximum entries to return (1-100).")] int maxResults = 50)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found (network and automount paths are not allowed): {directory}";
            maxResults = Math.Clamp(maxResults, 1, 100);
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = "*";
            var entries = Directory.EnumerateDirectories(resolved, pattern).Take(maxResults).ToArray();
            return entries.Length == 0 ? "(empty)" : string.Join(Environment.NewLine, entries);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists the largest files in a folder, sorted by size descending. Use for 'biggest files' questions.")]
    public static string GetLargestFiles(
        [Description("Directory path (e.g. ~/Movies) or a shortcut: Home, Documents, Desktop, Downloads, Applications.")] string directory = "Documents",
        [Description("How many files to return (1-50).")] int top = 10,
        [Description("Search subfolders too.")] bool recursive = true)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found (network and automount paths are not allowed): {directory}";
            top = Math.Clamp(top, 1, 50);

            var (files, _, errors) = FileScanner.FindLargestFiles([resolved], top, recursive: recursive);
            if (errors.Count > 0)
                return $"Error: {errors[0].Message}";
            if (files.Count == 0)
                return "(empty)";
            return string.Join(Environment.NewLine,
                files.Select(f => $"{ByteFormatter.FormatBytes(f.Size),-10} {f.FullPath}"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Finds duplicate files (identical content, confirmed by SHA-256) in a folder and its subfolders. "
        + "Returns groups sorted by space that could be freed. Read-only: it never deletes anything.")]
    public static string FindDuplicateFiles(
        [Description("Directory path (e.g. ~/Pictures) or a shortcut: Home, Documents, Desktop, Downloads.")] string directory = "Documents",
        [Description("Ignore files smaller than this many megabytes (0 = any size).")] int minSizeMb = 1,
        [Description("Maximum number of duplicate groups to return (1-50).")] int maxGroups = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found (network and automount paths are not allowed): {directory}";
            maxGroups = Math.Clamp(maxGroups, 1, 50);
            var minSize = Math.Max(0, minSizeMb) * 1024L * 1024L;

            var (groups, scanned, errors) = DuplicateFinder.FindDuplicates([resolved], minSize, cancellationToken: cancellationToken);
            if (errors.Count > 0)
                return $"Error: {errors[0].Message}";
            if (groups.Count == 0)
                return $"No duplicates found ({scanned:N0} files scanned).";

            var lines = new List<string>
            {
                $"{groups.Count:N0} duplicate groups in {scanned:N0} files; "
                    + $"{ByteFormatter.FormatBytes(groups.Sum(g => g.WastedBytes))} could be freed by keeping one copy of each."
                    + (groups.Count > maxGroups ? $" Showing the top {maxGroups}." : ""),
            };
            foreach (var group in groups.Take(maxGroups))
            {
                lines.Add($"- {group.Files.Count} copies of {ByteFormatter.FormatBytes(group.Size)} "
                    + $"(wastes {ByteFormatter.FormatBytes(group.WastedBytes)}), oldest first:");
                lines.AddRange(group.Files.Take(10).Select(f => $"    {f.FullPath}  ({f.Modified:g})"));
                if (group.Files.Count > 10)
                    lines.Add($"    ...and {group.Files.Count - 10} more");
            }
            return string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException)
        {
            throw; // let the chat cancel the whole request
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Shows what takes up space inside a folder: its direct subfolders (with total size) and files, largest first. Read-only.")]
    public static string GetFolderSizes(
        [Description("Directory path (e.g. ~/Library) or a shortcut: Home, Documents, Desktop, Downloads, Applications.")] string directory = "Documents",
        [Description("Maximum items to return (1-50).")] int top = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found (network and automount paths are not allowed): {directory}";
            top = Math.Clamp(top, 1, 50);

            var items = FileScanner.GetFolderContents(resolved, cancellationToken);
            if (items.Count == 0)
                return "(empty)";
            var lines = new List<string>
            {
                $"{resolved}: {items.Count} items, total {ByteFormatter.FormatBytes(items.Sum(i => i.Size))}"
                    + (items.Count > top ? $". Showing the largest {top}." : "."),
            };
            lines.AddRange(items.Take(top).Select(i =>
                $"{ByteFormatter.FormatBytes(i.Size),-10} {(i.IsFolder ? "[folder]" : "[file]  ")} {i.Name}"));
            return string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists programs launchd starts at login or boot: LaunchAgents in ~/Library and /Library, and LaunchDaemons "
        + "in /Library (Apple's own jobs in /System are left out). Read-only.")]
    public static string GetStartupItems()
    {
        try
        {
            var items = StartupItems.GetItems();
            if (items.Count == 0)
                return "(no launch agents or daemons found)";
            return string.Join(Environment.NewLine, items.Select(e =>
            {
                var command = e.Command.Length <= 300 ? e.Command : e.Command[..300] + "...";
                var flags = string.Join(", ", new[] { e.RunAtLoad ? "runs at load" : null, e.KeepAlive ? "kept alive" : null, e.Disabled ? "disabled" : null }
                    .Where(f => f is not null));
                return $"{e.Label} [{e.Location}{(flags.Length > 0 ? "; " + flags : "")}]: {command}";
            }));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Says whether Homebrew is installed (/opt/homebrew on Apple silicon, /usr/local on Intel) and lists the installed "
        + "formulae and casks: version, whether the user asked for it or it came in as a dependency, pinned, install date and size "
        + "on disk, plus the size of the download cache. Reads Homebrew's folders; brew itself is not run (except brew --version). Read-only.")]
    public static string GetHomebrewPackages(int maxCount = 200, CancellationToken cancellationToken = default)
    {
        try
        {
            var installations = Homebrew.GetInstallations(cancellationToken);
            if (installations.Count == 0)
                return "Homebrew is not installed (no brew in /opt/homebrew/bin or /usr/local/bin). Install it from https://brew.sh.";

            var lines = installations.Select(i =>
                $"Homebrew {(i.Version.Length > 0 ? i.Version : "(version unknown)")} at {i.Prefix} ({i.Architecture}"
                + $"{(i.UnderRosetta ? ", runs under Rosetta 2" : "")}): {i.Formulae} formulae, {i.Casks} casks").ToList();
            lines.Add($"Download cache: {ByteFormatter.FormatBytes(installations[0].CacheBytes)} in {Homebrew.CacheFolder} (brew cleanup removes old downloads)");

            var packages = Homebrew.GetPackages(includeSizes: true, cancellationToken);
            maxCount = Math.Clamp(maxCount, 1, 1000);
            lines.Add("");
            lines.AddRange(packages.Take(maxCount).Select(p =>
            {
                var details = new List<string> { p.Kind == HomebrewKind.Cask ? "cask" : "formula" };
                if (p.InstalledOnRequest is { } onRequest)
                    details.Add(onRequest ? "installed on request" : "dependency");
                if (p.Pinned)
                    details.Add("pinned");
                if (p.InstalledAt is { } at)
                    details.Add($"installed {at:yyyy-MM-dd}");
                details.Add(ByteFormatter.FormatBytes(p.SizeBytes));
                if (installations.Count > 1)
                    details.Add(p.Prefix);
                var older = p.Versions.Count > 1 ? $" (also {string.Join(", ", p.Versions.SkipLast(1))})" : "";
                return $"{p.Name} {p.Version}{older} [{string.Join(", ", details)}]";
            }));
            if (packages.Count > maxCount)
                lines.Add($"... and {packages.Count - maxCount} more");
            return string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Gets the MacBook battery's health and state: charge, charging or not, Apple's condition (Normal / Service "
        + "Recommended), maximum capacity against the design capacity, cycle count and rated cycles, temperature, power draw, "
        + "time remaining and the connected power adapter. Says so when the Mac has no battery. Read-only.")]
    public static string GetBatteryHealth()
    {
        try
        {
            var b = Battery.Get();
            if (b is null)
                return OperatingSystem.IsMacOS()
                    ? "This Mac has no battery (a desktop Mac or a virtual machine)."
                    : "Battery information is only available on macOS.";
            return string.Join(Environment.NewLine, BatteryLines(b));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>The battery as label/value lines; shared with the app's Battery screen.</summary>
    public static IEnumerable<string> BatteryLines(BatteryStatus b) =>
        Battery.Describe(b).Select(p => $"{p.Label}: {p.Value}");

    [Description("Lists the local user accounts on this Mac: name, full name, UID, administrator or standard, home folder and shell. Read-only.")]
    public static string GetLocalUsers()
    {
        try
        {
            var accounts = LocalUsers.GetAccounts();
            if (accounts.Count == 0)
                return "(no local user accounts found)";
            return string.Join(Environment.NewLine, accounts.Take(100).Select(a =>
                $"{a.Name}{(a.FullName.Length > 0 ? $" ({a.FullName})" : "")}: {LocalUsers.FormatType(a)}, UID {a.Uid}, "
                + $"home {a.Home}, shell {a.Shell}{(a.CanLogIn ? "" : " (cannot log in)")}"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists network interfaces (en0 Wi-Fi/Ethernet, utun VPNs...) with type, status, IPv4/IPv6 addresses and MAC address.")]
    public static string GetNetworkAdapters()
    {
        try
        {
            var adapters = NetworkInfo.GetAdapters();
            if (adapters.Count == 0)
                return "(no network interfaces)";
            return string.Join(Environment.NewLine, adapters.Select(a =>
                $"{a.Name} ({a.Type}, {a.Status})"
                + (a.IPv4.Length > 0 ? $" IPv4 {a.IPv4}" : "")
                + (a.IPv6.Length > 0 ? $" IPv6 {a.IPv6}" : "")
                + (a.Mac.Length > 0 ? $" MAC {a.Mac}" : "")));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Pings a host name or IP address to check connectivity and latency (ICMP echo, 3 s timeout each).")]
    public static async Task<string> PingHost(
        [Description("Host name or IP address, e.g. 8.8.8.8 or example.com.")] string host,
        [Description("Number of pings (1-4).")] int count = 4,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host) || host.Trim().Length > 253)
                return "Enter a host name or IP address.";
            var results = await NetworkInfo.PingAsync(host, Math.Clamp(count, 1, 4), cancellationToken);
            var ok = results.Where(r => r.Success).ToList();
            var summary = $"{ok.Count}/{results.Count} replies"
                + (ok.Count > 0 ? $", avg {ok.Average(r => r.RoundtripMs):0} ms" : "");
            return summary + Environment.NewLine + string.Join(Environment.NewLine, results.Select(r =>
                r.Success ? $"Reply from {r.Address}: time={r.RoundtripMs}ms" : $"Request failed: {r.Status}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Ping failed: {ex.GetBaseException().Message}";
        }
    }

    [Description("Shows the macOS Application Firewall: on/off, stealth mode, block-all, and the apps with their own "
        + "allow/block rule. The macOS firewall works per app, not per port; see GetListeningPorts for ports. Read-only.")]
    public static string GetFirewallStatus()
    {
        if (!OperatingSystem.IsMacOS())
            return "Error: the Application Firewall is only available on macOS.";
        try
        {
            var status = FirewallInfo.GetStatus();
            static string OnOff(bool? value) => value switch { true => "on", false => "off", _ => "unknown" };
            var lines = new List<string>
            {
                $"Firewall: {OnOff(status.Enabled)}",
                $"Stealth mode: {OnOff(status.StealthMode)}",
                $"Block all incoming connections: {OnOff(status.BlockAll)}",
                $"Automatically allow signed software: {OnOff(status.AllowSigned)}",
            };
            var rules = FirewallInfo.GetAppRules();
            lines.Add(rules.Count == 0 ? "App rules: none" : $"App rules ({rules.Count}):");
            lines.AddRange(rules.Take(40).Select(r => $"  {(r.AllowsIncoming ? "Allow" : "Block")}  {r.Application}"));
            if (rules.Count > 40)
                lines.Add($"  ... {rules.Count - 40} more");
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.GetBaseException().Message}";
        }
    }

    [Description("Lists TCP ports in LISTEN state and bound UDP ports with the process that owns them, and whether "
        + "they are reachable from other machines or loopback only. Without root only this user's processes are shown. Read-only.")]
    public static string GetListeningPorts()
    {
        if (!OperatingSystem.IsMacOS())
            return "Error: listing ports with lsof is only available on macOS.";
        try
        {
            var ports = FirewallInfo.GetListeningPorts();
            if (ports.Count == 0)
                return "(no listening ports found for this user's processes)";
            var lines = ports.Take(60).Select(p =>
                $"{p.Protocol,-4} {p.Port,-6} {p.Address,-16} {(p.IsExposed ? "exposed " : "loopback")} {p.Process} (PID {p.Pid})");
            var more = ports.Count > 60 ? $"{Environment.NewLine}... {ports.Count - 60} more" : "";
            return string.Join(Environment.NewLine, lines) + more;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.GetBaseException().Message}";
        }
    }

    [Description("Traces the network path to a host over TCP, hop by hop with latency (TCP SYN probes with rising TTL).")]
    public static async Task<string> TraceTcpRoute(
        [Description("Host name or IP address, e.g. example.com.")] string host,
        [Description("TCP port to probe (1-65535).")] int port = 80,
        [Description("Maximum hops to trace (1-64).")] int maxHops = 10,
        [Description("Resolve each hop's IP address to a host name (slower).")] bool resolveHostNames = true,
        [Description("Per-probe timeout in milliseconds (500-10000).")] int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host) || host.Trim().Length > 253)
                return "Enter a host name or IP address.";
            var hops = await TcpTraceroute.TraceAsync(host.Trim(), Math.Clamp(port, 1, 65535),
                Math.Clamp(maxHops, 1, 64), Math.Clamp(timeoutMs, 500, 10000), resolveHostNames, progress: null,
                cancellationToken: cancellationToken);
            if (hops.Count == 0)
                return "(no hops)";
            return string.Join(Environment.NewLine, hops.Select(h =>
                $"{h.Ttl,2}  {(h.Address ?? "*"),-15} {(h.HostName ?? ""),-30} {FormatRtt(h.Rtt1Ms),-8} {FormatRtt(h.Rtt2Ms),-8} {FormatRtt(h.Rtt3Ms),-8} {h.Note ?? ""}".TrimEnd()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Traceroute failed: {ex.GetBaseException().Message}";
        }
    }

    private static string FormatRtt(long? ms) => ms.HasValue ? $"{ms.Value} ms" : "*";

    internal static string? ResolveDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        var key = directory.Trim().Trim('"');
        var home = AppPaths.Home;
        var candidate = key.ToLowerInvariant() switch
        {
            "documents" or "mydocuments" or "my documents" => Path.Combine(home, "Documents"),
            "desktop" => Path.Combine(home, "Desktop"),
            "downloads" => Path.Combine(home, "Downloads"),
            "userprofile" or "home" or "~" => home,
            "applications" => "/Applications",
            _ => ToLocalPath(key),
        };
        return candidate is not null && Directory.Exists(candidate) ? candidate : null;
    }

    // The model chooses tool paths, so they are untrusted. On a Mac the danger is reaching another
    // machine: /net/<host> and /Network are autofs mount points (looking inside makes macOS mount
    // an NFS export from that host, so a host name the model invents becomes a DNS lookup and a
    // connection), and SMB/AFP/NFS/WebDAV shares can be mounted anywhere. Those are refused; the
    // lexical checks run before any file-system call. "~" is expanded to the home folder.
    internal static string? ToLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string full;
        try
        {
            var trimmed = path.Trim().Trim('"');
            if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal))
                trimmed = AppPaths.Home + trimmed[1..];
            full = Path.GetFullPath(trimmed);
        }
        catch (Exception)
        {
            return null;
        }
        if (OperatingSystem.IsWindows())
            return full.StartsWith(@"\\", StringComparison.Ordinal) ? null : full; // developer builds only
        if (full.Length > 1)
            full = full.TrimEnd('/');
        return Volumes.IsNetworkPath(full) ? null : full;
    }

    private static string LocalOnlyMessage(string path) =>
        $"Only paths on this Mac's own disks (e.g. ~/Documents/file.txt) are allowed; network and automount paths are blocked: {path}";

    [Description("Reads a text file. Read-only. The user is asked to allow each call.")]
    public static string ReadTextFile(
        [Description("Full file path, e.g. ~/Documents/notes.txt.")] string path,
        [Description("Maximum characters to return (max 20000).")] int maxChars = 8000)
    {
        try
        {
            var local = ToLocalPath(path);
            if (local is null)
                return LocalOnlyMessage(path);
            if (!File.Exists(local))
                return $"File not found: {path}";
            maxChars = Math.Clamp(maxChars, 1, 20000);
            // Read only what will be returned: an approved multi-GB log must not be pulled into
            // memory in full just to show its first 20,000 characters.
            using var reader = new StreamReader(local);
            var buffer = new char[maxChars];
            var read = reader.Read(buffer, 0, maxChars);
            var text = new string(buffer, 0, read);
            return reader.Peek() >= 0 ? text + "\n...(truncated)" : text;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Gets the value of an environment variable of this app. The user is asked to allow each call.")]
    public static string GetEnvironmentVariable(
        [Description("Variable name, e.g. PATH.")] string name)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null)
                return "(not set)";
            return value.Length <= 2000 ? value : value[..2000] + "\n...(truncated)";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists environment variables (NAME=value). Target Process = this app's own variables; LoginShell = what a "
        + "new Terminal window sees (runs the login shell once). Read-only. "
        + "The user is asked to allow this call because the values can contain secrets.")]
    public static string ListEnvironmentVariables(
        [Description("Target: Process or LoginShell (default Process).")] string target = "Process",
        [Description("Maximum variables to return (1-500).")] int maxResults = 200)
    {
        try
        {
            var source = EnvironmentInfo.Parse(target);
            maxResults = Math.Clamp(maxResults, 1, 500);

            var entries = EnvironmentInfo.Get(source).Select(e => $"{e.Key}={e.Value}").ToList();
            if (entries.Count == 0)
                return $"(no {source} environment variables)";

            var lines = entries.Take(maxResults)
                .Select(s => s.Length <= 2000 ? s : s[..2000] + "...(truncated)")
                .ToList();
            var more = entries.Count > maxResults ? $"{Environment.NewLine}... {entries.Count - maxResults} more" : "";
            return string.Join(Environment.NewLine, lines) + more;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Computes the hash of a file. Read-only.")]
    public static string ComputeFileHash(
        [Description("Full file path.")] string path,
        [Description("Algorithm: MD5, SHA-1, SHA-256, or SHA-512.")] string algorithm = "SHA-256")
    {
        try
        {
            var local = ToLocalPath(path);
            if (local is null)
                return LocalOnlyMessage(path);
            if (!File.Exists(local))
                return $"File not found: {path}";
            var parsed = ParseAlgorithm(algorithm);
            if (parsed is null)
                return $"Unknown algorithm '{algorithm}'. Use MD5, SHA-1, SHA-256, or SHA-512.";
            var hashes = FileHasher.ComputeHashes(local, [parsed.Value]);
            return $"{DisplayName(parsed.Value)}: {hashes[parsed.Value]}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Checks whether a file matches an expected hash. Picks the algorithm from the hash length.")]
    public static string VerifyFileHash(
        [Description("Full file path.")] string path,
        [Description("Expected hash in hex (MD5=32 chars, SHA-1=40, SHA-256=64, SHA-512=128).")] string expectedHash)
    {
        try
        {
            var local = ToLocalPath(path);
            if (local is null)
                return LocalOnlyMessage(path);
            if (!File.Exists(local))
                return $"File not found: {path}";
            var expected = expectedHash.Trim().ToLowerInvariant();
            HashAlgorithmName? parsed = null;
            if (expected.Length == 32)
                parsed = HashAlgorithmName.MD5;
            else if (expected.Length == 40)
                parsed = HashAlgorithmName.SHA1;
            else if (expected.Length == 64)
                parsed = HashAlgorithmName.SHA256;
            else if (expected.Length == 128)
                parsed = HashAlgorithmName.SHA512;
            if (parsed is null)
                return $"Cannot tell the algorithm from a {expected.Length}-character hash.";
            var hashes = FileHasher.ComputeHashes(local, [parsed.Value]);
            var name = DisplayName(parsed.Value);
            return FileHasher.FindMatch(hashes, expected) is not null
                ? $"MATCH ({name})"
                : $"NO MATCH ({name}). Expected {expected}, got {hashes[parsed.Value]}.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // NOTE: plain if-chain (not a switch expression with a `_ => null` arm). Roslyn compiles
    // the null arm of a nullable-struct switch through the struct's implicit string operator
    // with a null string, which throws ArgumentNullException.
    private static HashAlgorithmName? ParseAlgorithm(string algorithm)
    {
        var key = algorithm.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal);
        if (key == "MD5")
            return HashAlgorithmName.MD5;
        if (key == "SHA1")
            return HashAlgorithmName.SHA1;
        if (key == "SHA256")
            return HashAlgorithmName.SHA256;
        if (key == "SHA512")
            return HashAlgorithmName.SHA512;
        return null;
    }

    private static string DisplayName(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.MD5)
            return "MD5";
        if (algorithm == HashAlgorithmName.SHA1)
            return "SHA-1";
        if (algorithm == HashAlgorithmName.SHA256)
            return "SHA-256";
        if (algorithm == HashAlgorithmName.SHA384)
            return "SHA-384";
        if (algorithm == HashAlgorithmName.SHA512)
            return "SHA-512";
        return algorithm.Name ?? "Unknown";
    }
}
