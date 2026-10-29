namespace MacosUtils.Core.SystemInfo;

/// <summary>A local user account on this Mac.</summary>
public sealed record LocalUserAccount(
    string Name,
    string FullName,
    int Uid,
    string Home,
    string Shell,
    bool IsAdmin,
    bool CanLogIn);

/// <summary>
/// Read-only view of the local user accounts from Directory Services (dscacheutil). The many
/// system accounts (_www, daemon, nobody...) are left out: only accounts with a UID of 500 or more,
/// the range macOS gives to people, plus root when it has a login shell.
/// </summary>
public static class LocalUsers
{
    private const string DsCacheUtil = "/usr/bin/dscacheutil";

    /// <summary>Returns the accounts sorted by name; empty when not on macOS or the query fails.</summary>
    public static IReadOnlyList<LocalUserAccount> GetAccounts()
    {
        if (!OperatingSystem.IsMacOS())
            return [];
        var users = Processes.TryRun(DsCacheUtil, ["-q", "user"]);
        if (users is null)
            throw new InvalidOperationException("Could not read the user accounts (dscacheutil failed).");
        var admins = Processes.TryRun(DsCacheUtil, ["-q", "group", "-a", "name", "admin"]) ?? "";
        return Parse(users, ParseGroupMembers(admins));
    }

    /// <summary>Parses "dscacheutil -q user" output; public for tests.</summary>
    public static IReadOnlyList<LocalUserAccount> Parse(string output, IReadOnlySet<string> admins)
    {
        var accounts = new Dictionary<string, LocalUserAccount>(StringComparer.Ordinal);
        foreach (var record in Records(output))
        {
            var name = record.GetValueOrDefault("name", "");
            if (name.Length == 0 || name.StartsWith('_'))
                continue;
            if (!int.TryParse(record.GetValueOrDefault("uid"), out var uid))
                continue;
            var shell = record.GetValueOrDefault("shell", "");
            var canLogIn = shell.Length > 0 && !shell.EndsWith("/false", StringComparison.Ordinal)
                && !shell.EndsWith("/nologin", StringComparison.Ordinal);
            if (uid < 500 && !(uid == 0 && canLogIn))
                continue;
            accounts[name] = new LocalUserAccount(
                name,
                record.GetValueOrDefault("gecos", ""),
                uid,
                record.GetValueOrDefault("dir", ""),
                shell,
                admins.Contains(name) || uid == 0,
                canLogIn);
        }
        return accounts.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The "users:" line of "dscacheutil -q group" output.</summary>
    public static IReadOnlySet<string> ParseGroupMembers(string output)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in Records(output))
        {
            foreach (var user in record.GetValueOrDefault("users", "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                members.Add(user);
        }
        return members;
    }

    /// <summary>"Administrator" or "Standard".</summary>
    public static string FormatType(LocalUserAccount a) => a.IsAdmin ? "Administrator" : "Standard";

    // dscacheutil prints "key: value" lines, one blank line between records.
    private static IEnumerable<Dictionary<string, string>> Records(string output)
    {
        var record = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                if (record.Count > 0)
                    yield return record;
                record = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            record[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (record.Count > 0)
            yield return record;
    }
}
