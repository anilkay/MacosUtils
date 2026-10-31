using MacosUtils.AI;
using MacosUtils.Core;
using MacosUtils.Core.IO;
using MacosUtils.Core.Net;
using MacosUtils.Core.SystemInfo;
using Xunit;

namespace MacosUtils.Tests;

/// <summary>
/// Checks against the real operating system. They run on the macOS CI runner and return at once
/// elsewhere (xUnit 2 cannot skip at run time), so a native-call mistake shows up in CI instead of
/// on a user's Mac.
/// </summary>
public class MacOnlyTests
{
    [Fact]
    public void System_details_are_read_from_the_real_machine()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var (name, version, _) = SystemDetails.MacOSVersion();
        Assert.Equal("macOS", name);
        Assert.NotEmpty(version);
        Assert.NotEmpty(SystemDetails.ModelIdentifier());
        Assert.NotEqual("Unknown", SystemDetails.CpuName());
        var (total, available) = SystemDetails.Memory();
        Assert.True(total > 1UL << 30, "total memory");
        Assert.InRange(available, 1UL, total);
        Assert.InRange(SystemDetails.Uptime(), TimeSpan.FromSeconds(1), TimeSpan.FromDays(3650));
        Assert.NotNull(SystemDetails.BootTime());
    }

    [Fact]
    public void Startup_volume_is_listed_with_a_name_and_space()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var root = Assert.Single(Volumes.GetVolumes(), v => v.MountPoint == "/");
        Assert.NotEmpty(root.Name);
        Assert.True(root.TotalBytes > 0);
        Assert.False(root.IsNetwork);
        Assert.Contains("/", Volumes.LocalScanRoots());
    }

    [Fact]
    public void Files_can_be_moved_to_the_trash_and_come_back_from_it()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var path = Path.Combine(Path.GetTempPath(), "macosutils-trash-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "trash me");

        Trash.MoveToTrash(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Moving_a_missing_file_to_the_trash_fails_cleanly()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        Assert.Throws<FileNotFoundException>(() => Trash.MoveToTrash("/tmp/macosutils-does-not-exist-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void System_version_plist_is_read()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var dict = Plist.LoadDictionary("/System/Library/CoreServices/SystemVersion.plist");
        Assert.NotNull(dict);
        Assert.Equal("macOS", dict["ProductName"]);
    }

    [Fact]
    public void Network_tools_run_and_report()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        Assert.NotEmpty(NetworkInfo.GetAdapters());
        Assert.NotNull(FirewallInfo.GetStatus());
        _ = FirewallInfo.GetListeningPorts(); // may be empty on a runner; must not throw
        _ = FirewallInfo.GetAppRules();
    }

    [Fact]
    public void Security_features_are_reported()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var features = MacFeatures.Get();
        Assert.Contains(features, f => f.Id == "sip");
        Assert.Contains(features, f => f.Id == "gatekeeper");
        Assert.Contains(features, f => f.Id == "firewall");
    }

    [Fact]
    public void Users_include_the_current_user_and_the_admin_group_is_read()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var accounts = LocalUsers.GetAccounts();
        Assert.Contains(accounts, a => a.Name == Environment.UserName);
    }

    [Fact]
    public void Login_shell_environment_contains_a_path()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var variables = EnvironmentInfo.Get(EnvironmentSource.LoginShell);
        Assert.Contains(variables, v => v.Key == "PATH" && v.Value.Contains("/usr/bin"));
    }

    [Fact]
    public async Task Tcp_traceroute_reaches_loopback_in_one_hop()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var hops = await TcpTraceroute.TraceAsync("127.0.0.1", 80, maxHops: 3, timeoutMs: 1000, resolveHostNames: false);

        Assert.NotEmpty(hops);
        Assert.Equal(1, hops[0].Ttl);
    }

    [Fact]
    public void Keychain_stores_and_removes_a_key_per_host()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // A CI runner's login keychain is unlocked; a locked one (a user's Mac with no session) makes
        // the Keychain return an error status, which is not what this test is about.
        var host = "test-" + Guid.NewGuid().ToString("N") + ".example.com";
        try
        {
            CredentialStore.Save("sk-test-secret", host);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        try
        {
            Assert.Equal("sk-test-secret", CredentialStore.Load(host));
            Assert.Null(CredentialStore.Load("other-" + host));
            CredentialStore.Save("sk-second", host);
            Assert.Equal("sk-second", CredentialStore.Load(host));
        }
        finally
        {
            CredentialStore.Delete(host);
        }
        Assert.Null(CredentialStore.Load(host));
    }

    [Fact]
    public void Tools_return_text_and_never_throw()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        foreach (var output in new[]
                 {
                     PcTools.GetSystemInfo(), PcTools.GetVolumes(), PcTools.GetMacFeatures(), PcTools.GetStartupItems(),
                     PcTools.GetLocalUsers(), PcTools.GetNetworkAdapters(), PcTools.GetFirewallStatus(), PcTools.GetListeningPorts(),
                     PcTools.GetDotNetVersions(), PcTools.GetJavaVersion(), PcTools.GetPythonVersion(), PcTools.GetNodeVersion(),
                     PcTools.GetProcesses(5), PcTools.GetLargestFiles("home", 3, recursive: false), PcTools.GetFolderSizes("home", 3),
                     PcTools.GetHomebrewPackages(20),
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(output));
            Assert.False(output.StartsWith("Error:", StringComparison.Ordinal), output);
        }
    }

    [Theory]
    [InlineData("/net/example.com/export")]
    [InlineData("/Network/Servers")]
    [InlineData("/home/someone")]
    public void Automount_paths_are_refused_for_ai_tools(string path)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        Assert.Null(PcTools.ToLocalPath(path));
        Assert.True(Volumes.IsNetworkPath(path));
    }

    [Fact]
    public void Scans_skip_the_firmlinked_system_volumes()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        Assert.True(ScanExclusions.ShouldSkip("/System/Volumes"));
        Assert.True(ScanExclusions.ShouldSkip("/Volumes"));
        Assert.False(ScanExclusions.ShouldSkip(AppPaths.Home));
    }
}
