using Soundstage.Core.Effects;

namespace Soundstage.Core.Automation;

/// <summary>A one-tap starting point for a rule: added enabled, then freely editable.</summary>
public sealed record AutomationTemplate(
    string Id,
    string Title,
    string Description,
    string Glyph,
    Func<AutomationRule> Build);

/// <summary>
/// Curated automation templates shown as tap-to-add cards. Each builds a fresh rule so the
/// user can tweak it afterward without affecting the template.
/// </summary>
public static class AutomationTemplates
{
    public static IReadOnlyList<AutomationTemplate> All { get; } =
    [
        new("tmpl-quiet-hours", "Quiet Hours",
            "After 10pm every night, ease off the bass and even out loud moments.",
            "", // QuietHours (moon)
            () => new AutomationRule
            {
                Id = NewId(),
                Name = "Quiet Hours",
                Enabled = true,
                Trigger = new ScheduleTrigger(ScheduleTrigger.EveryDay, new TimeOnly(22, 0), new TimeOnly(6, 0)),
                Actions =
                [
                    new SetEffectEnabledAction(EffectKind.NightMode, true),
                    new SetEffectEnabledAction(EffectKind.Loudness, true),
                ],
            }),

        new("tmpl-movie-night", "Movie Night",
            "When surround content plays, drop to Flat so movie mixes stay as intended.",
            "", // Movie
            () => new AutomationRule
            {
                Id = NewId(),
                Name = "Movie Night",
                Enabled = true,
                Trigger = new ChannelCountTrigger(ChannelCondition.Multichannel),
                Actions = [new SwitchPresetAction("flat")],
            }),

        new("tmpl-gaming", "Gaming Mode",
            "When a game is playing audio, switch to the Gaming preset for punch and clarity.",
            "", // Game
            () => new AutomationRule
            {
                Id = NewId(),
                Name = "Gaming Mode",
                Enabled = true,
                Trigger = new AudioAppTrigger(["game", "steam", "valorant", "csgo", "cs2", "javaw"]),
                Actions = [new SwitchPresetAction("gaming")],
            }),

        new("tmpl-music-spotify", "Spotify Music",
            "When Spotify plays, switch to the Music preset.",
            "", // MusicNote
            () => new AutomationRule
            {
                Id = NewId(),
                Name = "Spotify Music",
                Enabled = true,
                Trigger = new AudioAppTrigger(["spotify", "tidal", "foobar2000", "musicbee", "itunes"]),
                Actions = [new SwitchPresetAction("music")],
            }),

        new("tmpl-focus", "Podcasts & Voice",
            "When a browser or player has audio, use the Spoken Word preset for clear voices.",
            "", // Microphone
            () => new AutomationRule
            {
                Id = NewId(),
                Name = "Podcasts & Voice",
                Enabled = true,
                Trigger = new AudioAppTrigger(["chrome", "msedge", "firefox", "brave", "vlc"]),
                Actions = [new SwitchPresetAction("podcast")],
            }),

        new("tmpl-headphones", "Headphone Comfort",
            "When you plug in headphones, turn stereo width off so vocals stay centered.",
            "", // Headphone
            () => new AutomationRule
            {
                Id = NewId(),
                Name = "Headphone Comfort",
                Enabled = true,
                Trigger = new DeviceTrigger("headphone"),
                Actions = [new SetEffectEnabledAction(EffectKind.StereoWidth, false)],
            }),
    ];

    private static string NewId() => "rule-" + Guid.NewGuid().ToString("N")[..10];
}
