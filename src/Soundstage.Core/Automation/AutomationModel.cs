using System.Text.Json.Serialization;
using Soundstage.Core.Effects;

namespace Soundstage.Core.Automation;

/// <summary>Spatial-audio (Dolby Atmos for Home Theater etc.) engagement state — best-effort.</summary>
public enum SpatialAudioState
{
    Unknown,
    Off,
    On,
}

/// <summary>
/// A snapshot of everything triggers can react to. Built by the coordinator from live
/// Windows sources; built by hand in tests and simulations.
/// </summary>
public sealed record AudioEnvironmentSnapshot(
    DateTimeOffset LocalTime,
    string? DeviceId,
    string? DeviceName,
    int Channels,
    int SampleRateHz,
    int BitDepth,
    SpatialAudioState Spatial,
    IReadOnlyList<string> ActiveAudioProcesses,
    IReadOnlyList<string>? NowPlaying = null,
    int SourceChannels = 0)
{
    /// <summary>
    /// Friendly "what's playing" labels for display — browser processes resolved to the
    /// streaming service in the tab title (e.g. "YouTube") where possible. Automation matching
    /// still uses the raw <see cref="ActiveAudioProcesses"/>; this is display-only. Empty when
    /// the source didn't enrich it (tests, non-Windows), in which case the UI falls back to
    /// prettifying the process names.
    /// </summary>
    public IReadOnlyList<string> NowPlaying { get; init; } = NowPlaying ?? [];

    /// <summary>
    /// Best-effort channel count of the audio actually coming IN from the loudest active app
    /// (2 = stereo source, 6 = 5.1, 8 = 7.1), from per-session metering. This is what lets the
    /// UI say "2.0 source → upmixed to 7.1". 0 when unknown (nothing playing, or metering
    /// unavailable). Distinct from <see cref="Channels"/>, which is the device's OUTPUT layout.
    /// </summary>
    public int SourceChannels { get; init; } = SourceChannels;

    public static AudioEnvironmentSnapshot Empty(DateTimeOffset localTime) =>
        new(localTime, null, null, 2, 48000, 0, SpatialAudioState.Unknown, []);
}

// ---------------------------------------------------------------------------
// Triggers
// ---------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ScheduleTrigger), "schedule")]
[JsonDerivedType(typeof(AudioAppTrigger), "audioApp")]
[JsonDerivedType(typeof(ChannelCountTrigger), "channelCount")]
[JsonDerivedType(typeof(DeviceTrigger), "device")]
public abstract record AutomationTrigger
{
    /// <summary>Whether the trigger currently holds, plus a human-readable reason for the trace.</summary>
    public abstract (bool, string) Evaluate(AudioEnvironmentSnapshot snapshot);

    /// <summary>Short human description for the rule list ("Every day 22:00–06:00").</summary>
    public abstract string Describe();
}

/// <summary>
/// Active inside a local-time window on selected days. Windows may wrap past midnight
/// (22:00–06:00); the day check applies to the window's *start* day, so a Friday
/// 22:00–06:00 window is still active at 02:00 Saturday.
/// </summary>
public sealed record ScheduleTrigger(
    IReadOnlyList<DayOfWeek> Days,
    TimeOnly Start,
    TimeOnly End) : AutomationTrigger
{
    public static readonly IReadOnlyList<DayOfWeek> EveryDay =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    public override (bool, string) Evaluate(AudioEnvironmentSnapshot snapshot)
    {
        var now = TimeOnly.FromTimeSpan(snapshot.LocalTime.TimeOfDay);
        var today = snapshot.LocalTime.DayOfWeek;

        bool inWindow;
        bool dayOk;
        if (Start <= End)
        {
            inWindow = now >= Start && now < End;
            dayOk = Days.Contains(today);
        }
        else
        {
            // Overnight wrap: the window belongs to its start day.
            var inEvening = now >= Start;
            var inMorning = now < End;
            inWindow = inEvening || inMorning;
            var windowStartDay = inMorning && !inEvening ? Yesterday(today) : today;
            dayOk = Days.Contains(windowStartDay);
        }

        var matches = inWindow && dayOk;
        return (matches, matches
            ? $"time {now:HH\\:mm} is inside {Start:HH\\:mm}–{End:HH\\:mm}"
            : $"time {now:HH\\:mm} is outside {Start:HH\\:mm}–{End:HH\\:mm}" + (inWindow && !dayOk ? " for the selected days" : ""));
    }

    public override string Describe()
    {
        var days = Days.Count == 7 ? "Every day" : string.Join("/", Days.Select(d => d.ToString()[..3]));
        return $"{days} {Start:HH\\:mm}–{End:HH\\:mm}";
    }

    private static DayOfWeek Yesterday(DayOfWeek day) => (DayOfWeek)(((int)day + 6) % 7);
}

