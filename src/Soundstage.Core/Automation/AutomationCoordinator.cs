using Soundstage.Core.Abstractions;
using Soundstage.Core.Effects;
using Soundstage.Core.State;

namespace Soundstage.Core.Automation;

/// <summary>Source of live audio-environment snapshots (Windows implementation wraps WASAPI).</summary>
public interface IAudioEnvironmentSource
{
    /// <summary>Fired on device/format/session changes. The coordinator debounces.</summary>
    event Action? Changed;

    AudioEnvironmentSnapshot GetSnapshot();
}

/// <summary>What the coordinator decided and why — surfaced in the status readout.</summary>
public sealed record AutomationDecision(
    EvaluationResult Evaluation,
    IReadOnlyList<string> AppliedChanges,
    DateTimeOffset At);

/// <summary>
/// Bridges the pure engine to the real world: debounces environment churn, re-evaluates on
/// changes and schedule boundaries, diffs desired state against the active profile, and
/// hands concrete mutations to the controller. The master kill switch short-circuits
/// everything — rules stay defined, nothing fires.
/// </summary>
public sealed class AutomationCoordinator : IDisposable
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ScheduleTick = TimeSpan.FromSeconds(30);

    private readonly IAudioEnvironmentSource _source;
    private readonly IClock _clock;
    private readonly IDelayScheduler _scheduler;
    private readonly object _gate = new();

    private IDisposable? _debounceHandle;
    private IDisposable? _tickHandle;
    private bool _disposed;

    public AutomationCoordinator(IAudioEnvironmentSource source, IClock clock, IDelayScheduler scheduler)
    {
        _source = source;
        _clock = clock;
        _scheduler = scheduler;
        _source.Changed += OnEnvironmentChanged;
    }

    /// <summary>The controller applies mutations; kept as delegates so Core stays UI-free.</summary>
    public Func<SoundstageState>? StateProvider { get; set; }

    public Action<string, ApplyAttribution>? SwitchPreset { get; set; }

    public Action<EffectKind, bool, ApplyAttribution>? SetEffectEnabled { get; set; }

    public Action<EffectKind, int, ApplyAttribution>? SetEffectIntensity { get; set; }

    public AutomationDecision? LastDecision { get; private set; }

    public event Action<AutomationDecision>? DecisionMade;

    /// <summary>Starts the periodic schedule tick (time-window rules need re-evaluation without events).</summary>
    public void Start()
    {
        ScheduleNextTick();
        Reevaluate();
    }

    public void Reevaluate()
    {
        var state = StateProvider?.Invoke();
        if (state is null)
        {
            return;
        }

        var profile = state.ActiveProfile;
        if (profile is null)
        {
            return;
        }

        if (!state.AutomationsEnabled)
        {
            LastDecision = new AutomationDecision(EvaluationResult.Empty, ["automations disabled (master switch)"], _clock.LocalNow);
            DecisionMade?.Invoke(LastDecision);
            return;
        }

        var snapshot = _source.GetSnapshot();
        var evaluation = AutomationEngine.Evaluate(profile.Rules, snapshot);
        var applied = new List<string>();

        foreach (var contributed in evaluation.EffectiveActions)
        {
            var attribution = ApplyAttribution.Rule(contributed.Rule.Id, contributed.Rule.Name);
            var action = contributed.Action;

            switch (action)
            {
                case SwitchPresetAction switchPreset when profile.ActivePresetId != switchPreset.PresetId:
                    SwitchPreset?.Invoke(switchPreset.PresetId, attribution);
                    applied.Add(action.Describe());
                    break;
                case SetEffectEnabledAction setEnabled when GetEffectEnabled(profile.Effects, setEnabled.Effect) != setEnabled.Enabled:
                    SetEffectEnabled?.Invoke(setEnabled.Effect, setEnabled.Enabled, attribution);
                    applied.Add(action.Describe());
                    break;
                case SetEffectIntensityAction setIntensity when GetEffectIntensity(profile.Effects, setIntensity.Effect) != setIntensity.Intensity:
                    SetEffectIntensity?.Invoke(setIntensity.Effect, setIntensity.Intensity, attribution);
                    applied.Add(action.Describe());
                    break;
            }
        }

        LastDecision = new AutomationDecision(evaluation, applied, _clock.LocalNow);
        DecisionMade?.Invoke(LastDecision);
    }

    public static bool GetEffectEnabled(EffectSettings effects, EffectKind kind) => kind switch
    {
        EffectKind.NightMode => effects.NightMode.Enabled,
        EffectKind.Loudness => effects.Loudness.Enabled,
        EffectKind.StereoWidth => effects.StereoWidth.Enabled,
        EffectKind.Ambience => effects.Ambience.Enabled,
        EffectKind.Fidelity => effects.Fidelity.Enabled,
        _ => false,
    };

    public static int GetEffectIntensity(EffectSettings effects, EffectKind kind) => kind switch
    {
        EffectKind.NightMode => effects.NightMode.Intensity,
        EffectKind.Loudness => effects.Loudness.Intensity,
        EffectKind.StereoWidth => effects.StereoWidth.WidthPercent,
        EffectKind.Ambience => effects.Ambience.Intensity,
        EffectKind.Fidelity => effects.Fidelity.Intensity,
        _ => 0,
    };

    private void OnEnvironmentChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounceHandle?.Dispose();
            _debounceHandle = _scheduler.Schedule(DebounceDelay, () =>
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }

                Reevaluate();
            });
        }
    }

    private void ScheduleNextTick()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _tickHandle = _scheduler.Schedule(ScheduleTick, () =>
            {
                Reevaluate();
                ScheduleNextTick();
            });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _debounceHandle?.Dispose();
            _tickHandle?.Dispose();
        }

        _source.Changed -= OnEnvironmentChanged;
    }
}
