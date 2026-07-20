using Soundstage.Core.Automation;
using Soundstage.Core.Effects;

namespace Soundstage.App;

/// <summary>Segoe MDL2 Assets glyphs for triggers, actions, and effects in the automations UI.</summary>
public static class AutomationGlyphs
{
    public const string Schedule = "";     // Clock
    public const string AudioApp = "";     // Refresh? → use AllApps
    public const string Channels = "";     // Multimedia / surround
    public const string Device = "";       // Devices

    public const string Preset = "";       // Equalizer
    public const string EffectToggle = ""; // LightningBolt
    public const string Intensity = "";    // Equalizer

    public static string ForTrigger(AutomationTrigger trigger) => trigger switch
    {
        ScheduleTrigger => "",     // Clock
        AudioAppTrigger => "",     // AllApps
        ChannelCountTrigger => "", // Speakers/surround
        DeviceTrigger => "",       // Devices
        _ => "",
    };

    public static string ForEffect(EffectKind effect) => effect switch
    {
        EffectKind.NightMode => "",    // QuietHours (moon)
        EffectKind.Loudness => "",     // Volume
        EffectKind.StereoWidth => "",  // Speakers
        EffectKind.Ambience => "",     // Streaming/waves
        _ => "",
    };
}
