using System.Collections.Concurrent;
using System.Diagnostics;
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

/// <summary>An ICMP Time Exceeded / Destination Unreachable reply, with the TCP probe it is about.</summary>
public sealed record IcmpProbeReply(int Type, int Code, int SourcePort, int DestPort, IPAddress DestAddress);

/// <summary>
/// TCP traceroute: ordinary TCP connects (SYN probes) with increasing TTL (1, 2, ...); each hop is
/// named by the ICMP Time Exceeded reply its router sends back, and the target by its SYN-ACK or RST.
/// No administrator password: macOS lets any user open an ICMP datagram socket (the one ping uses),
/// which also receives those replies. Root gets a raw ICMP socket instead.
/// </summary>
/// <remarks>
/// The system's <c>traceroute -P tcp</c> is not used: it reads the target's answer with pcap,
/// and opening /dev/bpf fails without root ("pcap_activate() failed").
/// </remarks>
public static class TcpTraceroute
{
    private const int ProbesPerHop = 3;

    private sealed record IcmpReply(string HopAddress, IcmpProbeReply Probe, long ReceivedTimestamp);

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
        maxHops = Math.Clamp(maxHops, 1, 255);
        timeoutMs = Math.Clamp(timeoutMs, 100, 60000);

        var target = await ResolveIPv4Async(host, cancellationToken);
        var localAddress = GetLocalAddress(target, port);

        // Without a listener the trace still runs: routers show as "*", the target is still found.
        using var listener = CreateIcmpListener(localAddress);
        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var replies = new ConcurrentQueue<IcmpReply>();
        var listenTask = listener is null
            ? Task.CompletedTask
            : Task.Run(() => ListenLoopAsync(listener, replies, listenCts.Token), CancellationToken.None);

        try
        {
            var hops = new List<TracerouteHop>();
            for (var ttl = 1; ttl <= maxHops; ttl++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hop = await TraceHopAsync(target, port, localAddress, ttl, timeoutMs,
                    resolveHostNames, replies, cancellationToken);
                hops.Add(hop);
                progress?.Report(hop);
                if (hop.Note == "Reached")
                    break;
            }
            return hops;
        }
        finally
        {
            listenCts.Cancel();
            try { await listenTask; } catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

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

    private static IPAddress GetLocalAddress(IPAddress target, int port)
    {
        // UDP "connect" sends nothing; it just selects the outbound interface.
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Connect(target, port);
        return ((IPEndPoint)udp.LocalEndPoint!).Address;
    }

    /// <summary>A raw ICMP socket when running as root, else the unprivileged ICMP datagram socket; null if neither opens.</summary>
    private static Socket? CreateIcmpListener(IPAddress localAddress)
    {
        foreach (var type in (SocketType[])[SocketType.Raw, SocketType.Dgram])
        {
            try
            {
                var listener = new Socket(AddressFamily.InterNetwork, type, ProtocolType.Icmp);
                try
                {
                    listener.Bind(new IPEndPoint(localAddress, 0));
                    return listener;
                }
                catch (SocketException)
                {
                    listener.Dispose();
                }
            }
            catch (SocketException)
            {
                // Raw needs root; try the next kind.
            }
        }
        return null;
    }

    private static async Task ListenLoopAsync(Socket listener, ConcurrentQueue<IcmpReply> replies, CancellationToken cancellationToken)
    {
        var buffer = new byte[1500];
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await listener.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
            var received = Stopwatch.GetTimestamp();
            if (TryParseIcmpReply(buffer.AsSpan(0, result.ReceivedBytes), out var probe))
                replies.Enqueue(new IcmpReply(((IPEndPoint)result.RemoteEndPoint).Address.ToString(), probe, received));
        }
    }

