using System.Text;
using MacosUtils.AI;

namespace MacosUtils.Cli;

// A command-line interface to the same read-only Mac tools the MacosUtils app and its AI
// assistant expose (PcTools). Every command inspects this Mac and prints the result to stdout;
// nothing is changed — destructive actions stay in the GUI only. Long scans stop with Control-C.
static class Program
{
    private sealed record Command(
        string Name,
        string[] Aliases,
        string Usage,
        string Summary,
        Func<string[], CancellationToken, Task<int>> Run);

    private static readonly Command[] Commands =
    [
        new("time", [], "time", "Current date and time.",
            (_, _) => Ok(PcTools.GetCurrentTime())),

        new("system", ["sysinfo"], "system", "OS, CPU, memory and .NET runtime.",
            (_, _) => Ok(PcTools.GetSystemInfo())),

        new("dotnet", [], "dotnet", "Installed .NET SDKs and runtimes.",
            (_, _) => Ok(PcTools.GetDotNetVersions())),

        new("java", [], "java", "Installed Java (JDK/JRE).",
            (_, _) => Ok(PcTools.GetJavaVersion())),

        new("python", [], "python", "Installed Python.",
            (_, _) => Ok(PcTools.GetPythonVersion())),

        new("node", [], "node", "Installed Node.js.",
            (_, _) => Ok(PcTools.GetNodeVersion())),

        new("features", ["security"], "features", "Security and sharing settings (SIP, FileVault, SSH...).",
            (_, _) => Ok(PcTools.GetMacFeatures())),

        new("processes", [], "processes [n]", "Top processes by memory usage.",
            (a, _) =>
            {
                var n = OptionalInt(a, 0, "process count", 10, out var error);
                return error is not null ? Fail(error) : Ok(PcTools.GetProcesses(n));
            }),

        new("disks", ["volumes"], "disks", "Volumes with free/total space.",
            (_, _) => Ok(PcTools.GetVolumes())),

        new("network", [], "network", "Network adapters, addresses and MAC.",
            (_, _) => Ok(PcTools.GetNetworkAdapters())),

        new("firewall", [], "firewall", "Application Firewall state and app rules.",
            (_, _) => Ok(PcTools.GetFirewallStatus())),

        new("ports", ["listening"], "ports", "Listening TCP/UDP ports and their processes.",
            (_, _) => Ok(PcTools.GetListeningPorts())),

        new("ping", [], "ping <host> [n]", "Ping a host (ICMP echo).",
            async (a, ct) =>
            {
                if (a.Length == 0)
                    return Error("Missing host.", "ping <host> [n]");
                var n = OptionalInt(a, 1, "count", 4, out var error);
                if (error is not null)
                    return Error(error);
                return Print(await PcTools.PingHost(a[0], n, ct));
            }),

        new("traceroute", ["trace"], "traceroute <host> [port] [hops] [--no-names] [--timeout ms]", "Trace the network path over TCP.",
            async (a, ct) =>
            {
                var positional = new List<string>();
                var resolveNames = true;
                var timeoutMs = 2000;
                for (var i = 0; i < a.Length; i++)
                {
                    if (a[i].Equals("--no-names", StringComparison.OrdinalIgnoreCase))
                    {
                        resolveNames = false;
                    }
                    else if (a[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase)
                        && i + 1 < a.Length && int.TryParse(a[i + 1], out var parsedTimeout))
                    {
                        timeoutMs = parsedTimeout;
                        i++;
                    }
                    else
                    {
                        positional.Add(a[i]);
                    }
                }

                var args = positional.ToArray();
                if (args.Length == 0)
                    return Error("Missing host.", "traceroute <host> [port] [hops] [--no-names] [--timeout ms]");
                var port = OptionalInt(args, 1, "port", 80, out var error);
                if (error is not null)
                    return Error(error);
                var hops = OptionalInt(args, 2, "hops", 10, out error);
                if (error is not null)
                    return Error(error);
                return Print(await PcTools.TraceTcpRoute(args[0], port, hops, resolveNames, timeoutMs, ct));
            }),

        new("startup", ["launchagents"], "startup", "Launch agents and daemons that start with the Mac.",
            (_, _) => Ok(PcTools.GetStartupItems())),

        new("brew", ["homebrew"], "brew [n]", "Is Homebrew installed, and its formulae and casks.",
            (a, ct) =>
            {
                var n = OptionalInt(a, 0, "count", 200, out var error);
                return error is not null ? Fail(error) : Ok(PcTools.GetHomebrewPackages(n, ct));
            }),

        new("battery", [], "battery", "Battery health, charge and cycle count.",
            (_, _) => Ok(PcTools.GetBatteryHealth())),

        new("users", [], "users", "Local user accounts and their status.",
            (_, _) => Ok(PcTools.GetLocalUsers())),

        new("env", ["getenv"], "env <name>", "Value of an environment variable.",
            (a, _) => a.Length == 0
                ? Fail("Missing variable name.", "env <name>")
                : Ok(PcTools.GetEnvironmentVariable(a[0]))),

        new("envlist", ["envall"], "envlist [Process|LoginShell] [n]", "List environment variables (values may be sensitive).",
            (a, _) =>
            {
                var target = a.Length > 0 ? a[0] : "Process";
                var n = OptionalInt(a, 1, "count", 200, out var error);
                return error is not null ? Fail(error) : Ok(PcTools.ListEnvironmentVariables(target, n));
            }),

        new("files", [], "files <dir> [pattern] [n]", "List files and folders in a directory.",
            (a, _) =>
            {
                if (a.Length == 0)
                    return Fail("Missing directory.", "files <dir> [pattern] [n]");
                var n = OptionalInt(a, 2, "count", 50, out var error);
                return error is not null
                    ? Fail(error)
                    : Ok(PcTools.ListFiles(a[0], a.Length > 1 ? a[1] : "*", n));
            }),

        new("folders", [], "folders <dir> [pattern] [n]", "List subfolders in a directory.",
            (a, _) =>
            {
                if (a.Length == 0)
                    return Fail("Missing directory.", "folders <dir> [pattern] [n]");
                var n = OptionalInt(a, 2, "count", 50, out var error);
                return error is not null
                    ? Fail(error)
                    : Ok(PcTools.ListFolders(a[0], a.Length > 1 ? a[1] : "*", n));
            }),

        new("largest", [], "largest <dir> [n]", "Largest files in a folder, recursive.",
            (a, _) =>
            {
                var dir = a.Length > 0 ? a[0] : ".";
                var n = OptionalInt(a, 1, "count", 10, out var error);
                return error is not null ? Fail(error) : Ok(PcTools.GetLargestFiles(dir, n, true));
            }),

        new("foldersize", ["dirsize"], "foldersize <dir> [n]", "What takes space inside a folder.",
            (a, ct) =>
            {
                var dir = a.Length > 0 ? a[0] : ".";
                var n = OptionalInt(a, 1, "count", 20, out var error);
                return error is not null ? Fail(error) : Ok(PcTools.GetFolderSizes(dir, n, ct));
            }),

        new("duplicates", ["dup"], "duplicates <dir> [minMb] [n]", "Duplicate files by content (read-only).",
            (a, ct) =>
            {
                var dir = a.Length > 0 ? a[0] : ".";
                var minMb = OptionalInt(a, 1, "minimum size", 1, out var error);
                if (error is not null)
                    return Fail(error);
                var n = OptionalInt(a, 2, "groups", 20, out error);
                return error is not null ? Fail(error) : Ok(PcTools.FindDuplicateFiles(dir, minMb, n, ct));
            }),

        new("read", [], "read <file> [n]", "Read a text file (first n characters).",
            (a, _) =>
            {
                if (a.Length == 0)
                    return Fail("Missing file.", "read <file> [n]");
                var n = OptionalInt(a, 1, "characters", 8000, out var error);
                return error is not null ? Fail(error) : Ok(PcTools.ReadTextFile(a[0], n));
            }),

        new("hash", [], "hash <file> [algorithm]", "Compute a file hash (MD5, SHA-1, SHA-256, SHA-512).",
            (a, _) => a.Length == 0
                ? Fail("Missing file.", "hash <file> [algorithm]")
                : Ok(PcTools.ComputeFileHash(a[0], a.Length > 1 ? a[1] : "SHA-256"))),

        new("verify", [], "verify <file> <hash>", "Check a file against an expected hash.",
            (a, _) => a.Length < 2
                ? Fail("Missing file or expected hash.", "verify <file> <hash>")
                : Ok(PcTools.VerifyFileHash(a[0], a[1]))),
    ];

