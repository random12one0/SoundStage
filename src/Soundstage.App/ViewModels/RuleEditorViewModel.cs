using System.Collections.ObjectModel;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSchedule))]
    [NotifyPropertyChangedFor(nameof(IsAudioApp))]
    [NotifyPropertyChangedFor(nameof(IsChannelCount))]
    [NotifyPropertyChangedFor(nameof(IsDevice))]
    private TriggerKindChoice _triggerKind = TriggerKindChoice.Schedule;

    public static IReadOnlyList<TriggerKindChoice> TriggerKinds { get; } = Enum.GetValues<TriggerKindChoice>();

    public bool IsSchedule => TriggerKind == TriggerKindChoice.Schedule;

    public bool IsAudioApp => TriggerKind == TriggerKindChoice.AudioApp;

    public bool IsChannelCount => TriggerKind == TriggerKindChoice.ChannelCount;

    public bool IsDevice => TriggerKind == TriggerKindChoice.Device;

    // Schedule
    public ObservableCollection<DayChoiceViewModel> Days { get; } =
        new(ScheduleTrigger.EveryDay.Select(d => new DayChoiceViewModel(d, selected: true)));

    [ObservableProperty]
    private string _startTimeText = "22:00";

    [ObservableProperty]
    private string _endTimeText = "06:00";

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

    public RuleEditorViewModel(IReadOnlyList<EqPreset> presets, AutomationRule? existing)
    {
        _presets = presets;
        if (existing is null)
        {
            Actions.Add(new ActionEditorViewModel(presets));
            return;
        }

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

                StartTimeText = s.Start.ToString("HH\\:mm");
                EndTimeText = s.End.ToString("HH\\:mm");
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

                if (!TimeOnly.TryParseExact(StartTimeText.Trim(), "HH:mm", out var start)
                    || !TimeOnly.TryParseExact(EndTimeText.Trim(), "HH:mm", out var end))
                {
                    ValidationError = "Times must look like 22:00.";
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
