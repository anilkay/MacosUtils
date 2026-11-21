using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace MacosUtils.Core.Net;

/// <summary>One certificate of the chain the server sent, leaf first.</summary>
public sealed record TlsChainCertificate(string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, string Status);

/// <summary>What a TLS handshake with a server negotiated, and the certificate it presented.</summary>
public sealed record TlsProbeResult(
    string Host,
    int Port,
    string RemoteAddress,
    string Protocol,
    string CipherSuite,
    string Subject,
    string Issuer,
    IReadOnlyList<string> DnsNames,
    DateTime NotBefore,
    DateTime NotAfter,
    string ValidationErrors,
    IReadOnlyList<TlsChainCertificate> Chain)
{
    /// <summary>Whole days until the certificate expires (negative once it has).</summary>
    public int DaysLeft => (int)Math.Floor((NotAfter - DateTime.Now).TotalDays);
}

public enum TlsVersionState
{
    Accepted,
    Refused,

    /// <summary>This Mac's openssl cannot offer the version, so the server's answer is unknown.</summary>
    NotTested,
}

/// <summary>Whether the server completed a handshake limited to one TLS version.</summary>
/// <param name="Detail">The cipher when accepted, the reason otherwise.</param>
public sealed record TlsVersionCheck(string Version, TlsVersionState State, string Detail);

/// <summary>
/// Checks a server's TLS from this Mac. <see cref="ProbeAsync"/> does one handshake with .NET's
/// SslStream and reports what it negotiated and the certificate chain. <see cref="CheckVersionsAsync"/>
/// asks macOS's own openssl (LibreSSL) for one handshake per TLS version, which shows the old
/// versions a server still accepts. Only handshakes run: no application data is sent. Revocation is
/// not checked, so nothing goes to the CA's CRL/OCSP servers.
/// </summary>
public static partial class TlsProbe
{
    public const int MaxHostLength = 253;
    private const string OpenSslTool = "/usr/bin/openssl";

    /// <summary>The versions tested one by one, with the s_client option that limits the handshake to each.</summary>
    public static readonly IReadOnlyList<(string Version, string Option)> Versions =
        [("TLS 1.0", "-tls1"), ("TLS 1.1", "-tls1_1"), ("TLS 1.2", "-tls1_2"), ("TLS 1.3", "-tls1_3")];

    /// <summary>
    /// Checks the host and port a user or the model typed: a bare DNS name or IP address (no URL,
    /// user@, path or leading "-", which openssl would read as an option) and a port of 1-65535.
    /// </summary>
    /// <exception cref="ArgumentException">With a message for the user.</exception>
    public static (string Host, int Port) Validate(string? host, int port)
    {
        var h = (host ?? "").Trim().Trim('[', ']');
        if (h.Length == 0 || h.Length > MaxHostLength)
            throw new ArgumentException("Enter a host name or IP address.");
        if (h.StartsWith('-') || Uri.CheckHostName(h) == UriHostNameType.Unknown)
            throw new ArgumentException($"Not a valid host name or IP address: {h}");
        if (port is < 1 or > 65535)
            throw new ArgumentException("The port must be between 1 and 65535.");
        return (h, port);
    }

    /// <summary>
    /// One handshake with the operating system's protocol defaults. A failed connection or
    /// handshake throws <see cref="IOException"/> with a message for the user; cancellation propagates.
    /// </summary>
    public static async Task<TlsProbeResult> ProbeAsync(string host, int port, int timeoutMs = 10000, CancellationToken cancellationToken = default)
    {
        (host, port) = Validate(host, port);
        timeoutMs = Math.Clamp(timeoutMs, 1000, 60000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        var endpoint = $"{host}:{port}";

        // Timeouts are set before connecting: macOS refuses socket options on a connection the peer already reset.
        using var client = new TcpClient { SendTimeout = timeoutMs, ReceiveTimeout = timeoutMs };
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"No connection to {endpoint} within {timeoutMs / 1000} s.");
        }
        catch (SocketException ex)
        {
            throw new IOException($"Could not connect to {endpoint}: {ex.Message}", ex);
        }

