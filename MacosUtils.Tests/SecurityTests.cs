using MacosUtils.AI;
using MacosUtils.Core;
using MacosUtils.Core.IO;
using MacosUtils.Core.SystemInfo;
using Xunit;

namespace MacosUtils.Tests;

/// <summary>The path rules for AI tools (the model picks the paths), tool approval flags and the macOS scan exclusions.</summary>
public class SecurityTests
{
    [Fact]
    public void Home_shortcut_resolves_to_the_home_folder() =>
        Assert.Equal(AppPaths.Home, PcTools.ResolveDirectory("home"));

    [Fact]
    public void Tilde_is_expanded_to_the_home_folder()
    {
        var path = PcTools.ToLocalPath("~/Documents/notes.txt");
        Assert.NotNull(path);
        Assert.StartsWith(AppPaths.Home, path);
        Assert.EndsWith("notes.txt", path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_paths_are_refused(string path) => Assert.Null(PcTools.ToLocalPath(path));

    [Fact]
    public void Unknown_directories_do_not_resolve() =>
        Assert.Null(PcTools.ResolveDirectory(Path.Combine(AppPaths.Home, "definitely-not-here-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void Tool_list_marks_private_data_and_network_tools_for_approval()
    {
        var tools = PcTools.CreateTools();
        var gated = tools.Where(t => t.NeedsApproval).Select(t => t.Function.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            [nameof(PcTools.GetEnvironmentVariable), nameof(PcTools.ListEnvironmentVariables), nameof(PcTools.PingHost),
                nameof(PcTools.ReadTextFile), nameof(PcTools.TraceTcpRoute)],
            gated);
        Assert.All(tools.Where(t => t.Function.Name is nameof(PcTools.PingHost) or nameof(PcTools.TraceTcpRoute)),
            t => Assert.True(t.OpenWorld));
    }

    [Fact]
    public void Every_tool_has_a_unique_name_and_a_description()
    {
        var tools = PcTools.CreateTools();
        Assert.Equal(tools.Length, tools.Select(t => t.Function.Name).Distinct().Count());
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Function.Description)));
    }

    [Fact]
    public void Approval_prompt_names_the_file_and_where_it_goes()
    {
        var call = new Microsoft.Extensions.AI.FunctionCallContent("1", nameof(PcTools.ReadTextFile),
            new Dictionary<string, object?> { ["path"] = "~/Documents/secret.txt" });
        var request = ChatSession.DescribeToolCall(call, "api.example.com");
        Assert.Equal(nameof(PcTools.ReadTextFile), request.ToolName);
        Assert.Contains("secret.txt", request.Prompt);
        Assert.Contains("api.example.com", request.Prompt);
        Assert.EndsWith("Allow?", request.Prompt);
    }

    [Fact]
    public void Scan_exclusions_do_nothing_off_macos()
    {
        if (OperatingSystem.IsMacOS())
            return;
        Assert.False(ScanExclusions.ShouldSkip("/System/Volumes"));
        Assert.False(Volumes.IsNetworkPath("/net/host"));
    }

    [Fact]
    public void Mac_only_features_return_empty_instead_of_throwing_elsewhere()
    {
        if (OperatingSystem.IsMacOS())
            return;
        Assert.Empty(StartupItems.GetItems());
        Assert.Empty(MacFeatures.Get());
        Assert.Empty(LocalUsers.GetAccounts());
        Assert.Empty(JavaDetector.Find());
        Assert.Equal((0UL, 0UL), SystemDetails.Memory());
    }

    [Fact]
    public void Chat_settings_round_trip_without_the_api_key()
    {
        var path = Path.Combine(Path.GetTempPath(), "macosutils-chat-" + Guid.NewGuid().ToString("N"), "chat.json");
        try
        {
            ChatSettingsStore.Save(new ChatSettings("https://api.example.com/v1", "model-x", "Low"), path);
            Assert.Equal(new ChatSettings("https://api.example.com/v1", "model-x", "Low"), ChatSettingsStore.Load(path));
            Assert.DoesNotContain("key", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ChatSettingsStore.Delete(path);
        }
    }

    [Fact]
    public void Credentials_cannot_be_saved_without_a_host() =>
        Assert.Throws<ArgumentException>(() => CredentialStore.Save("secret", " "));

    [Fact]
    public void Developer_caches_are_distinct_folders_inside_the_home_folder()
    {
        var locations = DeveloperCaches.Locations("/Users/someone");
        Assert.All(locations, l => Assert.StartsWith("/Users/someone/", l.Path.Replace('\\', '/')));
        Assert.Equal(locations.Count, locations.Select(l => l.Path).Distinct().Count());
        // Nothing on the list contains another entry, so one Trash move never takes a second item with it.
        Assert.DoesNotContain(locations, a => locations.Any(b => a != b && b.Path.StartsWith(a.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("~/Library/Caches")]
    public void Cleanup_refuses_to_trash_folders_that_are_not_on_the_list(string path) =>
        Assert.Throws<InvalidOperationException>(() => DeveloperCaches.MoveToTrash(path));

    [Fact]
    public void Cleanup_refuses_to_trash_the_simulators()
    {
        var simulators = DeveloperCaches.Locations(AppPaths.Home).Single(l => l.Name == "Simulator devices");
        Assert.False(simulators.CanTrash);
        Assert.Throws<InvalidOperationException>(() => DeveloperCaches.MoveToTrash(simulators.Path));
    }
}