    static async Task<int> Main(string[] args)
    {
        // Best effort: some terminals use a non-Unicode code page; UTF-8 prints names cleanly.
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        var name = args[0].ToLowerInvariant();
        if (name is "help" or "-h" or "--help" or "/?")
        {
            if (args.Length > 1)
                PrintCommandHelp(args[1]);
            else
                PrintHelp();
            return 0;
        }

        var command = FindCommand(name);
        if (command is null)
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'. Run 'mutils help' to list commands.");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Stay alive long enough for the tool to observe the cancellation and stop cleanly.
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return await command.Run(args[1..], cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
    }

    private static Command? FindCommand(string name) =>
        Commands.FirstOrDefault(c => c.Name == name || c.Aliases.Contains(name));

    private static void PrintHelp()
    {
        Console.WriteLine("MacosUtils CLI — read-only Mac inspection and file tools.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  mutils <command> [arguments]");
        Console.WriteLine("  mutils help [command]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        foreach (var command in Commands)
            Console.WriteLine($"  {command.Usage,-36} {command.Summary}");
        Console.WriteLine();
        Console.WriteLine("Every command is read-only; nothing is changed. Long scans stop with Control-C.");
    }

    private static void PrintCommandHelp(string name)
    {
        var command = FindCommand(name.ToLowerInvariant());
        if (command is null)
        {
            Console.Error.WriteLine($"Unknown command '{name}'. Run 'mutils help' to list commands.");
            return;
        }
        Console.WriteLine($"Usage: mutils {command.Usage}");
        Console.WriteLine();
        Console.WriteLine(command.Summary);
        if (command.Aliases.Length > 0)
            Console.WriteLine($"Aliases: {string.Join(", ", command.Aliases)}");
    }

    private static int OptionalInt(string[] args, int index, string name, int fallback, out string? error)
    {
        error = null;
        if (index >= args.Length)
            return fallback;
        if (int.TryParse(args[index], out var value))
            return value;
        error = $"'{args[index]}' is not a valid number for {name}.";
        return 0;
    }

    private static int Print(string text)
    {
        Console.WriteLine(text);
        return 0;
    }

    private static int Error(string message, string? usage = null)
    {
        Console.Error.WriteLine(message);
        if (usage is not null)
            Console.Error.WriteLine($"Usage: mutils {usage}");
        return 2;
    }

    private static Task<int> Ok(string text) => Task.FromResult(Print(text));

    private static Task<int> Fail(string message, string? usage = null) => Task.FromResult(Error(message, usage));
}
