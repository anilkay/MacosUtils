using MacosUtils.Core;
using MacosUtils.Core.Net;
using MacosUtils.Core.SystemInfo;
using Xunit;

namespace MacosUtils.Tests;

/// <summary>The parsers of macOS tool output, checked against captured samples so they run on any OS.</summary>
public class ParserTests
{
    [Fact]
    public void VmStat_counts_free_inactive_speculative_and_purgeable_pages()
    {
        const string output = """
            Mach Virtual Memory Statistics: (page size of 16384 bytes)
            Pages free:                               10000.
            Pages active:                            200000.
            Pages inactive:                           30000.
            Pages speculative:                         5000.
            Pages purgeable:                           1000.
            """;
        // (10000 + 30000 + 5000 + 1000) pages of 16 KiB.
        Assert.Equal(46000UL * 16384, SystemDetails.ParseAvailableMemory(output));
    }

    [Fact]
    public void VmStat_falls_back_to_4k_pages_without_a_header()
    {
        Assert.Equal(7UL * 4096, SystemDetails.ParseAvailableMemory("Pages free: 7."));
    }

    [Fact]
    public void DsCacheUtil_users_keep_people_and_drop_system_accounts()
    {
        const string output = """
            name: root
            password: *
            uid: 0
            gid: 0
            gecos: System Administrator
            dir: /var/root
            shell: /bin/sh

            name: _www
            uid: 70
            gecos: World Wide Web Server
            dir: /Library/WebServer
            shell: /usr/bin/false

            name: daemon
            uid: 1
            dir: /var/root
            shell: /usr/bin/false

            name: ayse
            password: ********
            uid: 501
            gid: 20
            gecos: Ayşe Yılmaz
            dir: /Users/ayse
            shell: /bin/zsh

            name: misafir
            uid: 502
            gecos: Guest
            dir: /Users/misafir
            shell: /usr/bin/false
            """;
        var accounts = LocalUsers.Parse(output, new HashSet<string> { "root", "ayse" });

        Assert.Equal(["ayse", "misafir", "root"], accounts.Select(a => a.Name).ToArray());
        var ayse = accounts.Single(a => a.Name == "ayse");
        Assert.True(ayse.IsAdmin);
        Assert.True(ayse.CanLogIn);
        Assert.Equal("Ayşe Yılmaz", ayse.FullName);
        Assert.False(accounts.Single(a => a.Name == "misafir").CanLogIn);
        Assert.Equal("Administrator", LocalUsers.FormatType(accounts.Single(a => a.Name == "root")));
    }

    [Fact]
    public void DsCacheUtil_root_without_a_login_shell_is_hidden()
    {
        var accounts = LocalUsers.Parse("name: root\nuid: 0\nshell: /usr/bin/false\n", new HashSet<string>());
        Assert.Empty(accounts);
    }

    [Fact]
    public void Admin_group_members_are_read_from_the_users_line()
    {
        const string output = "name: admin\npassword: *\ngid: 80\nusers: root ayse\n";
        Assert.Equal(["ayse", "root"], LocalUsers.ParseGroupMembers(output).Order().ToArray());
    }

    [Theory]
    [InlineData("Firewall is enabled. (State = 1)", true)]
    [InlineData("Firewall is disabled. (State = 0)", false)]
    [InlineData("Stealth mode enabled", true)]
    [InlineData("Stealth mode disabled", false)]
    [InlineData("Block all DISABLED!", false)]
    [InlineData("Automatically allow signed built-in software ENABLED", true)]
    [InlineData("something else", null)]
    public void Firewall_switches_are_read_from_socketfilterfw_text(string output, bool? expected) =>
        Assert.Equal(expected, FirewallInfo.ParseSwitch(output));

    [Fact]
    public void Firewall_app_rules_pair_each_path_with_its_rule()
    {
        const string output = """
            Total number of apps = 2

            1 :  /Applications/Safari.app
                 ( Allow incoming connections )

            2 :  /Applications/Some Tool.app
                 ( Block incoming connections )
            """;
        var rules = FirewallInfo.ParseAppRules(output);
        Assert.Equal(2, rules.Count);
        Assert.Equal(new FirewallAppRule("/Applications/Safari.app", true), rules[0]);
        Assert.Equal(new FirewallAppRule("/Applications/Some Tool.app", false), rules[1]);
    }

    [Fact]
    public void Lsof_listeners_are_parsed_and_connections_are_ignored()
    {
        const string output =
            "p312\ncControlCenter\nPTCP\nn*:7000\nn[::1]:5000\n" +
            "p900\ncpostgres\nPTCP\nn127.0.0.1:5432\n" +
            "p901\ncmDNSResponder\nPUDP\nn*:5353\nn10.0.0.2:5353->10.0.0.9:6000\n";
        var ports = FirewallInfo.ParseLsof(output);

        Assert.Equal(4, ports.Count);
        Assert.Contains(ports, p => p is { Protocol: "TCP", Port: 7000, Process: "ControlCenter", Pid: 312, IsExposed: true });
        Assert.Contains(ports, p => p is { Port: 5432, Process: "postgres", IsExposed: false });
        Assert.Contains(ports, p => p is { Protocol: "TCP", Port: 5000, IsExposed: false }); // [::1] is loopback
        Assert.Contains(ports, p => p is { Protocol: "UDP", Port: 5353, Address: "All interfaces" });
    }

