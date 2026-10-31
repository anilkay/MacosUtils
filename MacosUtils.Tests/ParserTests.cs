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
        var root = accounts.Single(a => a.Name == "root");
        Assert.Equal("Administrator", LocalUsers.FormatType(root));
        Assert.False(root.CanLogIn); // root has /bin/sh on every Mac but is disabled by default

        var withRootEnabled = LocalUsers.Parse(output, new HashSet<string>(), rootEnabled: true);
        Assert.True(withRootEnabled.Single(a => a.Name == "root").CanLogIn);
    }

    [Fact]
    public void Launchd_job_registered_by_an_app_names_its_bundle_program()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Label"] = "com.example.helper",
            ["BundleProgram"] = "Contents/MacOS/helper",
            ["AssociatedBundleIdentifiers"] = new List<object?> { "com.example.app" },
        };
        var item = StartupItems.FromDictionary(dict, "/Library/LaunchDaemons/com.example.helper.plist", "/Library/LaunchDaemons");
        Assert.Equal("Contents/MacOS/helper (in com.example.app)", item.Command);

        var empty = StartupItems.FromDictionary(new Dictionary<string, object?> { ["Label"] = "x" }, "/x.plist", "/Library/LaunchDaemons");
        Assert.Equal("(no program in the plist)", empty.Command);
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

    // An ICMP message quoting our TCP SYN: ICMP header (8 bytes), then the probe's IPv4 header
    // (20 bytes, protocol 6) and the first bytes of its TCP header (source and destination port).
    private static byte[] IcmpQuotingProbe(byte type, byte code, int sourcePort, int destPort, byte[] dest, bool withOuterIpHeader)
    {
        var icmp = new List<byte> { type, code, 0, 0, 0, 0, 0, 0 };
        var inner = new byte[20];
        inner[0] = 0x45;
        inner[9] = 6;
        dest.CopyTo(inner, 16);
        icmp.AddRange(inner);
        icmp.AddRange([(byte)(sourcePort >> 8), (byte)sourcePort, (byte)(destPort >> 8), (byte)destPort, 0, 0, 0, 0]);
        if (!withOuterIpHeader)
            return [.. icmp];
        var outer = new byte[20];
        outer[0] = 0x45;
        outer[9] = 1; // ICMP
        return [.. outer, .. icmp];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Icmp_time_exceeded_names_the_probe_it_quotes(bool withOuterIpHeader)
    {
        var packet = IcmpQuotingProbe(11, 0, 50123, 443, [93, 184, 216, 34], withOuterIpHeader);

        Assert.True(TcpTraceroute.TryParseIcmpReply(packet, out var reply));
        Assert.Equal(11, reply.Type);
        Assert.Equal(50123, reply.SourcePort);
        Assert.Equal(443, reply.DestPort);
        Assert.Equal("93.184.216.34", reply.DestAddress.ToString());
    }

    [Fact]
    public void Icmp_unreachable_keeps_its_code()
    {
        var packet = IcmpQuotingProbe(3, 13, 1000, 80, [10, 0, 0, 1], withOuterIpHeader: true);
        Assert.True(TcpTraceroute.TryParseIcmpReply(packet, out var reply));
        Assert.Equal((3, 13), (reply.Type, reply.Code));
    }

    [Fact]
    public void Icmp_echo_replies_and_short_packets_are_ignored()
    {
        var echo = IcmpQuotingProbe(0, 0, 1, 2, [1, 1, 1, 1], withOuterIpHeader: true);
        Assert.False(TcpTraceroute.TryParseIcmpReply(echo, out _));
        Assert.False(TcpTraceroute.TryParseIcmpReply(new byte[12], out _));
    }

    [Fact]
    public void Icmp_about_a_udp_probe_is_not_ours()
    {
        var packet = IcmpQuotingProbe(11, 0, 1, 2, [1, 1, 1, 1], withOuterIpHeader: false);
        packet[8 + 9] = 17; // the quoted packet was UDP
        Assert.False(TcpTraceroute.TryParseIcmpReply(packet, out _));
    }

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

    [Fact]
    public void Brew_version_is_read_from_the_first_line()
    {
        Assert.Equal("4.6.3", Homebrew.ParseVersion("Homebrew 4.6.3\nHomebrew/homebrew-core (git revision 1a2b; last commit 2026-10-01)\n"));
        Assert.Equal("4.6.3-12-gabcdef0", Homebrew.ParseVersion("Homebrew 4.6.3-12-gabcdef0"));
        Assert.Equal("", Homebrew.ParseVersion("brew: command not found"));
    }

    [Fact]
    public void Install_receipt_says_whether_a_formula_was_asked_for()
    {
        const string receipt = """
            {"homebrew_version":"4.6.3","used_options":[],"unused_options":[],"built_as_bottle":true,"poured_from_bottle":true,
             "loaded_from_api":true,"installed_as_dependency":false,"installed_on_request":true,"changed_files":[],
             "time":1760000000,"source_modified_time":1759000000,"compiler":"clang","runtime_dependencies":[]}
            """;
        var (onRequest, installedAt) = Homebrew.ParseReceipt(receipt);
        Assert.True(onRequest);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000).LocalDateTime, installedAt);

        var dependency = Homebrew.ParseReceipt("""{"installed_on_request":false,"installed_as_dependency":true}""");
        Assert.False(dependency.OnRequest);
        Assert.Null(dependency.InstalledAt);

        Assert.Equal((null, null), Homebrew.ParseReceipt("not json"));
        Assert.Equal((null, null), Homebrew.ParseReceipt("""{"installed_on_request":"yes","time":"later"}"""));
    }

    [Fact]
    public void Cask_definition_names_its_app_bundles()
    {
        const string cask = """
            {"token":"visual-studio-code","version":"1.105.1","artifacts":[
              {"uninstall":[{"quit":"com.microsoft.VSCode"}]},
              {"app":["Visual Studio Code.app"]},
              {"binary":["$APPDIR/Visual Studio Code.app/Contents/Resources/app/bin/code"]},
              {"app":["Foo Installer.app",{"target":"Foo.app"}]},
              {"app":["Evil.app",{"target":"../../etc/x.app"}]},
              {"zap":[{"trash":["~/Library/Application Support/Code"]}]}
            ]}
            """;
        Assert.Equal(["Visual Studio Code.app", "Foo.app"], Homebrew.ParseCaskApps(cask));
        Assert.Empty(Homebrew.ParseCaskApps("""{"token":"font-x","artifacts":[{"font":["X.ttf"]}]}"""));
        Assert.Empty(Homebrew.ParseCaskApps("{"));
    }

    [Fact]
    public void Keg_versions_sort_by_their_numbers()
    {
        var versions = new List<string> { "1.10.0", "1.9.2", "2.0_1", "2.0", "1.9.2_1" };
        versions.Sort(Homebrew.CompareVersions);
        Assert.Equal(["1.9.2", "1.9.2_1", "1.10.0", "2.0", "2.0_1"], versions);
    }

    // "ioreg -r -c AppleSmartBattery -a" on an Apple silicon MacBook (trimmed): percentages in
    // CurrentCapacity/MaxCapacity, mAh in the AppleRaw* keys, a discharge current written unsigned.
    private const string AppleSiliconBattery = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <array>
          <dict>
            <key>AppleRawCurrentCapacity</key><integer>3605</integer>
            <key>AppleRawMaxCapacity</key><integer>4210</integer>
            <key>NominalChargeCapacity</key><integer>4325</integer>
            <key>DesignCapacity</key><integer>4563</integer>
            <key>CurrentCapacity</key><integer>86</integer>
            <key>MaxCapacity</key><integer>100</integer>
            <key>CycleCount</key><integer>214</integer>
            <key>BatteryData</key><dict><key>DesignCycleCount70</key><integer>1000</integer></dict>
            <key>BatteryInstalled</key><true/>
            <key>ExternalConnected</key><false/>
            <key>IsCharging</key><false/>
            <key>FullyCharged</key><false/>
            <key>Temperature</key><integer>3061</integer>
            <key>Voltage</key><integer>12450</integer>
            <key>Amperage</key><integer>18446744073709550616</integer>
            <key>AvgTimeToEmpty</key><integer>412</integer>
            <key>TimeRemaining</key><integer>400</integer>
            <key>AvgTimeToFull</key><integer>65535</integer>
            <key>DeviceName</key><string>bq40z651</string>
            <key>PermanentFailureStatus</key><integer>0</integer>
          </dict>
        </array>
        </plist>
        """;

    [Fact]
    public void Apple_silicon_battery_reads_percent_charge_and_raw_capacities()
    {
        var entry = Assert.IsType<List<object?>>(Plist.Parse(AppleSiliconBattery)).OfType<Dictionary<string, object?>>().Single();
        var battery = Battery.FromRegistry(entry)!;

        Assert.Equal(86, battery.ChargePercent);
        Assert.Equal(214, battery.CycleCount);
        Assert.Equal(1000, battery.DesignCycleCount);
        Assert.Equal(4210, battery.FullChargeMah);
        Assert.Equal(3605, battery.CurrentChargeMah);
        Assert.Equal(4563, battery.DesignCapacityMah);
        Assert.Equal(94.8, battery.HealthPercent);      // nominal 4325 / design 4563
        Assert.Equal(30.61, battery.TemperatureCelsius);
        Assert.Equal(-1000, battery.AmperageMa);
        Assert.Equal(-12.45, battery.PowerWatts!.Value, 2);
        Assert.Equal(TimeSpan.FromMinutes(412), battery.TimeRemaining);
        Assert.Equal("On battery", battery.State);
        Assert.Contains(("Cycle Count", "214 of 1000 rated cycles", false), Battery.Describe(battery));
        Assert.Contains(("Temperature", "30.6 °C", false), Battery.Describe(battery));
        Assert.Equal("", battery.Adapter);
        Assert.False(battery.NeedsAttention);
    }

    [Fact]
    public void Apples_condition_and_maximum_capacity_win_over_the_computed_health()
    {
        const string profiler = """
            Power:

                Battery Information:

                  Model Information:
                      Device Name: bq40z651
                  Charge Information:
                      Fully Charged: No
                      Charging: No
                      State of Charge (%): 86
                  Health Information:
                      Cycle Count: 214
                      Condition: Service Recommended
                      Maximum Capacity: 79%
            """;
        Assert.Equal(("Service Recommended", 79.0, 214), Battery.ParseSystemProfiler(profiler));

        var entry = Assert.IsType<List<object?>>(Plist.Parse(AppleSiliconBattery)).OfType<Dictionary<string, object?>>().Single();
        var battery = Battery.FromRegistry(entry, profiler)!;
        Assert.Equal(79.0, battery.HealthPercent);
        Assert.Equal("Service Recommended", battery.Condition);
        Assert.True(battery.NeedsAttention);
    }

    [Fact]
    public void Intel_battery_reads_mah_capacities_and_the_adapter()
    {
        var entry = new Dictionary<string, object?>
        {
            ["BatteryInstalled"] = true,
            ["CurrentCapacity"] = 3000L,
            ["MaxCapacity"] = 4000L,
            ["DesignCapacity"] = 5000L,
            ["CycleCount"] = 640L,
            ["DesignCycleCount9C"] = 1000L,
            ["ExternalConnected"] = true,
            ["IsCharging"] = true,
            ["AvgTimeToFull"] = 75L,
            ["AdapterDetails"] = new Dictionary<string, object?> { ["Watts"] = 87L, ["Name"] = "87W USB-C Power Adapter" },
        };
        var battery = Battery.FromRegistry(entry)!;

        Assert.Equal(75, battery.ChargePercent);
        Assert.Equal(4000, battery.FullChargeMah);
        Assert.Equal(3000, battery.CurrentChargeMah);
        Assert.Equal(80.0, battery.HealthPercent);
        Assert.Equal(1000, battery.DesignCycleCount);
        Assert.Equal(TimeSpan.FromMinutes(75), battery.TimeRemaining);
        Assert.Equal("Charging", battery.State);
        Assert.Equal("87W USB-C Power Adapter", battery.Adapter);

        entry["AdapterDetails"] = new Dictionary<string, object?> { ["Watts"] = 60L };
        entry["IsCharging"] = false;
        battery = Battery.FromRegistry(entry)!;
        Assert.Equal("60 W adapter", battery.Adapter);
        Assert.Equal("On power adapter, not charging", battery.State);
        Assert.Null(battery.TimeRemaining);
    }

    [Fact]
    public void A_mac_without_a_battery_has_no_status()
    {
        Assert.Null(Battery.FromRegistry(new Dictionary<string, object?> { ["BatteryInstalled"] = false }));
    }
}