/// <summary>Matches when any listed process (case-insensitive substring) is actively rendering audio.</summary>
public sealed record AudioAppTrigger(IReadOnlyList<string> ProcessPatterns) : AutomationTrigger
{
    public override (bool, string) Evaluate(AudioEnvironmentSnapshot snapshot)
    {
        foreach (var pattern in ProcessPatterns)
        {
            var match = snapshot.ActiveAudioProcesses
                .FirstOrDefault(p => p.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return (true, $"{match} is playing audio");
            }
        }

        return (false, ProcessPatterns.Count == 1
            ? $"{ProcessPatterns[0]} is not playing audio"
            : "none of the listed apps are playing audio");
    }

    public override string Describe() => $"While {string.Join(" or ", ProcessPatterns)} plays audio";
}

public enum ChannelCondition
{
    Stereo,
    Multichannel,
    Exactly,
}

/// <summary>Matches on the output's current channel layout.</summary>
public sealed record ChannelCountTrigger(ChannelCondition Condition, int? Count = null) : AutomationTrigger
{
    public override (bool, string) Evaluate(AudioEnvironmentSnapshot snapshot)
    {
        var matches = Condition switch
        {
            ChannelCondition.Stereo => snapshot.Channels <= 2,
            ChannelCondition.Multichannel => snapshot.Channels > 2,
            ChannelCondition.Exactly => snapshot.Channels == (Count ?? -1),
            _ => false,
        };
        return (matches, $"output is {Configio.ChainCompiler.FormatChannels(snapshot.Channels)}");
    }

    public override string Describe() => Condition switch
    {
        ChannelCondition.Stereo => "When output is stereo",
        ChannelCondition.Multichannel => "When output is multichannel",
        _ => $"When output has {Count} channels",
    };
}

/// <summary>Matches when the default output device's id or name contains the pattern.</summary>
public sealed record DeviceTrigger(string DeviceIdOrNamePattern) : AutomationTrigger
{
    public override (bool, string) Evaluate(AudioEnvironmentSnapshot snapshot)
    {
        var matches = (snapshot.DeviceId?.Contains(DeviceIdOrNamePattern, StringComparison.OrdinalIgnoreCase) ?? false)
                      || (snapshot.DeviceName?.Contains(DeviceIdOrNamePattern, StringComparison.OrdinalIgnoreCase) ?? false);
        return (matches, matches
            ? $"device “{snapshot.DeviceName}” matches “{DeviceIdOrNamePattern}”"
            : $"device “{snapshot.DeviceName ?? "none"}” does not match “{DeviceIdOrNamePattern}”");
    }

    public override string Describe() => $"When output device matches “{DeviceIdOrNamePattern}”";
}

// ---------------------------------------------------------------------------
// Actions
// ---------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SwitchPresetAction), "switchPreset")]
[JsonDerivedType(typeof(SetEffectEnabledAction), "setEffectEnabled")]
[JsonDerivedType(typeof(SetEffectIntensityAction), "setEffectIntensity")]
public abstract record AutomationAction
{
    /// <summary>A key identifying what this action targets; later rules override earlier ones per target.</summary>
    public abstract string TargetKey { get; }

    public abstract string Describe();
}

public sealed record SwitchPresetAction(string PresetId) : AutomationAction
{
    public override string TargetKey => "preset";

    public override string Describe() => $"Switch preset to “{PresetId}”";
}

public sealed record SetEffectEnabledAction(EffectKind Effect, bool Enabled) : AutomationAction
{
    public override string TargetKey => $"effect:{Effect}:enabled";

    public override string Describe() => $"{(Enabled ? "Enable" : "Disable")} {EffectName(Effect)}";

    internal static string EffectName(EffectKind kind) => kind switch
    {
        EffectKind.NightMode => "night mode",
        EffectKind.Loudness => "loudness compensation",
        EffectKind.StereoWidth => "stereo width",
        EffectKind.Ambience => "ambience",
        _ => kind.ToString(),
    };
}

public sealed record SetEffectIntensityAction(EffectKind Effect, int Intensity) : AutomationAction
{
    public override string TargetKey => $"effect:{Effect}:intensity";

    public override string Describe() => $"Set {SetEffectEnabledAction.EffectName(Effect)} intensity to {Intensity}%";
}

// ---------------------------------------------------------------------------
// Rules
// ---------------------------------------------------------------------------

public sealed class AutomationRule
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    public required AutomationTrigger Trigger { get; set; }

    public List<AutomationAction> Actions { get; set; } = [];

    /// <summary>True for the rules Soundstage ships (they start disabled).</summary>
    public bool IsPrebuilt { get; set; }
}
