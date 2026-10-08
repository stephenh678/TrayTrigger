using System.IO;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class HelpContentServiceTests
{
    [Fact]
    public void Parse_HandlesEveryBlockKind()
    {
        const string md = """
            # My Title
            Intro line one
            continues here.

            ## Section A
            - first bullet
            - second bullet
            > a note

            Closing paragraph.
            """;

        var topic = HelpContentService.Parse("x/y", md);

        Assert.Equal("My Title", topic.Title);
        Assert.Equal(new[]
        {
            (HelpBlockKind.Paragraph, "Intro line one continues here."),
            (HelpBlockKind.Heading, "Section A"),
            (HelpBlockKind.Bullet, "first bullet"),
            (HelpBlockKind.Bullet, "second bullet"),
            (HelpBlockKind.Note, "a note"),
            (HelpBlockKind.Paragraph, "Closing paragraph."),
        }, topic.Blocks.Select(b => (b.Kind, b.Text)).ToArray());
    }

    [Fact]
    public void Parse_FallsBackToIdWhenNoTitle()
    {
        var topic = HelpContentService.Parse("tweaks/thing", "just text");
        Assert.Equal("tweaks/thing", topic.Title);
        Assert.Single(topic.Blocks);
    }

    [Fact]
    public void EveryEmbeddedTopic_LoadsWithTitleAndContent()
    {
        var ids = HelpContentService.ListTopicIds();
        Assert.NotEmpty(ids);

        foreach (var id in ids)
        {
            var topic = HelpContentService.GetTopic(id);
            Assert.NotNull(topic);
            Assert.False(string.IsNullOrWhiteSpace(topic!.Title), $"{id} has no '# Title' line");
            Assert.NotEqual(id, topic.Title);
            Assert.True(topic.Blocks.Count >= 2, $"{id} has almost no content");
        }
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("mouse_accel")]
    [InlineData("hags")]
    [InlineData("windowed_opts")]
    [InlineData("fse_behavior")]
    [InlineData("power_plan")]
    [InlineData("game_mode")]
    [InlineData("timer_resolution")]
    [InlineData("visual_fx")]
    [InlineData("net_throttling")]
    [InlineData("nagle_disable")]
    [InlineData("delivery_opt")]
    [InlineData("game_dvr")]
    [InlineData("telemetry_sweeps")]
    [InlineData("game_bar_overlay")]
    [InlineData("core_isolation")]
    [InlineData("vrr_global")]
    [InlineData("auto_hdr")]
    [InlineData("sticky_keys")]
    [InlineData("mpo_disable")]
    [InlineData("wu_driver_exclude")]
    [InlineData("priority_separation")]
    [InlineData("nv_shader_cache")]
    [InlineData("dx_shader_cache")]
    public void EverySystemTweak_HasAHelpTopic(string tweakId)
    {
        // Mirrors the Id values in SystemTweaksService.BuildTweaks. If a tweak is added there,
        // add a Help/tweaks/<id>.md and a row here so the "Learn more" link never dead-ends.
        Assert.True(HelpContentService.HasTopic("tweaks/" + tweakId), $"Help/tweaks/{tweakId}.md is missing");
    }

    [Theory]
    // Topic ids referenced from XAML CommandParameter="..." or view-model HelpTopicId values.
    // If a link is added in the UI, add its id here so it can never dead-end.
    [InlineData("tweaks/overview")]
    [InlineData("tweaks/restore_point")]
    [InlineData("profiles/overview")]
    [InlineData("profiles/power_plan")]
    [InlineData("profiles/gpu_preference")]
    [InlineData("profiles/system_responsiveness")]
    [InlineData("profiles/mmcss_games_priority")]
    [InlineData("profiles/above_normal_priority")]
    [InlineData("profiles/defender_exclusion")]
    [InlineData("profiles/hdr")]
    [InlineData("profiles/timer_resolution")]
    [InlineData("profiles/do_not_disturb")]
    [InlineData("profiles/cpu_affinity")]
    [InlineData("profiles/nvidia_max_performance")]
    [InlineData("profiles/power_throttling")]
    [InlineData("profiles/frame_cap")]
    [InlineData("profiles/resizable_bar")]
    [InlineData("dlss/override")]
    [InlineData("dlss/indicator")]
    [InlineData("scanner/overview")]
    [InlineData("traymenu/overview")]
    [InlineData("scripts/overview")]
    [InlineData("updates/prerelease")]
    [InlineData("library/artwork")]
    [InlineData("library/titles")]
    [InlineData("library/game_info")]
    [InlineData("library/launch")]
    [InlineData("tools/overview")]
    [InlineData("tools/start_with_games")]
    [InlineData("troubleshooting/logs")]
    [InlineData("troubleshooting/backup")]
    [InlineData("library/suspend")]
    [InlineData("library/select_mode")]
    [InlineData("library/filtering")]
    [InlineData("general/overview")]
    [InlineData("scripts/writing")]
    [InlineData("library/api_keys")]
    public void EveryLinkedTopic_Exists(string topicId)
    {
        Assert.True(HelpContentService.HasTopic(topicId), $"Help/{topicId}.md is missing");
    }

    /// <summary>
    /// Every "Learn more" link in the views opens a page that exists. Read from the XAML itself, so
    /// a link added to a view is covered without anyone remembering to list it above.
    /// </summary>
    [Fact]
    public void EveryTopicLinkedFromAView_Exists()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "TrayTrigger.csproj"))) root = Path.GetDirectoryName(root);
        Assert.True(root != null, "The repository root (TrayTrigger.csproj) was not found above the test output folder.");

        // A help link's parameter is the only CommandParameter shaped "section/name" in lower case.
        var parameter = new System.Text.RegularExpressions.Regex("CommandParameter=\"(?<id>[a-z_]+/[a-z_]+)\"");
        int links = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "Views"), "*.xaml"))
        {
            string xaml = File.ReadAllText(file);
            if (!xaml.Contains("HelpCommands.ShowTopic", StringComparison.Ordinal)) continue;
            foreach (System.Text.RegularExpressions.Match match in parameter.Matches(xaml))
            {
                links++;
                string topicId = match.Groups["id"].Value;
                Assert.True(HelpContentService.HasTopic(topicId), $"{Path.GetFileName(file)} links to Help/{topicId}.md, which is missing");
            }
        }
        Assert.True(links >= 25, $"Only {links} help links were found in the views; the search is probably broken.");
    }

    [Fact]
    public void GetIndex_GroupsBySectionInDisplayOrder_WithOverviewFirst()
    {
        var index = HelpContentService.GetIndex();

        Assert.Equal(
            new[] { "tweaks", "profiles", "dlss", "scanner", "traymenu", "tools", "scripts", "library", "general", "updates", "troubleshooting" },
            index.Select(g => g.Section).ToArray());

        var tweaks = index.First(g => g.Section == "tweaks");
        Assert.Equal("Performance Tweaks", tweaks.Label);
        Assert.Equal("tweaks/overview", tweaks.Topics[0].Id);
        Assert.Equal("How Performance Tweaks work", tweaks.Topics[0].Title);
        Assert.True(tweaks.Topics.Count >= 17);

        // Everything after the overview is alphabetical by title, quotes ignored: the section has
        // "\"Ultimate Plan - TrayTrigger\" Power Plan (always on)", which belongs under U, not first.
        var rest = tweaks.Topics.Skip(1).Select(t => t.Title).ToList();
        Assert.Equal(rest.OrderBy(t => t.Replace("\"", ""), StringComparer.OrdinalIgnoreCase), rest);
        Assert.Contains(rest, t => t.StartsWith('"'));
        Assert.False(rest[0].StartsWith('"'), "a leading quote must not sort a topic to the top");
    }

    [Theory]
    [InlineData("tweaks/hags", "Performance Tweaks")]
    [InlineData("profiles/overview", "Performance Profiles")]
    [InlineData("scripts/overview", "Game Scripts")]
    [InlineData("misc/thing", "Misc")]
    public void SectionLabel_MapsKnownSections(string id, string expected)
    {
        Assert.Equal(expected, HelpContentService.SectionLabel(id));
    }

    [Fact]
    public void MissingTopic_ReturnsNull()
    {
        Assert.Null(HelpContentService.GetTopic("tweaks/does_not_exist"));
        Assert.False(HelpContentService.HasTopic(""));
    }
}
