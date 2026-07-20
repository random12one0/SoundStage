using Soundstage.Core.Automation;
using Xunit;

namespace Soundstage.Core.Tests.Automation;

public class AutomationTemplatesTests
{
    [Fact]
    public void SixTemplates_AreOffered_WithGlyphsAndCopy()
    {
        Assert.Equal(6, AutomationTemplates.All.Count);
        Assert.All(AutomationTemplates.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.False(string.IsNullOrWhiteSpace(t.Glyph));
        });
    }

    [Fact]
    public void EachTemplate_BuildsAnEnabledRule_WithTriggerAndActions()
    {
        foreach (var template in AutomationTemplates.All)
        {
            var rule = template.Build();
            Assert.True(rule.Enabled);
            Assert.NotNull(rule.Trigger);
            Assert.NotEmpty(rule.Actions);
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));
            Assert.False(string.IsNullOrWhiteSpace(rule.Id));
        }
    }

    [Fact]
    public void Build_ProducesFreshInstances_WithUniqueIds()
    {
        var template = AutomationTemplates.All[0];
        var a = template.Build();
        var b = template.Build();
        Assert.NotEqual(a.Id, b.Id);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void QuietHours_IsAnOvernightScheduleWithNightAndLoudness()
    {
        var rule = AutomationTemplates.All.Single(t => t.Id == "tmpl-quiet-hours").Build();
        var schedule = Assert.IsType<ScheduleTrigger>(rule.Trigger);
        Assert.True(schedule.Start > schedule.End); // overnight wrap
        Assert.Equal(2, rule.Actions.Count);
    }

    [Fact]
    public void Templates_ComposeReadableSentences()
    {
        foreach (var template in AutomationTemplates.All)
        {
            var rule = template.Build();
            var sentence = RuleSentence.Compose(rule.Trigger, rule.Actions);
            Assert.StartsWith("When", sentence);
            Assert.EndsWith(".", sentence);
        }
    }
}
