using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundstage.App.Services;
using Soundstage.Core.Automation;

namespace Soundstage.App.ViewModels;

/// <summary>A rule row: toggle, description, live matched/winner state, edit affordances.</summary>
public partial class RuleViewModel : ObservableObject
{
    private readonly AutomationsViewModel _owner;

    public RuleViewModel(AutomationRule rule, AutomationsViewModel owner)
    {
        Rule = rule;
        _owner = owner;
        _enabled = rule.Enabled;
    }

    public AutomationRule Rule { get; }

    public string Name => Rule.Name;

    /// <summary>Glyph for the trigger kind — shown in the accent chip on the card.</summary>
    public string TriggerGlyph => AutomationGlyphs.ForTrigger(Rule.Trigger);

    /// <summary>The whole rule as one plain-English sentence.</summary>
    public string Sentence => RuleSentence.Compose(Rule.Trigger, Rule.Actions, _owner.PresetName);

    public bool IsPrebuilt => Rule.IsPrebuilt;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _isMatched;

    [ObservableProperty]
    private bool _isWinner;

    [ObservableProperty]
    private string _liveReason = "";

    partial void OnEnabledChanged(bool value)
    {
        Rule.Enabled = value;
        _owner.RuleMutated();
    }

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TriggerGlyph));
        OnPropertyChanged(nameof(Sentence));
    }
}

/// <summary>A tap-to-add template card.</summary>
public partial class TemplateViewModel(AutomationTemplate template) : ObservableObject
{
    public AutomationTemplate Template { get; } = template;

    public string Title => Template.Title;

    public string Description => Template.Description;

    public string Glyph => Template.Glyph;
}

/// <summary>
/// The automations tab: master switch, ordered rule list (order matters — last match
/// wins), and the live "why is this active?" trace.
/// </summary>
public partial class AutomationsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _syncing;

    [ObservableProperty]
    private ObservableCollection<RuleViewModel> _rules = [];

    [ObservableProperty]
    private bool _masterEnabled = true;

    [ObservableProperty]
    private string _decisionSummary = "No evaluation yet.";

    [ObservableProperty]
    private bool _hasRules;

    public ObservableCollection<TemplateViewModel> Templates { get; } =
        new(AutomationTemplates.All.Select(t => new TemplateViewModel(t)));

    public bool ApoAvailable => _services.ApoAvailable;

    /// <summary>Humanizes a preset id to its display name for rule sentences.</summary>
    public Func<string, string> PresetName => id => _services.Presets.Get(id)?.Name ?? id;

    public AutomationsViewModel(AppServices services)
    {
        _services = services;
        if (services.Controller is { } controller)
        {
            controller.Changed += () => UiDispatch.Post(SyncMaster);
        }

        if (services.Coordinator is { } coordinator)
        {
            coordinator.DecisionMade += decision => UiDispatch.Post(() => OnDecision(decision));
        }

        Reload();
        SyncMaster();
    }

    public void Reload()
    {
        var rules = _services.Controller?.ActiveProfile?.Rules ?? [];
        Rules = new ObservableCollection<RuleViewModel>(rules.Select(r => new RuleViewModel(r, this)));
        HasRules = Rules.Count > 0;
    }

    [RelayCommand]
    private void AddTemplate(TemplateViewModel? template)
    {
        if (template is null || _services.Controller?.ActiveProfile is not { } profile)
        {
            return;
        }

        profile.Rules.Add(template.Template.Build());
        Reload();
        RuleMutated();
    }

    private void SyncMaster()
    {
        _syncing = true;
        MasterEnabled = _services.Controller?.State.AutomationsEnabled ?? true;
        _syncing = false;

        // The active profile may have changed (device switch) — rebind if the lists differ.
        var current = _services.Controller?.ActiveProfile?.Rules;
        if (current is not null && (Rules.Count != current.Count || !Rules.Select(r => r.Rule).SequenceEqual(current)))
        {
            Reload();
        }
    }

    partial void OnMasterEnabledChanged(bool value)
    {
        if (!_syncing)
        {
            _services.Controller?.SetAutomationsEnabled(value);
        }
    }

    internal void RuleMutated()
    {
        _services.Controller?.SaveState();
        _services.Coordinator?.Reevaluate();
    }

    [RelayCommand]
    private void AddRule()
    {
        var editor = new Views.RuleEditorWindow(_services, existing: null);
        if (editor.ShowDialog() == true && editor.Result is { } rule)
        {
            _services.Controller?.ActiveProfile?.Rules.Add(rule);
            Reload();
            RuleMutated();
        }
    }

    [RelayCommand]
    private void EditRule(RuleViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var editor = new Views.RuleEditorWindow(_services, row.Rule);
        if (editor.ShowDialog() == true)
        {
            row.RefreshTexts();
            RuleMutated();
        }
    }

    [RelayCommand]
    private void DeleteRule(RuleViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _services.Controller?.ActiveProfile?.Rules.Remove(row.Rule);
        Reload();
        RuleMutated();
    }

    [RelayCommand]
    private void MoveUp(RuleViewModel? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(RuleViewModel? row) => Move(row, +1);

    private void Move(RuleViewModel? row, int delta)
    {
        var rules = _services.Controller?.ActiveProfile?.Rules;
        if (row is null || rules is null)
        {
            return;
        }

        var index = rules.IndexOf(row.Rule);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= rules.Count)
        {
            return;
        }

        (rules[index], rules[target]) = (rules[target], rules[index]);
        Reload();
        RuleMutated();
    }

    private void OnDecision(AutomationDecision decision)
    {
        foreach (var row in Rules)
        {
            var trace = decision.Evaluation.Traces.FirstOrDefault(t => ReferenceEquals(t.Rule, row.Rule));
            row.IsMatched = trace?.Matched ?? false;
            row.IsWinner = trace?.Winner ?? false;
            row.LiveReason = trace?.Reason ?? "";
        }

        if (!MasterEnabled)
        {
            DecisionSummary = "Automations are paused — nothing will fire until you resume them.";
        }
        else if (decision.Evaluation.WinningRule is { } winner)
        {
            var applied = decision.AppliedChanges.Count > 0
                ? $" — applied: {string.Join(", ", decision.AppliedChanges)}"
                : " — nothing needed changing";
            DecisionSummary = $"Active rule: “{winner.Name}”{applied}.";
        }
        else
        {
            DecisionSummary = "No rule matches right now.";
        }
    }
}
