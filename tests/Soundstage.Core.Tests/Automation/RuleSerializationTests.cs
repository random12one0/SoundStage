using System.Text.Json;
using Soundstage.Core;
using Soundstage.Core.Automation;
using Soundstage.Core.Effects;
using Xunit;

namespace Soundstage.Core.Tests.Automation;

public class RuleSerializationTests
{
    [Fact]
    public void AllTriggerAndActionTypes_RoundTripThroughJson()
    {
        var rules = new List<AutomationRule>
        {
            new()
            {
                Id = "schedule",
                Name = "Schedule",
                Trigger = new ScheduleTrigger([DayOfWeek.Friday, DayOfWeek.Saturday], new TimeOnly(22, 0), new TimeOnly(6, 0)),
                Actions = [new SetEffectEnabledAction(EffectKind.NightMode, true), new SetEffectIntensityAction(EffectKind.NightMode, 80)],
            },
            new()
            {
                Id = "app",
                Name = "App",
                Enabled = false,
                Trigger = new AudioAppTrigger(["Spotify", "Tidal"]),
                Actions = [new SwitchPresetAction("music")],
            },
            new()
            {
                Id = "channels",
                Name = "Channels",
                Trigger = new ChannelCountTrigger(ChannelCondition.Exactly, 8),
                Actions = [],
            },
            new()
            {
                Id = "device",
                Name = "Device",
                Trigger = new DeviceTrigger("headphone"),
                Actions = [new SetEffectEnabledAction(EffectKind.StereoWidth, false)],
            },
        };

        var json = JsonSerializer.Serialize(rules, JsonDefaults.Readable);
        var loaded = JsonSerializer.Deserialize<List<AutomationRule>>(json, JsonDefaults.Readable)!;

        Assert.Equal(4, loaded.Count);

        var schedule = Assert.IsType<ScheduleTrigger>(loaded[0].Trigger);
        Assert.Equal(new TimeOnly(22, 0), schedule.Start);
        Assert.Equal(2, schedule.Days.Count);
        Assert.Equal(2, loaded[0].Actions.Count);
        Assert.IsType<SetEffectIntensityAction>(loaded[0].Actions[1]);

        var app = Assert.IsType<AudioAppTrigger>(loaded[1].Trigger);
        Assert.Equal(new[] { "Spotify", "Tidal" }, app.ProcessPatterns);
        Assert.False(loaded[1].Enabled);

        var channels = Assert.IsType<ChannelCountTrigger>(loaded[2].Trigger);
        Assert.Equal(ChannelCondition.Exactly, channels.Condition);
        Assert.Equal(8, channels.Count);

        Assert.IsType<DeviceTrigger>(loaded[3].Trigger);
        var widthOff = Assert.IsType<SetEffectEnabledAction>(loaded[3].Actions[0]);
        Assert.False(widthOff.Enabled);
    }

    [Fact]
    public void TriggerDescriptions_AreHumanReadable()
    {
        Assert.Equal("Every day 22:00–06:00", new ScheduleTrigger(ScheduleTrigger.EveryDay, new TimeOnly(22, 0), new TimeOnly(6, 0)).Describe());
        Assert.Equal("While Spotify plays audio", new AudioAppTrigger(["Spotify"]).Describe());
        Assert.Equal("When output is multichannel", new ChannelCountTrigger(ChannelCondition.Multichannel).Describe());
        Assert.Equal("When output device matches “headphone”", new DeviceTrigger("headphone").Describe());
    }
}
