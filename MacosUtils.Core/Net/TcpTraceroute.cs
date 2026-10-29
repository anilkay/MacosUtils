using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MacosUtils.Core.Net;

/// <summary>One completed hop of a TCP traceroute. <see cref="Address"/> is null when the hop did not answer ("*").</summary>
public sealed record TracerouteHop(
    int Ttl,
    string? Address,
    string? HostName,
    long? Rtt1Ms,
    long? Rtt2Ms,
    long? Rtt3Ms,
    string? Note);

/// <summary>
/// TCP traceroute on macOS: runs the system's /usr/sbin/traceroute in TCP mode
/// (<c>-P tcp -p port</c>), which sends SYN probes with increasing TTL and reads the ICMP Time
/// Exceeded replies. macOS installs traceroute setuid root, so no administrator password is needed
/// (WindowsUtils needs Administrator for its raw socket). Hops are parsed as traceroute prints them.
/// </summary>
public static class TcpTraceroute
{
    private const string Traceroute = "/usr/sbin/traceroute";
    private const int ProbesPerHop = 3;

    /// <summary>Traces the route to <paramref name="host"/>:<paramref name="port"/>, reporting each finished hop via <paramref name="progress"/>.</summary>
    public static async Task<IReadOnlyList<TracerouteHop>> TraceAsync(
        string host,
        int port,
        int maxHops = 30,
        int timeoutMs = 3000,
        bool resolveHostNames = true,
        IProgress<TracerouteHop>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be 1-65535.");
        if (!OperatingSystem.IsMacOS() || !File.Exists(Traceroute))
            throw new PlatformNotSupportedException("TCP traceroute uses macOS's /usr/sbin/traceroute.");
        maxHops = Math.Clamp(maxHops, 1, 255);
        timeoutMs = Math.Clamp(timeoutMs, 100, 60000);

        // Resolved here, so traceroute gets an address and no option-looking host name ever reaches it.
        var target = await ResolveIPv4Async(host, cancellationToken);
        var waitSeconds = Math.Max(1, (int)Math.Ceiling(timeoutMs / 1000.0));

        var info = new ProcessStartInfo(Traceroute)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Environment = { ["LC_ALL"] = "en_US.UTF-8" },
        };
        foreach (var argument in (string[])["-n", "-P", "tcp", "-p", port.ToString(CultureInfo.InvariantCulture),
                     "-m", maxHops.ToString(CultureInfo.InvariantCulture), "-q", ProbesPerHop.ToString(CultureInfo.InvariantCulture),
                     "-w", waitSeconds.ToString(CultureInfo.InvariantCulture), target.ToString()])
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start traceroute.");
        using var _ = cancellationToken.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var hops = new List<TracerouteHop>();
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hop = ParseHopLine(line, target.ToString());
            if (hop is null)
                continue;
            if (resolveHostNames && hop.Address is not null)
                hop = hop with { HostName = await TryResolveHostNameAsync(hop.Address, cancellationToken) };
            hops.Add(hop);
            progress?.Report(hop);
            if (hop.Note == "Reached")
                break;
        }

        if (!process.HasExited)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        var errorText = (await errors).Trim();
        if (hops.Count == 0 && process.ExitCode != 0 && errorText.Length > 0)
            throw new InvalidOperationException(LastLine(errorText));
        return hops;
    }

    /// <summary>
    /// Parses one hop line of "traceroute -n", e.g. <c> 3  10.0.0.1  5.1 ms  10.0.0.2  6.2 ms *</c>
    /// or <c> 9  93.184.216.34  20.1 ms !Z  19.8 ms  20.0 ms</c>. Returns null for other lines.
    /// Public for tests.
    /// </summary>
    public static TracerouteHop? ParseHopLine(string line, string targetAddress)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !int.TryParse(tokens[0], out var ttl))
            return null;

        var rtts = new long?[ProbesPerHop];
        var probe = 0;
        string? address = null;
        string? note = null;
        for (var i = 1; i < tokens.Length && probe < ProbesPerHop; i++)
        {
            var token = tokens[i];
            if (token == "*")
            {
                rtts[probe++] = null;
            }
            else if (token.StartsWith('!'))
            {
                note ??= UnreachableNote(token);
            }
            else if (i + 1 < tokens.Length && tokens[i + 1] == "ms"
                && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
            {
                rtts[probe++] = (long)Math.Round(ms);
                i++; // skip "ms"
            }
            else if (IPAddress.TryParse(token, out _))
            {
                address ??= token; // later probes may answer from another router; the first one names the hop
            }
        }

        var reached = address == targetAddress;
        return new TracerouteHop(ttl, address, null, rtts[0], rtts[1], rtts[2], reached ? "Reached" : note);
    }

    private static string UnreachableNote(string token) => token switch
    {
        "!N" => "!N (net unreachable)",
        "!H" => "!H (host unreachable)",
        "!P" => "!P (protocol unreachable)",
        "!X" => "!X (admin prohibited)",
        "!S" => "!S (source route failed)",
        "!F" => "!F (fragmentation needed)",
        _ => token.StartsWith("!<", StringComparison.Ordinal) ? $"ICMP unreachable {token[1..]}" : token,
    };

    private static async Task<IPAddress> ResolveIPv4Async(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Enter a host name or IP address.", nameof(host));
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host.Trim(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not resolve '{host}'.", ex);
        }
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"Could not resolve an IPv4 address for '{host}'.");
    }

    private static async Task<string?> TryResolveHostNameAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsCts.CancelAfter(1500);
            var entry = await Dns.GetHostEntryAsync(address, dnsCts.Token);
            var name = entry.HostName.TrimEnd('.');
            return string.IsNullOrEmpty(name) || name == address ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? text;
}
