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
    public void EverySystemTweak_HasAHelpTopic(string tweakId)
    {
        // Mirrors the Id values in SystemTweaksService.BuildTweaks. If a tweak is added there,
        // add a Help/tweaks/<id>.md and a row here so the "Learn more" link never dead-ends.
        Assert.True(HelpContentService.HasTopic("tweaks/" + tweakId), $"Help/tweaks/{tweakId}.md is missing");
    }

    [Fact]
    public void MissingTopic_ReturnsNull()
    {
        Assert.Null(HelpContentService.GetTopic("tweaks/does_not_exist"));
        Assert.False(HelpContentService.HasTopic(""));
    }
}