        var remote = client.Client.RemoteEndPoint is IPEndPoint ip
            ? (ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address).ToString()
            : "";

        // The callback runs during the handshake; the chain is only valid inside it, so copy what is shown.
        CertificateCapture? capture = null;
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.None, // the operating system's defaults
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            {
                capture = CertificateCapture.From(certificate, chain, errors);
                return true; // inspect only: nothing is sent after the handshake
            },
        };
        try
        {
            await ssl.AuthenticateAsClientAsync(options, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"The TLS handshake with {endpoint} did not finish within {timeoutMs / 1000} s.");
        }
        catch (Exception ex) when (ex is AuthenticationException or SocketException or IOException)
        {
            throw new IOException($"TLS handshake with {endpoint} failed: {ex.GetBaseException().Message}", ex);
        }

        var c = capture ?? throw new IOException($"{endpoint} did not send a certificate.");
        return new TlsProbeResult(host, port, remote, DescribeProtocol(ssl.SslProtocol), ssl.NegotiatedCipherSuite.ToString(),
            c.Subject, c.Issuer, c.DnsNames, c.NotBefore, c.NotAfter, c.Errors, c.Chain);
    }

    /// <summary>
    /// One openssl handshake per entry of <see cref="Versions"/>, run together. Empty off macOS or
    /// when /usr/bin/openssl is missing.
    /// </summary>
    public static async Task<IReadOnlyList<TlsVersionCheck>> CheckVersionsAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        (host, port) = Validate(host, port);
        if (!OperatingSystem.IsMacOS() || !File.Exists(OpenSslTool))
            return [];
        var isName = Uri.CheckHostName(host) == UriHostNameType.Dns;
        var connect = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
        var checks = Versions.Select(async v =>
        {
            // -servername sends SNI, which servers hosting several sites need; it is only valid for names.
            string[] args = isName
                ? ["s_client", "-connect", connect, "-servername", host, v.Option]
                : ["s_client", "-connect", connect, v.Option];
            try
            {
                return ParseSClient(v.Version, await Processes.RunAsync(OpenSslTool, args, TimeSpan.FromSeconds(10), cancellationToken));
            }
            catch (TimeoutException)
            {
                return new TlsVersionCheck(v.Version, TlsVersionState.Refused, "no answer within 10 s");
            }
        });
        return await Task.WhenAll(checks);
    }

    /// <summary>Reads one "openssl s_client -tls1_x" run (LibreSSL's output). Public for tests.</summary>
    public static TlsVersionCheck ParseSClient(string version, ToolResult result)
    {
        var text = result.Output + "\n" + result.Error;
        if (text.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no protocols available", StringComparison.OrdinalIgnoreCase))
            return new TlsVersionCheck(version, TlsVersionState.NotTested, "this Mac's openssl cannot offer it");

        var cipher = SessionCipher().Match(text) is { Success: true } m ? m.Groups[1].Value : "";
        if (SessionProtocol().IsMatch(text) && cipher.Length > 0 && cipher != "0000" && cipher != "(NONE)")
            return new TlsVersionCheck(version, TlsVersionState.Accepted, cipher);

        var reason = Alert().Match(text) is { Success: true } alert ? alert.Groups[1].Value.Trim()
            : text.Contains("errno=61", StringComparison.Ordinal) || text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ? "connection refused"
            : text.Contains("wrong version number", StringComparison.OrdinalIgnoreCase) ? "wrong version number"
            : "handshake failed";
        return new TlsVersionCheck(version, TlsVersionState.Refused, reason);
    }

    /// <summary>The result as label/value rows, shared by the TLS Check screen and the AI tool; Warn marks what needs a look.</summary>
    public static IEnumerable<(string Label, string Value, bool Warn)> Describe(TlsProbeResult? r, IReadOnlyList<TlsVersionCheck> versions)
    {
        if (r is not null)
        {
            yield return ("Server", $"{r.Host}:{r.Port} ({r.RemoteAddress})", false);
            yield return ("Negotiated", r.Protocol, r.Protocol is "TLS 1.0" or "TLS 1.1" or "SSL 3.0");
            yield return ("Cipher suite", r.CipherSuite, false);
            yield return ("Certificate", r.Subject, false);
            yield return ("Issuer", r.Issuer, false);
            if (r.DnsNames.Count > 0)
                yield return ("Names", string.Join(", ", r.DnsNames.Take(20)) + (r.DnsNames.Count > 20 ? $" (+{r.DnsNames.Count - 20})" : ""), false);
            yield return ("Valid", $"{r.NotBefore:yyyy-MM-dd} to {r.NotAfter:yyyy-MM-dd} ({DaysText(r.DaysLeft)})", r.DaysLeft < 30);
            yield return ("Validation", r.ValidationErrors == "None" ? "OK" : r.ValidationErrors, r.ValidationErrors != "None");
            for (var i = 0; i < r.Chain.Count; i++)
                yield return ($"Chain {i + 1}", $"{r.Chain[i].Subject} (until {r.Chain[i].NotAfter:yyyy-MM-dd}, {r.Chain[i].Status})", r.Chain[i].Status != "OK");
        }
        foreach (var v in versions)
        {
            var old = v.Version is "TLS 1.0" or "TLS 1.1";
            var value = v.State switch
            {
                TlsVersionState.Accepted => $"accepted ({v.Detail})" + (old ? ": outdated, should be turned off on the server" : ""),
                TlsVersionState.Refused => $"refused ({v.Detail})",
                _ => $"not tested ({v.Detail})",
            };
            yield return (v.Version, value, old && v.State == TlsVersionState.Accepted);
        }
    }

    private static string DaysText(int days) => days >= 0 ? $"{days} days left" : $"expired {-days} days ago";

    private static string DescribeProtocol(SslProtocols protocol) => protocol switch
    {
#pragma warning disable CS0618, SYSLIB0039 // named only to describe what was negotiated
        SslProtocols.Ssl3 => "SSL 3.0",
        SslProtocols.Tls => "TLS 1.0",
        SslProtocols.Tls11 => "TLS 1.1",
#pragma warning restore CS0618, SYSLIB0039
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.Tls13 => "TLS 1.3",
        _ => protocol.ToString(),
    };

    private sealed record CertificateCapture(
        string Subject, string Issuer, IReadOnlyList<string> DnsNames,
        DateTime NotBefore, DateTime NotAfter, string Errors, IReadOnlyList<TlsChainCertificate> Chain)
    {
        public static CertificateCapture? From(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null)
                return null;
            var leaf = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            var elements = chain?.ChainElements.Select(e => new TlsChainCertificate(
                e.Certificate.Subject, e.Certificate.Issuer, e.Certificate.NotBefore, e.Certificate.NotAfter,
                e.ChainElementStatus.Length == 0 ? "OK" : string.Join(", ", e.ChainElementStatus.Select(s => s.Status.ToString())))).ToList() ?? [];
            var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            return new CertificateCapture(leaf.Subject, leaf.Issuer, san is null ? [] : [.. san.EnumerateDnsNames()],
                leaf.NotBefore, leaf.NotAfter, errors == SslPolicyErrors.None ? "None" : errors.ToString(), elements);
        }
    }

    [GeneratedRegex(@"^\s*Protocol\s*:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex SessionProtocol();

    [GeneratedRegex(@"^\s*Cipher\s*:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex SessionCipher();

    [GeneratedRegex(@"(?:tlsv1|sslv3|tlsv13|tls)\s+alert\s+([a-z ]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Alert();
}
