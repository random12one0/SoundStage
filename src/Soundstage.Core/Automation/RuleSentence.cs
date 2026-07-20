using Soundstage.Core.Effects;

namespace Soundstage.Core.Automation;

/// <summary>
/// Composes a rule into one readable "When … , then …" sentence for the builder preview and
/// the rule cards. Preset ids are humanized via an optional resolver so "film-dialogue"
/// reads as "Film &amp; Dialogue".
/// </summary>
public static class RuleSentence
{
    public static string Compose(AutomationTrigger? trigger, IReadOnlyList<AutomationAction> actions, Func<string, string>? presetName = null)
    {
        var when = trigger is null ? "When something happens" : TriggerClause(trigger);
        if (actions.Count == 0)
        {
            return $"{when}, do nothing yet.";
        }

        var parts = actions.Select(a => ActionClause(a, presetName)).ToList();
        return $"{when}, {JoinNaturally(parts)}.";
    }

    public static string TriggerClause(AutomationTrigger trigger) => trigger switch
    {
        ScheduleTrigger s => $"When it's {DescribeSchedule(s)}",
        AudioAppTrigger a => $"When {JoinNaturally(a.ProcessPatterns.Select(Humanize).ToList(), "or")} is playing",
        ChannelCountTrigger c => c.Condition switch
        {
            ChannelCondition.Stereo => "When audio is stereo",
            ChannelCondition.Multichannel => "When audio is surround (5.1/7.1)",
            _ => $"When audio has {c.Count} channels",
        },
        DeviceTrigger d => $"When the output is a {Humanize(d.DeviceIdOrNamePattern)}",
        _ => trigger.Describe(),
    };

    public static string ActionClause(AutomationAction action, Func<string, string>? presetName = null) => action switch
    {
        SwitchPresetAction p => $"switch to {presetName?.Invoke(p.PresetId) ?? Humanize(p.PresetId)}",
        SetEffectEnabledAction e => $"turn {(e.Enabled ? "on" : "off")} {EffectName(e.Effect)}",
        SetEffectIntensityAction i => $"set {EffectName(i.Effect)} to {i.Intensity}%",
        _ => action.Describe(),
    };

    public static string EffectName(EffectKind kind) => kind switch
    {
        EffectKind.NightMode => "night mode",
        EffectKind.Loudness => "loudness",
        EffectKind.StereoWidth => "stereo width",
        EffectKind.Ambience => "ambience",
        _ => kind.ToString(),
    };

    private static string DescribeSchedule(ScheduleTrigger s)
    {
        var days = s.Days.Count == 7
            ? "every day"
            : IsWeekdays(s.Days) ? "weekdays"
            : IsWeekend(s.Days) ? "weekends"
            : string.Join("/", s.Days.Select(d => d.ToString()[..3]));
        return $"{days} between {s.Start:HH\\:mm} and {s.End:HH\\:mm}";
    }

    private static bool IsWeekdays(IReadOnlyList<DayOfWeek> days) =>
        days.Count == 5 && !days.Contains(DayOfWeek.Saturday) && !days.Contains(DayOfWeek.Sunday);

    private static bool IsWeekend(IReadOnlyList<DayOfWeek> days) =>
        days.Count == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday);

    /// <summary>"a, b and c" / "a or b"; single item unchanged.</summary>
    private static string JoinNaturally(IReadOnlyList<string> parts, string conjunction = "and")
    {
        if (parts.Count == 0)
        {
            return "";
        }

        if (parts.Count == 1)
        {
            return parts[0];
        }

        return string.Join(", ", parts.Take(parts.Count - 1)) + $" {conjunction} " + parts[^1];
    }

    /// <summary>Turn ids/process names into title case ("film-dialogue" → "Film Dialogue", "chrome" → "Chrome").</summary>
    private static string Humanize(string raw)
    {
        var friendly = raw switch
        {
            "msedge" => "Edge",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            _ => raw,
        };
        var words = friendly.Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
