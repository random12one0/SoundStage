using Soundstage.Core.Effects;

namespace Soundstage.Core.Automation;

/// <summary>
/// The automations Soundstage ships with. All disabled by default — one click to enable.
/// Created fresh per profile so users can tweak them independently per device.
/// </summary>
public static class PrebuiltRules
{
    public static List<AutomationRule> CreateDefaultSet() =>
    [
        new AutomationRule
        {
            Id = "prebuilt-night",
            Name = "Night mode after 10pm",
            Enabled = false,
            IsPrebuilt = true,
            Trigger = new ScheduleTrigger(ScheduleTrigger.EveryDay, new TimeOnly(22, 0), new TimeOnly(6, 0)),
            Actions =
            [
                new SetEffectEnabledAction(EffectKind.NightMode, true),
                new SetEffectEnabledAction(EffectKind.Loudness, true),
            ],
        },
        new AutomationRule
        {
            Id = "prebuilt-spotify",
            Name = "Music preset when Spotify plays",
            Enabled = false,
            IsPrebuilt = true,
            Trigger = new AudioAppTrigger(["Spotify"]),
            Actions = [new SwitchPresetAction("music")],
        },
        new AutomationRule
        {
            Id = "prebuilt-browser-video",
            Name = "Film preset when a browser plays audio",
            Enabled = false,
            IsPrebuilt = true,
            Trigger = new AudioAppTrigger(["chrome", "msedge", "firefox", "brave", "opera"]),
            Actions = [new SwitchPresetAction("film-dialogue")],
        },
        new AutomationRule
        {
            Id = "prebuilt-multichannel-flat",
            Name = "Flat for multichannel content",
            Enabled = false,
            IsPrebuilt = true,
            Trigger = new ChannelCountTrigger(ChannelCondition.Multichannel),
            Actions = [new SwitchPresetAction("flat")],
        },
        new AutomationRule
        {
            Id = "prebuilt-headphones",
            Name = "Headphone profile on headphone connect",
            Enabled = false,
            IsPrebuilt = true,
            Trigger = new DeviceTrigger("headphone"),
            Actions = [new SetEffectEnabledAction(EffectKind.StereoWidth, false)],
        },
    ];
}