    [Fact]
    public void Traceroute_hop_with_three_answers()
    {
        var hop = TcpTraceroute.ParseHopLine(" 3  10.0.0.1  5.1 ms  10.0.0.1  6.2 ms  4.9 ms", "93.184.216.34");
        Assert.NotNull(hop);
        Assert.Equal(3, hop.Ttl);
        Assert.Equal("10.0.0.1", hop.Address);
        Assert.Equal([5L, 6L, 5L], [hop.Rtt1Ms!.Value, hop.Rtt2Ms!.Value, hop.Rtt3Ms!.Value]);
        Assert.Null(hop.Note);
    }

    [Fact]
    public void Traceroute_silent_hop_has_no_address()
    {
        var hop = TcpTraceroute.ParseHopLine(" 4  * * *", "93.184.216.34");
        Assert.NotNull(hop);
        Assert.Null(hop.Address);
        Assert.Null(hop.Rtt1Ms);
    }

    [Fact]
    public void Traceroute_reaching_the_target_is_marked()
    {
        var hop = TcpTraceroute.ParseHopLine(" 9  93.184.216.34  20.1 ms  19.8 ms  20.0 ms", "93.184.216.34");
        Assert.Equal("Reached", hop!.Note);
    }

    [Fact]
    public void Traceroute_unreachable_flag_becomes_a_note()
    {
        var hop = TcpTraceroute.ParseHopLine(" 6  172.16.0.1  3.0 ms !H  2.9 ms  3.1 ms", "93.184.216.34");
        Assert.Equal("!H (host unreachable)", hop!.Note);
    }

    [Theory]
    [InlineData("traceroute to example.com (93.184.216.34), 30 hops max")]
    [InlineData("")]
    public void Traceroute_other_lines_are_not_hops(string line) =>
        Assert.Null(TcpTraceroute.ParseHopLine(line, "1.1.1.1"));

    [Fact]
    public void Launchd_plist_gives_label_command_and_flags()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key><string>com.example.updater</string>
                <key>ProgramArguments</key>
                <array>
                    <string>/Applications/Example App.app/Contents/MacOS/updater</string>
                    <string>--background</string>
                </array>
                <key>RunAtLoad</key><true/>
                <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
            </dict>
            </plist>
            """;
        var dict = Assert.IsType<Dictionary<string, object?>>(Plist.Parse(xml));
        var item = StartupItems.FromDictionary(dict, "/Users/x/Library/LaunchAgents/com.example.updater.plist", "~/Library/LaunchAgents");

        Assert.Equal("com.example.updater", item.Label);
        Assert.Equal("\"/Applications/Example App.app/Contents/MacOS/updater\" --background", item.Command);
        Assert.True(item.RunAtLoad);
        Assert.True(item.KeepAlive);
        Assert.False(item.Disabled);
    }

    [Fact]
    public void Plist_values_keep_their_types()
    {
        const string xml = """
            <plist version="1.0"><dict>
              <key>n</key><integer>42</integer>
              <key>r</key><real>1.5</real>
              <key>b</key><false/>
              <key>list</key><array><string>a</string><integer>2</integer></array>
            </dict></plist>
            """;
        var dict = Assert.IsType<Dictionary<string, object?>>(Plist.Parse(xml));
        Assert.Equal(42L, dict["n"]);
        Assert.Equal(1.5, dict["r"]);
        Assert.Equal(false, dict["b"]);
        Assert.Equal(new object?[] { "a", 2L }, ((List<object?>)dict["list"]!).ToArray());
    }

    [Fact]
    public void Launchctl_print_disabled_lists_each_service_state()
    {
        const string output = """
            disabled services = {
                "com.openssh.sshd" => disabled
                "com.apple.screensharing" => enabled
                "com.apple.smbd" => true
            }
            """;
        var states = MacFeatures.ParseDisabledServices(output);
        Assert.True(states["com.openssh.sshd"]);
        Assert.False(states["com.apple.screensharing"]);
        Assert.True(states["com.apple.smbd"]);
    }

    [Fact]
    public void Env_dash_zero_output_keeps_values_with_newlines()
    {
        var variables = EnvironmentInfo.ParseNullSeparated("PATH=/usr/bin:/bin\0MULTI=line1\nline2\0EMPTY=\0");
        Assert.Equal("/usr/bin:/bin", variables["PATH"]);
        Assert.Equal("line1\nline2", variables["MULTI"]);
        Assert.Equal("", variables["EMPTY"]);
    }

    [Theory]
    [InlineData("15.1", "Sequoia")]
    [InlineData("26.0", "Tahoe")]
    [InlineData("10.15", "")]
    public void Marketing_names_follow_the_major_version(string version, string expected) =>
        Assert.Equal(expected, SystemDetails.MacOSMarketingName(version));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void Bytes_are_formatted_for_people(long bytes, string expected)
    {
        // The formatter follows the user's culture ("1,5 KB" on a Turkish Mac); the test pins one.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, ByteFormatter.FormatBytes(bytes));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Sort_key_orders_java_legacy_and_modern_versions()
    {
        var versions = new[] { "1.8.0_402", "21.0.4", "17.0.11", "11.0.23" };
        var sorted = versions.OrderByDescending(RuntimeProbe.SortKey).ToArray();
        Assert.Equal(["21.0.4", "17.0.11", "11.0.23", "1.8.0_402"], sorted);
    }
}
