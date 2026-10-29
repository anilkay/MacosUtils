using MacosUtils.Core.Net;

namespace MacosUtils.Core.SystemInfo;

public enum FeatureState { Enabled, Disabled, NotInstalled, Unknown }

/// <summary>A macOS security or sharing feature and whether it is on.</summary>
public sealed record MacFeature(string Name, string Id, FeatureState State, string Detail)
{
    public string Display => State switch
    {
        FeatureState.Enabled => "On",
        FeatureState.Disabled => "Off",
        FeatureState.NotInstalled => "Not installed",
        _ => "Unknown",
    } + (Detail.Length > 0 ? $" ({Detail})" : "");
}

/// <summary>
/// The Mac counterpart of WindowsUtils' optional-features check: security settings (SIP,
/// Gatekeeper, FileVault, firewall), sharing services that open the Mac to the network (Remote
/// Login, Screen Sharing, File Sharing) and developer components (Command Line Tools, Rosetta 2,
/// Homebrew). Only status commands that work without administrator rights are used. Read-only.
/// </summary>
public static class MacFeatures
{
    private const string Csrutil = "/usr/bin/csrutil";
    private const string Spctl = "/usr/sbin/spctl";
    private const string Fdesetup = "/usr/bin/fdesetup";
    private const string Launchctl = "/bin/launchctl";
    private const string XcodeSelect = "/usr/bin/xcode-select";

    public static IReadOnlyList<MacFeature> Get()
    {
        if (!OperatingSystem.IsMacOS())
            return [];

        var features = new List<MacFeature>
        {
            FromOutput("System Integrity Protection", "sip", Processes.TryRun(Csrutil, ["status"]),
                on: "status: enabled", off: "status: disabled"),
            // spctl prints its answer on stderr on some versions, so both streams are read.
            FromOutput("Gatekeeper", "gatekeeper", RunBoth(Spctl, ["--status"]),
                on: "assessments enabled", off: "assessments disabled"),
            FromOutput("FileVault disk encryption", "filevault", Processes.TryRun(Fdesetup, ["status"]),
                on: "FileVault is On", off: "FileVault is Off"),
        };

        var firewall = FirewallInfo.GetStatus();
        features.Add(new MacFeature("Application Firewall", "firewall",
            firewall.Enabled switch { true => FeatureState.Enabled, false => FeatureState.Disabled, _ => FeatureState.Unknown },
            firewall.StealthMode == true ? "stealth mode on" : ""));

        // Sharing services are launchd jobs; "launchctl print-disabled system" says which are off.
        var disabled = ParseDisabledServices(Processes.TryRun(Launchctl, ["print-disabled", "system"]) ?? "");
        features.Add(Service("Remote Login (SSH)", "com.openssh.sshd", disabled));
        features.Add(Service("Screen Sharing", "com.apple.screensharing", disabled));
        features.Add(Service("File Sharing (SMB)", "com.apple.smbd", disabled));
        features.Add(Service("Remote Management (ARD)", "com.apple.RemoteDesktop.agent", disabled));

        var cltPath = Processes.TryRun(XcodeSelect, ["-p"])?.Trim();
        features.Add(new MacFeature("Xcode Command Line Tools", "clt",
            string.IsNullOrEmpty(cltPath) ? FeatureState.NotInstalled : FeatureState.Enabled, cltPath ?? ""));

        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
        {
            features.Add(new MacFeature("Rosetta 2", "rosetta",
                File.Exists("/Library/Apple/usr/share/rosetta/rosetta") ? FeatureState.Enabled : FeatureState.NotInstalled, ""));
        }

        var brew = new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew" }.FirstOrDefault(File.Exists);
        features.Add(new MacFeature("Homebrew", "homebrew",
            brew is null ? FeatureState.NotInstalled : FeatureState.Enabled, brew is null ? "" : Path.GetDirectoryName(Path.GetDirectoryName(brew))!));
        return features;
    }

    /// <summary>
    /// Parses "launchctl print-disabled system": lines like <c>"com.openssh.sshd" => disabled</c>
    /// (or <c>=> true</c> on older systems). Returns label → is disabled. Public for tests.
    /// </summary>
    public static Dictionary<string, bool> ParseDisabledServices(string output)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var arrow = line.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0)
                continue;
            var label = line[..arrow].Trim().Trim('"');
            var value = line[(arrow + 2)..].Trim().ToLowerInvariant();
            if (label.Length > 0)
                result[label] = value is "disabled" or "true";
        }
        return result;
    }

    // A sharing service missing from the list was never switched on (its plist ships disabled).
    private static MacFeature Service(string name, string label, Dictionary<string, bool> disabled) =>
        new(name, label, disabled.TryGetValue(label, out var off) ? (off ? FeatureState.Disabled : FeatureState.Enabled) : FeatureState.Disabled, "");

    private static MacFeature FromOutput(string name, string id, string? output, string on, string off)
    {
        if (output is null)
            return new MacFeature(name, id, FeatureState.Unknown, "");
        if (output.Contains(on, StringComparison.OrdinalIgnoreCase))
            return new MacFeature(name, id, FeatureState.Enabled, "");
        if (output.Contains(off, StringComparison.OrdinalIgnoreCase))
            return new MacFeature(name, id, FeatureState.Disabled, "");
        return new MacFeature(name, id, FeatureState.Unknown, output.Trim());
    }

    private static string? RunBoth(string tool, string[] arguments)
    {
        if (!File.Exists(tool))
            return null;
        try
        {
            var result = Processes.Run(tool, arguments);
            return result.Output + result.Error;
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