    /// <summary>
    /// Reads an ICMP Time Exceeded (11) or Destination Unreachable (3) message and the TCP ports and
    /// destination of the probe it quotes. macOS hands ICMP sockets the packet with its IPv4 header in
    /// front; that header is skipped when present. Public for tests.
    /// </summary>
    public static bool TryParseIcmpReply(ReadOnlySpan<byte> packet, out IcmpProbeReply reply)
    {
        reply = null!;
        if (packet.Length >= 20 && packet[0] >> 4 == 4)
        {
            var outerHeader = (packet[0] & 0x0F) * 4;
            if (outerHeader < 20 || packet.Length < outerHeader)
                return false;
            packet = packet[outerHeader..];
        }
        if (packet.Length < 8 + 20)
            return false;
        var type = packet[0];
        if (type is not (11 or 3))
            return false;
        var code = packet[1];
        var inner = packet[8..];                   // the quoted IPv4 header of our probe
        if (inner[0] >> 4 != 4 || inner[9] != 6)   // IPv4 carrying TCP
            return false;
        var innerHeader = (inner[0] & 0x0F) * 4;
        if (innerHeader < 20 || inner.Length < innerHeader + 4)
            return false;
        var tcp = inner[innerHeader..];
        reply = new IcmpProbeReply(
            type,
            code,
            (tcp[0] << 8) | tcp[1],
            (tcp[2] << 8) | tcp[3],
            new IPAddress(inner.Slice(16, 4)));
        return true;
    }

    private static async Task<TracerouteHop> TraceHopAsync(
        IPAddress target, int port, IPAddress localAddress, int ttl, int timeoutMs,
        bool resolveHostNames, ConcurrentQueue<IcmpReply> replies, CancellationToken cancellationToken)
    {
        var rtts = new long?[ProbesPerHop];
        string? hopAddress = null;
        string? note = null;
        var reached = false;

        for (var probe = 0; probe < ProbesPerHop; probe++)
        {
            var (probeReached, rtt, icmp) = await ProbeOnceAsync(target, port, localAddress, ttl, timeoutMs, replies, cancellationToken);
            rtts[probe] = rtt;
            if (probeReached)
                reached = true;
            else if (icmp is not null)
            {
                hopAddress ??= icmp.HopAddress;
                note ??= icmp.Probe.Type == 3 ? UnreachableNote(icmp.Probe.Code) : null;
            }
        }

        if (reached)
            hopAddress ??= target.ToString(); // SYN-ACK/RST came from the target itself
        string? hostName = null;
        if (resolveHostNames && hopAddress is not null)
            hostName = await TryResolveHostNameAsync(hopAddress, cancellationToken);

        return new TracerouteHop(ttl, hopAddress, hostName, rtts[0], rtts[1], rtts[2], reached ? "Reached" : note);
    }

    private static async Task<(bool Reached, long? RttMs, IcmpReply? Icmp)> ProbeOnceAsync(
        IPAddress target, int port, IPAddress localAddress, int ttl, int timeoutMs,
        ConcurrentQueue<IcmpReply> replies, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(localAddress, 0));
        var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);

        var sent = Stopwatch.GetTimestamp();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectTask = socket.ConnectAsync(target, port, connectCts.Token).AsTask();
        var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs, cancellationToken));

        if (completed == connectTask)
        {
            try
            {
                await connectTask;
                return (true, ElapsedMs(sent), null); // port open: reached
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                return (true, ElapsedMs(sent), null); // RST from the target: reached
            }
            catch (SocketException)
            {
                // e.g. host/net unreachable reported by the stack: look at the ICMP queue below.
            }
        }
        else
        {
            // Timeout (or cancelled): stop the pending connect.
            connectCts.Cancel();
            try { await connectTask; } catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Replies for this probe: its source port, the target port and the target address must match.
        IcmpReply? match = null;
        while (replies.TryDequeue(out var reply))
        {
            if (match is null
                && reply.ReceivedTimestamp >= sent
                && reply.Probe.SourcePort == localPort
                && reply.Probe.DestPort == port
                && reply.Probe.DestAddress.Equals(target))
                match = reply;
            // else: stale or stray reply — dropped.
        }

        if (match is null)
            return (false, null, null); // "*": no answer
        var rtt = (long)((match.ReceivedTimestamp - sent) * 1000.0 / Stopwatch.Frequency);
        return (false, rtt, match);
    }

    private static long ElapsedMs(long sentTimestamp) =>
        (long)((Stopwatch.GetTimestamp() - sentTimestamp) * 1000.0 / Stopwatch.Frequency);

    private static string UnreachableNote(int code) => code switch
    {
        0 => "!N (net unreachable)",
        1 => "!H (host unreachable)",
        3 => "!P (port unreachable)",
        13 => "!X (admin prohibited)",
        _ => $"ICMP unreachable 3/{code}",
    };

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
}
