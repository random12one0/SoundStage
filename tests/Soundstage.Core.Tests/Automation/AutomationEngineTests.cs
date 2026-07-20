using Soundstage.Core.Automation;
using Soundstage.Core.Effects;
using Xunit;

namespace Soundstage.Core.Tests.Automation;

public class AutomationEngineTests
{
    private static AudioEnvironmentSnapshot Snapshot(
        string time = "2026-07-20T14:00:00+02:00",
        int channels = 2,
        string? deviceName = "Speakers",
        params string[] apps) =>
        new(DateTimeOffset.Parse(time), "{dev-id}", deviceName, channels, 48000, 24, SpatialAudioState.Unknown, apps);

    private static AutomationRule Rule(string id, AutomationTrigger trigger, params AutomationAction[] actions) =>
        new() { Id = id, Name = id, Trigger = trigger, Actions = [.. actions] };

    // ---- Triggers ----

    [Theory]
    [InlineData("2026-07-20T22:30:00+02:00", true)]   // Monday evening
    [InlineData("2026-07-20T21:59:00+02:00", false)]  // just before
    [InlineData("2026-07-21T02:00:00+02:00", true)]   // Tuesday 2am — window started Monday
    [InlineData("2026-07-21T06:00:00+02:00", false)]  // window closed
    [InlineData("2026-07-20T14:00:00+02:00", false)]  // afternoon
    public void ScheduleTrigger_OvernightWindow_EveryDay(string time, bool expected)
    {
        var trigger = new ScheduleTrigger(ScheduleTrigger.EveryDay, new TimeOnly(22, 0), new TimeOnly(6, 0));
        var (matches, _) = trigger.Evaluate(Snapshot(time));
        Assert.Equal(expected, matches);
    }

    [Fact]
    public void ScheduleTrigger_OvernightWindow_BelongsToStartDay()
    {
        // Friday-only 22:00–06:00: active Saturday 2am, NOT active Thursday 23:00 or Saturday 23:00.
        var trigger = new ScheduleTrigger([DayOfWeek.Friday], new TimeOnly(22, 0), new TimeOnly(6, 0));

        Assert.True(trigger.Evaluate(Snapshot("2026-07-24T23:00:00+02:00")).Item1);  // Fri evening
        Assert.True(trigger.Evaluate(Snapshot("2026-07-25T02:00:00+02:00")).Item1);  // Sat 2am (Fri window)
        Assert.False(trigger.Evaluate(Snapshot("2026-07-23T23:00:00+02:00")).Item1); // Thu evening
        Assert.False(trigger.Evaluate(Snapshot("2026-07-25T23:00:00+02:00")).Item1); // Sat evening
    }

