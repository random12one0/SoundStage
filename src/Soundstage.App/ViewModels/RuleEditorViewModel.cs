using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundstage.Core.Automation;
using Soundstage.Core.Effects;
using Soundstage.Core.Presets;

namespace Soundstage.App.ViewModels;

public enum TriggerKindChoice
{
    Schedule,
    AudioApp,
    ChannelCount,
    Device,
}

public partial class DayChoiceViewModel(DayOfWeek day, bool selected) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Label { get; } = day.ToString()[..3];

    [ObservableProperty]
    private bool _selected = selected;
}

/// <summary>One action row in the editor.</summary>
public partial class ActionEditorViewModel : ObservableObject
{
    public ActionEditorViewModel(IReadOnlyList<EqPreset> presets)
    {
        Presets = presets;
        _selectedPreset = presets.FirstOrDefault();
    }

    public static IReadOnlyList<string> ActionKinds { get; } = ["Switch preset", "Turn effect on/off", "Set effect intensity"];

    public static IReadOnlyList<EffectKind> Effects { get; } = Enum.GetValues<EffectKind>();

    public IReadOnlyList<EqPreset> Presets { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresetKind))]
    [NotifyPropertyChangedFor(nameof(IsEnabledKind))]
    [NotifyPropertyChangedFor(nameof(IsIntensityKind))]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    private string _kind = ActionKinds[0];

    [ObservableProperty]
    private EqPreset? _selectedPreset;

    [ObservableProperty]
    private EffectKind _effect = EffectKind.NightMode;

    [ObservableProperty]
    private bool _effectOn = true;

    [ObservableProperty]
    private double _intensity = 50;

    public bool IsPresetKind => Kind == ActionKinds[0];

    public bool IsEnabledKind => Kind == ActionKinds[1];

    public bool IsIntensityKind => Kind == ActionKinds[2];

    /// <summary>Segoe MDL2 glyph for this action kind (shown in the card chip).</summary>
    public string Glyph => IsPresetKind ? "" : IsIntensityKind ? "" : "";

    public AutomationAction? Build()
    {
        if (IsPresetKind)
        {
            return SelectedPreset is null ? null : new SwitchPresetAction(SelectedPreset.Id);
        }

        if (IsEnabledKind)
        {
            return new SetEffectEnabledAction(Effect, EffectOn);
        }

        return new SetEffectIntensityAction(Effect, (int)Math.Round(Intensity));
    }

    public void LoadFrom(AutomationAction action)
    {
        switch (action)
        {
            case SwitchPresetAction sp:
                Kind = ActionKinds[0];
                SelectedPreset = Presets.FirstOrDefault(p => p.Id == sp.PresetId) ?? SelectedPreset;
                break;
            case SetEffectEnabledAction se:
                Kind = ActionKinds[1];
                Effect = se.Effect;
                EffectOn = se.Enabled;
                break;
            case SetEffectIntensityAction si:
                Kind = ActionKinds[2];
                Effect = si.Effect;
                Intensity = si.Intensity;
                break;
        }
    }
}

