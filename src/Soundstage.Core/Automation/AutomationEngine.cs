namespace Soundstage.Core.Automation;

/// <summary>Per-rule evaluation record powering the "why is this active?" panel.</summary>
public sealed record RuleTrace(AutomationRule Rule, bool Matched, string Reason, bool Winner);

/// <summary>An effective action together with the rule that contributed it — attribution
/// must be per action: two rules can match at once and each own different targets.</summary>
public sealed record ContributedAction(AutomationAction Action, AutomationRule Rule);

public sealed record EvaluationResult(
    IReadOnlyList<RuleTrace> Traces,
    IReadOnlyList<ContributedAction> EffectiveActions,
    AutomationRule? WinningRule)
{
    public static readonly EvaluationResult Empty = new([], [], null);
}

/// <summary>
/// Pure rule evaluation. Rules run top to bottom; every matching rule contributes its
/// actions, and when two matching rules touch the same target (same preset slot, same
/// effect knob) the LATER rule wins. The winning rule shown in the status readout is the
/// last matching rule overall.
/// </summary>
public static class AutomationEngine
{
    public static EvaluationResult Evaluate(IReadOnlyList<AutomationRule> rules, AudioEnvironmentSnapshot snapshot)
    {
        var traces = new List<RuleTrace>(rules.Count);
        var actionsByTarget = new Dictionary<string, ContributedAction>();
        var order = new List<string>();
        AutomationRule? lastMatch = null;

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                traces.Add(new RuleTrace(rule, Matched: false, "rule is disabled", Winner: false));
                continue;
            }

            var (matches, reason) = rule.Trigger.Evaluate(snapshot);
            traces.Add(new RuleTrace(rule, matches, reason, Winner: false));
            if (!matches)
            {
                continue;
            }

            lastMatch = rule;
            foreach (var action in rule.Actions)
            {
                if (!actionsByTarget.ContainsKey(action.TargetKey))
                {
                    order.Add(action.TargetKey);
                }

                actionsByTarget[action.TargetKey] = new ContributedAction(action, rule);
            }
        }

        if (lastMatch is not null)
        {
            for (var i = 0; i < traces.Count; i++)
            {
                if (ReferenceEquals(traces[i].Rule, lastMatch))
                {
                    traces[i] = traces[i] with { Winner = true };
                }
            }
        }

        var effective = order.Select(k => actionsByTarget[k]).ToList();
        return new EvaluationResult(traces, effective, lastMatch);
    }
}