    [Fact]
    public void ScheduleTrigger_DaytimeWindow()
    {
        var trigger = new ScheduleTrigger([DayOfWeek.Monday], new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.True(trigger.Evaluate(Snapshot("2026-07-20T14:00:00+02:00")).Item1);
        Assert.False(trigger.Evaluate(Snapshot("2026-07-20T18:00:00+02:00")).Item1);
        Assert.False(trigger.Evaluate(Snapshot("2026-07-21T14:00:00+02:00")).Item1); // Tuesday
    }

    [Fact]
    public void AudioAppTrigger_MatchesSubstring_CaseInsensitive()
    {
        var trigger = new AudioAppTrigger(["spotify"]);
        Assert.True(trigger.Evaluate(Snapshot(apps: "Spotify")).Item1);
        Assert.True(trigger.Evaluate(Snapshot(apps: ["chrome", "Spotify"])).Item1);
        Assert.False(trigger.Evaluate(Snapshot(apps: "chrome")).Item1);
        Assert.False(trigger.Evaluate(Snapshot()).Item1);
    }

    [Fact]
    public void ChannelCountTrigger_Conditions()
    {
        Assert.True(new ChannelCountTrigger(ChannelCondition.Stereo).Evaluate(Snapshot(channels: 2)).Item1);
        Assert.False(new ChannelCountTrigger(ChannelCondition.Stereo).Evaluate(Snapshot(channels: 6)).Item1);
        Assert.True(new ChannelCountTrigger(ChannelCondition.Multichannel).Evaluate(Snapshot(channels: 6)).Item1);
        Assert.True(new ChannelCountTrigger(ChannelCondition.Exactly, 8).Evaluate(Snapshot(channels: 8)).Item1);
        Assert.False(new ChannelCountTrigger(ChannelCondition.Exactly, 8).Evaluate(Snapshot(channels: 6)).Item1);
    }

    [Fact]
    public void DeviceTrigger_MatchesIdOrName()
    {
        var trigger = new DeviceTrigger("headphone");
        Assert.True(trigger.Evaluate(Snapshot(deviceName: "USB Headphones")).Item1);
        Assert.False(trigger.Evaluate(Snapshot(deviceName: "Onkyo Receiver")).Item1);
    }

    // ---- Engine semantics ----

    [Fact]
    public void LastMatchingRule_WinsPerTarget()
    {
        var rules = new List<AutomationRule>
        {
            Rule("first", new ChannelCountTrigger(ChannelCondition.Stereo), new SwitchPresetAction("music")),
            Rule("second", new AudioAppTrigger(["chrome"]), new SwitchPresetAction("film-dialogue")),
        };

        var result = AutomationEngine.Evaluate(rules, Snapshot(apps: "chrome"));

        var contributed = Assert.Single(result.EffectiveActions);
        var action = Assert.IsType<SwitchPresetAction>(contributed.Action);
        Assert.Equal("film-dialogue", action.PresetId);
        Assert.Equal("second", contributed.Rule.Id);
        Assert.Equal("second", result.WinningRule!.Id);
    }

    [Fact]
    public void NonConflictingActions_AllApply()
    {
        var rules = new List<AutomationRule>
        {
            Rule("preset", new ChannelCountTrigger(ChannelCondition.Stereo), new SwitchPresetAction("music")),
            Rule("night", new ScheduleTrigger(ScheduleTrigger.EveryDay, new TimeOnly(22, 0), new TimeOnly(6, 0)),
                new SetEffectEnabledAction(EffectKind.NightMode, true)),
        };

        var result = AutomationEngine.Evaluate(rules, Snapshot("2026-07-20T23:00:00+02:00", apps: "foo"));

        Assert.Equal(2, result.EffectiveActions.Count);
        // Each action is attributed to the rule that contributed it, not the overall winner.
        Assert.Equal("preset", result.EffectiveActions[0].Rule.Id);
        Assert.Equal("night", result.EffectiveActions[1].Rule.Id);
        Assert.Equal("night", result.WinningRule!.Id);
    }

    [Fact]
    public void DisabledRules_AreSkipped_ButTraced()
    {
        var rule = Rule("r", new ChannelCountTrigger(ChannelCondition.Stereo), new SwitchPresetAction("music"));
        rule.Enabled = false;

        var result = AutomationEngine.Evaluate([rule], Snapshot());

        Assert.Empty(result.EffectiveActions);
        Assert.Null(result.WinningRule);
        var trace = Assert.Single(result.Traces);
        Assert.False(trace.Matched);
        Assert.Contains("disabled", trace.Reason);
    }

    [Fact]
    public void Traces_MarkTheWinner_AndExplainEveryRule()
    {
        var rules = new List<AutomationRule>
        {
            Rule("a", new AudioAppTrigger(["spotify"]), new SwitchPresetAction("music")),
            Rule("b", new ChannelCountTrigger(ChannelCondition.Stereo), new SwitchPresetAction("flat")),
        };

        var result = AutomationEngine.Evaluate(rules, Snapshot(apps: "Spotify"));

        Assert.Equal(2, result.Traces.Count);
        Assert.True(result.Traces[0].Matched);
        Assert.False(result.Traces[0].Winner);
        Assert.True(result.Traces[1].Winner);
        Assert.All(result.Traces, t => Assert.False(string.IsNullOrWhiteSpace(t.Reason)));
    }

    [Fact]
    public void NoRules_YieldsEmptyResult()
    {
        var result = AutomationEngine.Evaluate([], Snapshot());
        Assert.Empty(result.Traces);
        Assert.Null(result.WinningRule);
    }
}