/// <summary>
/// Builds/edits one automation rule: a trigger (four kinds, each with a tiny form) and a
/// list of actions. Designed to be understandable on first sight — no nesting, no
/// conditions language.
/// </summary>
public partial class RuleEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<EqPreset> _presets;

    [ObservableProperty]
    private string _ruleName = "";

    private readonly Func<string, string>? _presetName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSchedule))]
    [NotifyPropertyChangedFor(nameof(IsAudioApp))]
    [NotifyPropertyChangedFor(nameof(IsChannelCount))]
    [NotifyPropertyChangedFor(nameof(IsDevice))]
    private TriggerKindChoice _triggerKind = TriggerKindChoice.Schedule;

    /// <summary>Live plain-English preview of the rule as it's being built.</summary>
    [ObservableProperty]
    private string _liveSentence = "";

    public static IReadOnlyList<TriggerKindChoice> TriggerKinds { get; } = Enum.GetValues<TriggerKindChoice>();

    public bool IsSchedule => TriggerKind == TriggerKindChoice.Schedule;

    public bool IsAudioApp => TriggerKind == TriggerKindChoice.AudioApp;

    public bool IsChannelCount => TriggerKind == TriggerKindChoice.ChannelCount;

    public bool IsDevice => TriggerKind == TriggerKindChoice.Device;

    // Schedule
    public ObservableCollection<DayChoiceViewModel> Days { get; } =
        new(ScheduleTrigger.EveryDay.Select(d => new DayChoiceViewModel(d, selected: true)));

    [ObservableProperty]
    private string _startTimeText = "10:00 PM";

    [ObservableProperty]
    private string _endTimeText = "6:00 AM";

    /// <summary>Accepted time formats — 12-hour (default) and 24-hour, so both "10:00 PM" and "22:00" work.</summary>
    private static readonly string[] TimeFormats = ["h:mm tt", "hh:mm tt", "h:mmtt", "htt", "H:mm", "HH:mm"];

    internal static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
        || TimeOnly.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out time);

    /// <summary>12-hour display, e.g. "10:00 PM".</summary>
    internal static string FormatTime(TimeOnly time) => time.ToString("h:mm tt", CultureInfo.InvariantCulture);

    // Audio app
    [ObservableProperty]
    private string _processesText = "Spotify";

    // Channel count
    public static IReadOnlyList<ChannelCondition> ChannelConditions { get; } = Enum.GetValues<ChannelCondition>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExactChannels))]
    private ChannelCondition _channelCondition = ChannelCondition.Multichannel;

    [ObservableProperty]
    private double _exactChannels = 6;

    public bool IsExactChannels => ChannelCondition == ChannelCondition.Exactly;

    // Device
    [ObservableProperty]
    private string _devicePatternText = "";

    // Actions
    public ObservableCollection<ActionEditorViewModel> Actions { get; } = [];

    [ObservableProperty]
    private string _validationError = "";

    public RuleEditorViewModel(IReadOnlyList<EqPreset> presets, AutomationRule? existing, Func<string, string>? presetName = null)
    {
        _presets = presets;
        _presetName = presetName;

        if (existing is null)
        {
            Actions.Add(new ActionEditorViewModel(presets));
        }
        else
        {
            RuleName = existing.Name;
            LoadTrigger(existing.Trigger);
            foreach (var action in existing.Actions)
            {
                var vm = new ActionEditorViewModel(presets);
                vm.LoadFrom(action);
                Actions.Add(vm);
            }

            if (Actions.Count == 0)
            {
                Actions.Add(new ActionEditorViewModel(presets));
            }
        }

        // Keep the live sentence in sync with every editable field.
        foreach (var day in Days)
        {
            day.PropertyChanged += (_, _) => RecomputeSentence();
        }

        Actions.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.OfType<ActionEditorViewModel>() ?? [])
            {
                item.PropertyChanged += (_, _) => RecomputeSentence();
            }

            RecomputeSentence();
        };

        foreach (var action in Actions)
        {
            action.PropertyChanged += (_, _) => RecomputeSentence();
        }

        RecomputeSentence();
    }

    private void RecomputeSentence()
    {
        var (trigger, actions) = BuildPreview();
        LiveSentence = RuleSentence.Compose(trigger, actions, _presetName);
    }

    /// <summary>Best-effort (never-throwing) trigger + actions for the live preview.</summary>
    private (AutomationTrigger Trigger, IReadOnlyList<AutomationAction> Actions) BuildPreview()
    {
        AutomationTrigger trigger = TriggerKind switch
        {
            TriggerKindChoice.Schedule => new ScheduleTrigger(
                Days.Where(d => d.Selected).Select(d => d.Day).DefaultIfEmpty(DayOfWeek.Monday).ToList(),
                TryParseTime(StartTimeText, out var s) ? s : new TimeOnly(22, 0),
                TryParseTime(EndTimeText, out var e) ? e : new TimeOnly(6, 0)),
            TriggerKindChoice.AudioApp => new AudioAppTrigger(
                ProcessesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .DefaultIfEmpty("an app").ToList()),
            TriggerKindChoice.ChannelCount => new ChannelCountTrigger(
                ChannelCondition, ChannelCondition == ChannelCondition.Exactly ? (int)Math.Round(ExactChannels) : null),
            _ => new DeviceTrigger(string.IsNullOrWhiteSpace(DevicePatternText) ? "device" : DevicePatternText.Trim()),
        };

        var actions = Actions.Select(a => a.Build()).OfType<AutomationAction>().ToList();
        return (trigger, actions);
    }

    private void LoadTrigger(AutomationTrigger trigger)
    {
        switch (trigger)
        {
            case ScheduleTrigger s:
                TriggerKind = TriggerKindChoice.Schedule;
                foreach (var day in Days)
                {
                    day.Selected = s.Days.Contains(day.Day);
                }

                StartTimeText = FormatTime(s.Start);
                EndTimeText = FormatTime(s.End);
                break;
            case AudioAppTrigger a:
                TriggerKind = TriggerKindChoice.AudioApp;
                ProcessesText = string.Join(", ", a.ProcessPatterns);
                break;
            case ChannelCountTrigger c:
                TriggerKind = TriggerKindChoice.ChannelCount;
                ChannelCondition = c.Condition;
                ExactChannels = c.Count ?? 6;
                break;
            case DeviceTrigger d:
                TriggerKind = TriggerKindChoice.Device;
                DevicePatternText = d.DeviceIdOrNamePattern;
                break;
        }
    }

    partial void OnTriggerKindChanged(TriggerKindChoice value) => RecomputeSentence();

    partial void OnStartTimeTextChanged(string value) => RecomputeSentence();

    partial void OnEndTimeTextChanged(string value) => RecomputeSentence();

    partial void OnProcessesTextChanged(string value) => RecomputeSentence();

    partial void OnChannelConditionChanged(ChannelCondition value) => RecomputeSentence();

    partial void OnExactChannelsChanged(double value) => RecomputeSentence();

    partial void OnDevicePatternTextChanged(string value) => RecomputeSentence();

    /// <summary>Suggestion chips shown under the app-trigger field.</summary>
    public static IReadOnlyList<string> AppSuggestions { get; } = ["Spotify", "Chrome", "VLC", "Discord", "Steam"];

    /// <summary>Suggestion chips shown under the device-trigger field.</summary>
    public static IReadOnlyList<string> DeviceSuggestions { get; } = ["headphone", "speaker", "receiver", "HDMI"];

    [RelayCommand]
    private void AppendApp(string? app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            return;
        }

        var existing = ProcessesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (existing.Any(e => string.Equals(e, app, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ProcessesText = existing.Length == 0 ? app : ProcessesText.TrimEnd().TrimEnd(',') + ", " + app;
    }

    [RelayCommand]
    private void UseDeviceSuggestion(string? pattern)
    {
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            DevicePatternText = pattern;
        }
    }

    [RelayCommand]
    private void AddAction() => Actions.Add(new ActionEditorViewModel(_presets));

    [RelayCommand]
    private void RemoveAction(ActionEditorViewModel? action)
    {
        if (action is not null && Actions.Count > 1)
        {
            Actions.Remove(action);
        }
    }

    /// <summary>Validates and builds the trigger + actions; null (with error text) when invalid.</summary>
    public (AutomationTrigger Trigger, List<AutomationAction> Actions)? Build()
    {
        ValidationError = "";

        AutomationTrigger trigger;
        switch (TriggerKind)
        {
            case TriggerKindChoice.Schedule:
                var days = Days.Where(d => d.Selected).Select(d => d.Day).ToList();
                if (days.Count == 0)
                {
                    ValidationError = "Pick at least one day.";
                    return null;
                }

                if (!TryParseTime(StartTimeText, out var start) || !TryParseTime(EndTimeText, out var end))
                {
                    ValidationError = "Times must look like 10:00 PM (or 22:00).";
                    return null;
                }

                trigger = new ScheduleTrigger(days, start, end);
                break;

            case TriggerKindChoice.AudioApp:
                var processes = ProcessesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (processes.Count == 0)
                {
                    ValidationError = "Name at least one app (e.g. Spotify).";
                    return null;
                }

                trigger = new AudioAppTrigger(processes);
                break;

            case TriggerKindChoice.ChannelCount:
                trigger = new ChannelCountTrigger(ChannelCondition,
                    ChannelCondition == ChannelCondition.Exactly ? (int)Math.Round(ExactChannels) : null);
                break;

            default:
                if (string.IsNullOrWhiteSpace(DevicePatternText))
                {
                    ValidationError = "Enter part of a device name (e.g. headphone).";
                    return null;
                }

                trigger = new DeviceTrigger(DevicePatternText.Trim());
                break;
        }

        var actions = new List<AutomationAction>();
        foreach (var vm in Actions)
        {
            if (vm.Build() is { } action)
            {
                actions.Add(action);
            }
        }

        if (actions.Count == 0)
        {
            ValidationError = "Add at least one action.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(RuleName))
        {
            RuleName = trigger.Describe();
        }

        return (trigger, actions);
    }
}
