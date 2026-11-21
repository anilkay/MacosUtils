namespace MacosUtils.Core.SystemInfo;

public enum AtsRisk
{
    /// <summary>Plain HTTP or TLS below 1.2 for every connection, or an old TLS minimum for a domain.</summary>
    High,

    /// <summary>Plain HTTP for one domain, in web views or for media.</summary>
    Medium,

    /// <summary>Local network only, ciphers without forward secrecy, or a setting the OS ignores.</summary>
    Low,
}

/// <summary>One App Transport Security setting that loosens what an app's connections must use.</summary>
/// <param name="Scope">"All connections", "Web views", a domain...</param>
public sealed record AtsException(string App, string AppPath, string Scope, string Setting, AtsRisk Risk);

/// <summary>The apps that set exceptions, compared with how many apps were read.</summary>
public sealed record AtsScan(IReadOnlyList<AtsException> Exceptions, int AppsRead, bool SearchedSpotlight)
{
    public int AppsWithExceptions => Exceptions.Select(e => e.AppPath).Distinct().Count();
}

/// <summary>
/// macOS has no system-wide switch for TLS versions (Windows has Schannel's registry keys). What an
/// app may use is decided per app by App Transport Security: by default Apple's networking APIs
/// require HTTPS with TLS 1.2 or newer and forward secrecy, and an app loosens that with the
/// NSAppTransportSecurity dictionary in its Info.plist. This reads that dictionary for the installed
/// apps. Apps with their own TLS stack (Chrome, Firefox, curl) are not bound by ATS at all.
/// </summary>
public static class AppTransportSecurity
{
    /// <summary>Reads every installed app outside /System. macOS only (empty elsewhere).</summary>
    public static AtsScan Scan(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            return new AtsScan([], 0, false);
        progress?.Report("installed apps");
        var installed = InstalledApplications.Find(cancellationToken);
        var exceptions = new List<AtsException>();
        var read = 0;
        foreach (var app in installed.Apps.Where(a => !a.Path.StartsWith("/System/", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(app.Name);
            if (InstalledApplications.InfoPlist(app.Path) is not { } info)
                continue;
            read++;
            if (info.GetValueOrDefault("NSAppTransportSecurity") is Dictionary<string, object?> ats)
                exceptions.AddRange(Describe(app.Name, app.Path, ats));
        }
        var ordered = exceptions
            .OrderBy(e => e.Risk)
            .ThenBy(e => e.App, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Scope, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AtsScan(ordered, read, installed.SearchedSpotlight);
    }

    /// <summary>The loosening settings in one NSAppTransportSecurity dictionary. Public for tests.</summary>
    public static IReadOnlyList<AtsException> Describe(string app, string appPath, IReadOnlyDictionary<string, object?> ats)
    {
        var result = new List<AtsException>();
        void Add(string scope, string setting, AtsRisk risk) => result.Add(new AtsException(app, appPath, scope, setting, risk));

        var webContent = IsTrue(ats, "NSAllowsArbitraryLoadsInWebContent");
        var media = IsTrue(ats, "NSAllowsArbitraryLoadsForMedia");
        var local = IsTrue(ats, "NSAllowsLocalNetworking");
        if (IsTrue(ats, "NSAllowsArbitraryLoads"))
        {
            // Since macOS 10.12 the specific keys win: with any of them set, the blanket one is ignored.
            if (webContent || media || local)
                Add("All connections", "NSAllowsArbitraryLoads is set but ignored, because a narrower exception is also set", AtsRisk.Low);
            else
                Add("All connections", "Plain HTTP and TLS below 1.2 allowed everywhere (NSAllowsArbitraryLoads)", AtsRisk.High);
        }
        if (webContent)
            Add("Web views", "Web pages shown in the app may load over plain HTTP", AtsRisk.Medium);
        if (media)
            Add("Media", "Audio and video may load over plain HTTP", AtsRisk.Medium);
        if (local)
            Add("Local network", "Local addresses and .local names without TLS", AtsRisk.Low);

        if (ats.GetValueOrDefault("NSExceptionDomains") is Dictionary<string, object?> domains)
        {
            foreach (var (domain, value) in domains.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (value is not Dictionary<string, object?> d)
                    continue;
                var scope = IsTrue(d, "NSIncludesSubdomains") ? $"{domain} and subdomains" : domain;
                if (AnyTrue(d, "AllowsInsecureHTTPLoads"))
                    Add(scope, "Plain HTTP allowed", AtsRisk.Medium);
                var minimum = Variants("MinimumTLSVersion").Select(k => d.GetValueOrDefault(k) as string).FirstOrDefault(v => v is not null);
                if (minimum is not null && MinimumBelow12(minimum) is { } shown)
                    Add(scope, $"Minimum TLS lowered to {shown}", AtsRisk.High);
                if (Variants("RequiresForwardSecrecy").Any(k => d.GetValueOrDefault(k) is false))
                    Add(scope, "Ciphers without forward secrecy allowed", AtsRisk.Low);
            }
        }
        return result;
    }

    // "TLSv1.0" → "TLS 1.0"; null for 1.2 and newer (the default or stricter).
    private static string? MinimumBelow12(string value) => value.Trim().ToUpperInvariant() switch
    {
        "TLSV1.0" => "TLS 1.0",
        "TLSV1.1" => "TLS 1.1",
        "TLSV1.2" or "TLSV1.3" => null,
        _ => value,
    };

    // Each per-domain key exists in three spellings: NSException…, NSTemporaryException… (old) and NSThirdPartyException….
    private static IEnumerable<string> Variants(string name) =>
        [$"NSException{name}", $"NSTemporaryException{name}", $"NSThirdPartyException{name}"];

    private static bool AnyTrue(IReadOnlyDictionary<string, object?> d, string name) => Variants(name).Any(k => IsTrue(d, k));

    private static bool IsTrue(IReadOnlyDictionary<string, object?> d, string key) => d.GetValueOrDefault(key) is true;
}
