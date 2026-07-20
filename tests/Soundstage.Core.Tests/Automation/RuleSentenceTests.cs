using Soundstage.Core.Automation;
using Soundstage.Core.Effects;
using Xunit;

namespace Soundstage.Core.Tests.Automation;

public class RuleSentenceTests
{
    [Fact]
    public void SingleAction_ReadsNaturally()
    {
        var sentence = RuleSentence.Compose(
            new AudioAppTrigger(["spotify"]),
            [new SwitchPresetAction("music")]);
        Assert.Equal("When Spotify is playing, switch to Music.", sentence);
    }

    [Fact]
    public void MultipleActions_AreJoinedWithAnd()
    {
        var sentence = RuleSentence.Compose(
            new ScheduleTrigger(ScheduleTrigger.EveryDay, new TimeOnly(22, 0), new TimeOnly(6, 0)),
            [
                new SetEffectEnabledAction(EffectKind.NightMode, true),
                new SetEffectEnabledAction(EffectKind.Loudness, true),
            ]);
        Assert.Equal("When it's every day between 22:00 and 06:00, turn on night mode and turn on loudness.", sentence);
    }

    [Fact]
    public void ThreeActions_UseOxfordishCommas()
    {
        var sentence = RuleSentence.Compose(
            new DeviceTrigger("headphone"),
            [
                new SwitchPresetAction("flat"),
                new SetEffectEnabledAction(EffectKind.StereoWidth, false),
                new SetEffectIntensityAction(EffectKind.NightMode, 40),
            ]);
        Assert.Equal(
            "When the output is a Headphone, switch to Flat, turn off stereo width and set night mode to 40%.",
            sentence);
    }

    [Fact]
    public void PresetNameResolver_IsUsed()
    {
        var sentence = RuleSentence.Compose(
            new ChannelCountTrigger(ChannelCondition.Multichannel),
            [new SwitchPresetAction("film-dialogue")],
            presetName: id => id == "film-dialogue" ? "Film & Dialogue" : id);
        Assert.Equal("When audio is surround (5.1/7.1), switch to Film & Dialogue.", sentence);
    }

    [Fact]
    public void Weekdays_And_Weekend_AreNamed()
    {
        var weekdays = new ScheduleTrigger(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.Contains("weekdays", RuleSentence.TriggerClause(weekdays));

        var weekend = new ScheduleTrigger([DayOfWeek.Saturday, DayOfWeek.Sunday], new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.Contains("weekends", RuleSentence.TriggerClause(weekend));
    }

    [Fact]
    public void MultipleApps_JoinedWithOr()
    {
        var clause = RuleSentence.TriggerClause(new AudioAppTrigger(["chrome", "firefox"]));
        Assert.Equal("When Chrome or Firefox is playing", clause);
    }

    [Fact]
    public void NoActions_SaysNothingYet()
    {
        var sentence = RuleSentence.Compose(new AudioAppTrigger(["spotify"]), []);
        Assert.Equal("When Spotify is playing, do nothing yet.", sentence);
    }
}
