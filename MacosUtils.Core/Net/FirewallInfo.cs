namespace MacosUtils.Core.Net;

/// <summary>The Application Firewall's switches; null when a value could not be read.</summary>
public sealed record FirewallStatus(bool? Enabled, bool? StealthMode, bool? BlockAll, bool? AllowSigned);

/// <summary>An app with its own rule in the Application Firewall.</summary>
public sealed record FirewallAppRule(string Application, bool AllowsIncoming);

/// <summary>A socket a process listens on: a TCP port in LISTEN state, or a bound UDP port.</summary>
public sealed record ListeningPort(string Protocol, string Address, int Port, string Process, int Pid)
{
    /// <summary>True when the port is reachable from other machines (not loopback only).</summary>
    public bool IsExposed => !(Address.StartsWith("127.", StringComparison.Ordinal) || Address is "[::1]" or "::1" or "localhost");
}

/// <summary>
/// macOS's Application Firewall (socketfilterfw) works per app, not per port, so the Mac version of
/// WindowsUtils' Firewall Ports screen shows its state and app rules next to the ports processes
/// actually listen on (lsof). Without root lsof sees only the current user's processes. Read-only.
/// </summary>
public static class FirewallInfo
{
    private const string SocketFilter = "/usr/libexec/ApplicationFirewall/socketfilterfw";
    private const string Lsof = "/usr/sbin/lsof";

    public static FirewallStatus GetStatus()
    {
        if (!OperatingSystem.IsMacOS())
            return new FirewallStatus(null, null, null, null);
        return new FirewallStatus(
            ParseSwitch(Processes.TryRun(SocketFilter, ["--getglobalstate"])),
            ParseSwitch(Processes.TryRun(SocketFilter, ["--getstealthmode"])),
            ParseSwitch(Processes.TryRun(SocketFilter, ["--getblockall"])),
            ParseSwitch(Processes.TryRun(SocketFilter, ["--getallowsigned"])));
    }

    /// <summary>"Firewall is enabled. (State = 1)", "Stealth mode enabled", "Block all DISABLED!"...; public for tests.</summary>
    public static bool? ParseSwitch(string? output)
    {
        if (output is null)
            return null;
        var text = output.ToLowerInvariant();
        // "disabled" contains "enabled": check it first. "Firewall is on/off" in some versions.
        if (text.Contains("disabled") || text.Contains(" is off") || text.Contains("state = 0"))
            return false;
        if (text.Contains("enabled") || text.Contains(" is on") || text.Contains("state = 1") || text.Contains("state = 2"))
            return true;
        return null;
    }

    public static IReadOnlyList<FirewallAppRule> GetAppRules()
    {
        if (!OperatingSystem.IsMacOS())
            return [];
        return ParseAppRules(Processes.TryRun(SocketFilter, ["--listapps"]) ?? "");
    }

    /// <summary>
    /// Parses "socketfilterfw --listapps": numbered app paths, each followed by a line
    /// "( Allow incoming connections )" or "( Block incoming connections )". Public for tests.
    /// </summary>
    public static IReadOnlyList<FirewallAppRule> ParseAppRules(string output)
    {
        var rules = new List<FirewallAppRule>();
        string? app = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim() is { Length: > 0 } number && number.All(char.IsAsciiDigit))
            {
                app = line[(colon + 1)..].Trim();
                continue;
            }
            if (app is not null && line.StartsWith('('))
            {
                rules.Add(new FirewallAppRule(app, line.Contains("allow", StringComparison.OrdinalIgnoreCase)));
                app = null;
            }
        }
        return rules;
    }

    /// <summary>TCP ports in LISTEN state and bound UDP ports, sorted by protocol then port.</summary>
    public static IReadOnlyList<ListeningPort> GetListeningPorts()
    {
        if (!OperatingSystem.IsMacOS())
            return [];
        // -F: one field per line (p=pid, c=command, P=protocol, n=address); -n/-P: no DNS or port names.
        var output = Processes.TryRun(Lsof, ["-nP", "-iTCP", "-sTCP:LISTEN", "-iUDP", "-F", "pcPn"], TimeSpan.FromSeconds(20));
        return output is null ? [] : ParseLsof(output);
    }

    /// <summary>Parses "lsof -F pcPn" output; public for tests.</summary>
    public static IReadOnlyList<ListeningPort> ParseLsof(string output)
    {
        var ports = new HashSet<ListeningPort>();
        var pid = 0;
        var command = "";
        var protocol = "";
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 2)
                continue;
            var value = line[1..];
            switch (line[0])
            {
                case 'p':
                    pid = int.TryParse(value, out var parsed) ? parsed : 0;
                    break;
                case 'c':
                    command = value;
                    break;
                case 'P':
                    protocol = value;
                    break;
                case 'n':
                    // UDP sockets with a peer ("1.2.3.4:5->6.7.8.9:10") are connections, not listeners.
                    if (value.Contains("->", StringComparison.Ordinal))
                        break;
                    var colon = value.LastIndexOf(':');
                    if (colon <= 0 || !int.TryParse(value[(colon + 1)..], out var port))
                        break;
                    var address = value[..colon];
                    ports.Add(new ListeningPort(protocol, address == "*" ? "All interfaces" : address, port, command, pid));
                    break;
            }
        }
        return ports
            .OrderBy(p => p.Protocol, StringComparer.Ordinal)
            .ThenBy(p => p.Port)
            .ThenBy(p => p.Process, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
