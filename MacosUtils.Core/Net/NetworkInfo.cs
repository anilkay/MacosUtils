using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MacosUtils.Core.Net;

/// <summary>A network adapter with its first IPv4 and global IPv6 address.</summary>
public sealed record NetworkAdapter(string Name, string Type, string Status, string IPv4, string IPv6, string Mac);

/// <summary>Result of a single ping.</summary>
public sealed record PingResult(bool Success, string? Address, long RoundtripMs, string Status);

public static class NetworkInfo
{
    public static IReadOnlyList<NetworkAdapter> GetAdapters()
    {
        var adapters = new List<NetworkAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            var ipv4 = properties.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address.ToString() ?? "";
            var ipv6 = properties.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)
                ?.Address.ToString() ?? "";

            adapters.Add(new NetworkAdapter(
                nic.Name,
                nic.NetworkInterfaceType.ToString(),
                nic.OperationalStatus.ToString(),
                ipv4,
                ipv6,
                FormatMac(nic.GetPhysicalAddress())));
        }
        return adapters;
    }

    /// <summary>Pings <paramref name="host"/> <paramref name="count"/> times (1-10), 3 s timeout each.</summary>
    public static async Task<IReadOnlyList<PingResult>> PingAsync(string host, int count = 4, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        count = Math.Clamp(count, 1, 10);

        using var ping = new Ping();
        var results = new List<PingResult>(count);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reply = await ping.SendPingAsync(host.Trim(), TimeSpan.FromSeconds(3), cancellationToken: cancellationToken);
            results.Add(new PingResult(reply.Status == IPStatus.Success, reply.Address?.ToString(), reply.RoundtripTime, reply.Status.ToString()));
        }
        return results;
    }

    public static string FormatMac(PhysicalAddress mac)
    {
        var value = mac.ToString();
        if (value.Length != 12)
            return value;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => value.Substring(i * 2, 2)));
    }
}
