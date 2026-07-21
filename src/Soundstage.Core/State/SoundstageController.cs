using Soundstage.Core.Abstractions;
using Soundstage.Core.Automation;
using Soundstage.Core.Configio;
using Soundstage.Core.Effects;
using Soundstage.Core.Presets;

namespace Soundstage.Core.State;

/// <summary>
/// The one facade the UI (and tray, and hotkeys) talk to. Owns the state, routes every
/// mutation through the apply pipeline, persists after each change, follows the default
/// output device to its per-device profile, and feeds the automation coordinator.
/// </summary>
public sealed class SoundstageController : IDisposable
{
    private readonly StateStore _stateStore;
    private readonly PresetStore _presets;
    private readonly ApplyOrchestrator _orchestrator;
    private readonly IAudioEnvironmentSource _environment;
    private readonly AutomationCoordinator _coordinator;
    private readonly Abstractions.IDelayScheduler? _scheduler;
    private readonly object _gate = new();

    public SoundstageController(
        StateStore stateStore,
        PresetStore presets,
        ApplyOrchestrator orchestrator,
        IAudioEnvironmentSource environment,
        AutomationCoordinator coordinator,
        Abstractions.IDelayScheduler? scheduler = null)
    {
        _stateStore = stateStore;
        _presets = presets;
        _orchestrator = orchestrator;
        _environment = environment;
        _coordinator = coordinator;
        _scheduler = scheduler;

        State = _stateStore.Load();

        _coordinator.StateProvider = () => State;
        _coordinator.SwitchPreset = (presetId, attribution) => ApplyPreset(presetId, attribution);
        _coordinator.SetEffectEnabled = (kind, enabled, attribution) =>
            UpdateEffects(e => WithEffectEnabled(e, kind, enabled), attribution);
        _coordinator.SetEffectIntensity = (kind, intensity, attribution) =>
            UpdateEffects(e => WithEffectIntensity(e, kind, intensity), attribution);

        _environment.Changed += OnEnvironmentChanged;
        _orchestrator.ChainHashConfirmed += _ => SaveState();
        _orchestrator.WriteBlocked += blocked => WriteBlocked?.Invoke(blocked);
    }

    public SoundstageState State { get; }

    public PresetStore Presets => _presets;

    public ApplyOrchestrator Orchestrator => _orchestrator;

    public AutomationCoordinator Coordinator => _coordinator;

    /// <summary>
    /// Resolves the ambience impulse-response path for a sample rate (writing the file if
    /// needed). Provided by the app layer; null disables ambience compilation entirely.
    /// </summary>
    public Func<int, string?>? AmbienceIrResolver { get; set; }

    /// <summary>
    /// Resolves a VST plugin DLL filename to an absolute path (downloading/locating it if needed),
    /// or null if it isn't installed. Provided by the app layer; null disables the VST rack.
    /// </summary>
    public Func<string, string?>? VstPluginResolver { get; set; }

    /// <summary>Raised after any state mutation + apply cycle. UI refreshes off this.</summary>
    public event Action? Changed;

    /// <summary>Raised when an apply happened (carries headroom reports for the meter).</summary>
    public event Action<ApplyResult>? Applied;

    /// <summary>Raised when a config write was blocked (permission/lock) — the UI shows a hint instead of crashing.</summary>
    public event Action<ConfigWriteBlocked>? WriteBlocked;

    public DeviceProfile? ActiveProfile => State.ActiveProfile;

    /// <summary>Startup: adopt the current default device, compile, apply, start automations.</summary>
    public void Initialize()
    {
        AdoptCurrentDevice(applyAfter: false);
        Apply(ApplyAttribution.System("startup"));
        _coordinator.Start();
    }

    // ---- Preset & EQ ----

    public void ApplyPreset(string presetId, ApplyAttribution? attribution = null)
    {
        lock (_gate)
        {
            var profile = ActiveProfile;
            if (profile is null || profile.ActivePresetId == presetId)
            {
                return;
            }
        }

        // Only the user's own actions earn undo steps — automation churn would bury them.
        if ((attribution?.Source ?? AttributionSource.Manual) == AttributionSource.Manual)
        {
            PushUndoSnapshot();
        }

        lock (_gate)
        {
            if (ActiveProfile is { } profile)
            {
                profile.ActivePresetId = presetId;
            }
        }

        Apply(attribution ?? ApplyAttribution.Manual());
    }

    /// <summary>Re-applies after a preset's content changed (EQ editing).</summary>
    public void NotifyPresetContentChanged() => Apply(ApplyAttribution.Manual());

    /// <summary>
    /// Panic button: clears every effect and the preset on the active device back to stock (flat,
    /// no effects) and reapplies. Undoable, and scoped to the active device only.
    /// </summary>
    public void ResetToDefaults()
    {
        if (ActiveProfile is null)
        {
            return;
        }

        PushUndoSnapshot();
        lock (_gate)
        {
            if (ActiveProfile is { } profile)
            {
                profile.Effects = EffectSettings.Default;
                profile.ActivePresetId = "flat";
            }
        }

        Apply(ApplyAttribution.Manual());
    }

    // ---- Effects ----

    public void UpdateEffects(Func<EffectSettings, EffectSettings> mutate, ApplyAttribution? attribution = null)
    {
        EffectSettings previous, updated;
        lock (_gate)
        {
            var profile = ActiveProfile;
            if (profile is null)
            {
                return;
            }

            previous = profile.Effects;
            updated = mutate(previous);
            if (updated == previous)
            {
                return;
            }
        }

        if ((attribution?.Source ?? AttributionSource.Manual) == AttributionSource.Manual)
        {
            PushUndoSnapshot();
        }

        lock (_gate)
        {
            if (ActiveProfile is { } profile)
            {
                profile.Effects = updated;
            }
        }

        var attr = attribution ?? ApplyAttribution.Manual();
        if (!TryStartRamp(previous, updated, attr))
        {
            Apply(attr);
        }
    }

    // ---- Smooth transitions (ramp) ----

    private int _rampGeneration;

    /// <summary>Wall-clock spacing between ramp steps. Small because on a healthy setup an APO
    /// config reload is silent — it's the size of each gain step that must stay tiny, not the count.</summary>
    private static readonly TimeSpan RampStepInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// If the effect change is big enough to pop, eases into it: writes a series of interpolated
    /// configs over a few hundred ms, then does the real committed <see cref="Apply"/> as the final
    /// step. Returns false (caller applies normally) when smoothing is off, there's no scheduler,
    /// audio is bypassed, or the change is too small to bother. Works for every attribution source.
    /// </summary>
    private bool TryStartRamp(EffectSettings from, EffectSettings to, ApplyAttribution attribution)
    {
        if (_scheduler is null || !State.Settings.SmoothEffectTransitions || State.BypassActive)
        {
            return false;
        }

        List<string> steps;
        lock (_gate)
        {
            var profile = ActiveProfile;
            if (profile is null || !EffectRamp.ShouldRamp(from, to))
            {
                return false;
            }

            var count = EffectRamp.StepCount(from, to);
            steps = new List<string>(count - 1);
            for (var i = 1; i < count; i++)
            {
                profile.Effects = EffectRamp.Lerp(from, to, (double)i / count);
                steps.Add(ChainCompiler.Compile(State, _presets.Get, AmbienceIrResolver, VstPluginResolver).RenderedText);
            }

            profile.Effects = to; // leave the committed target in place for the final Apply
        }

        if (steps.Count == 0)
        {
            return false;
        }

        var generation = Interlocked.Increment(ref _rampGeneration);
        RunRampStep(steps, 0, generation, attribution);
        return true;
    }

    private void RunRampStep(List<string> steps, int index, int generation, ApplyAttribution attribution)
    {
        if (Volatile.Read(ref _rampGeneration) != generation)
        {
            return; // a newer change superseded this ramp — stop writing stale steps
        }

        if (index < steps.Count)
        {
            _orchestrator.WriteRampStep(steps[index]);
            _scheduler!.Schedule(RampStepInterval, () => RunRampStep(steps, index + 1, generation, attribution));
        }
        else
        {
            Apply(attribution); // committed target: full pipeline (persist, events, guard)
        }
    }

    // ---- Bypass & kill switch ----

    public void SetBypass(bool bypassed)
    {
        _orchestrator.SetBypass(State, bypassed);
        SaveState();
        Changed?.Invoke();
    }

    public void ToggleBypass() => SetBypass(!State.BypassActive);

    public void SetAutomationsEnabled(bool enabled)
    {
        State.AutomationsEnabled = enabled;
        SaveState();
        _coordinator.Reevaluate();
        Changed?.Invoke();
    }

    /// <summary>User confirmed a guarded apply (the countdown toast's Keep button).</summary>
    public void ConfirmPendingApply() => _orchestrator.Guard.Confirm();

    // ---- Undo ----

    private sealed record UndoSnapshot(string StateJson, string? PresetId, string? PresetJson);

    private const int MaxUndoDepth = 50;
    private readonly List<UndoSnapshot> _undoStack = [];

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _undoStack.Count > 0;
            }
        }
    }

    /// <summary>
    /// Captures the current sound (state + the active user preset's content) as an undo
    /// step. Callers push at the START of a user gesture, so a whole slider drag
    /// collapses into one step.
    /// </summary>
    public void PushUndoSnapshot()
    {
        lock (_gate)
        {
            var presetId = ActiveProfile?.ActivePresetId;
            var preset = presetId is null ? null : _presets.Get(presetId);
            var presetJson = preset is { IsBuiltIn: false }
                ? System.Text.Json.JsonSerializer.Serialize(preset, JsonDefaults.Readable)
                : null;

            _undoStack.Add(new UndoSnapshot(
                System.Text.Json.JsonSerializer.Serialize(State, JsonDefaults.Readable),
                presetId,
                presetJson));

            if (_undoStack.Count > MaxUndoDepth)
            {
                _undoStack.RemoveAt(0);
            }
        }

        // No Changed event here: pushing a snapshot is invisible to the sound; the apply
        // that follows the mutation raises Changed (and refreshes CanUndo bindings).
    }

    /// <summary>Steps back to the previous sound: profile state, effects, preset choice and content.</summary>
    public bool Undo()
    {
        UndoSnapshot snapshot;
        lock (_gate)
        {
            if (_undoStack.Count == 0)
            {
                return false;
            }

            snapshot = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);

            var restored = System.Text.Json.JsonSerializer.Deserialize<SoundstageState>(snapshot.StateJson, JsonDefaults.Readable);
            if (restored is null)
            {
                return false;
            }

            // Audio-affecting fields only. The physical device, settings, and the
            // confirmed-hash memory are not part of "what did I just change".
            State.BypassActive = restored.BypassActive;
            State.AutomationsEnabled = restored.AutomationsEnabled;
            State.Profiles.Clear();
            State.Profiles.AddRange(restored.Profiles);
        }

        if (snapshot.PresetJson is not null)
        {
            var preset = System.Text.Json.JsonSerializer.Deserialize<Presets.EqPreset>(snapshot.PresetJson, JsonDefaults.Readable);
            if (preset is not null)
            {
                preset.IsBuiltIn = false;
                _presets.Save(preset);
            }
        }

        // The current default device is physical reality — make sure it still has a profile.
        AdoptCurrentDevice(applyAfter: false);
        _orchestrator.SetBypass(State, State.BypassActive);
        Apply(ApplyAttribution.System("undo"));
        return true;
    }

    // ---- Devices ----

    /// <summary>
    /// Follows the default output device: ensures a profile exists (new devices get the
    /// prebuilt rules, disabled), records the live format for compile-time guards, and
    /// re-applies so the new device's own preset/effects are what the chain expresses.
    /// </summary>
    public void AdoptCurrentDevice(bool applyAfter = true)
    {
        var snapshot = _environment.GetSnapshot();
        if (snapshot.DeviceId is null)
        {
            return;
        }

        bool deviceSwitched;
        lock (_gate)
        {
            deviceSwitched = State.ActiveEndpointId != snapshot.DeviceId;
            var profile = State.GetOrCreateProfile(snapshot.DeviceId, snapshot.DeviceName ?? "");
            profile.LastKnownChannels = snapshot.Channels;
            profile.LastKnownSampleRateHz = snapshot.SampleRateHz;
            State.ActiveEndpointId = snapshot.DeviceId;
        }

        if (applyAfter && deviceSwitched)
        {
            Apply(ApplyAttribution.System($"switched to device “{snapshot.DeviceName}”"));
        }
        else if (applyAfter)
        {
            // Same device, maybe a format change (2.0 ↔ 5.1) — recompile guards.
            Apply(State.LastAttribution ?? ApplyAttribution.System("format change"));
        }
    }

    // ---- Apply plumbing ----

    public ApplyResult? Apply(ApplyAttribution attribution)
    {
        // Any committed apply supersedes an in-flight ramp: a preset switch (or the ramp's own final
        // step) bumps the generation so no stale interpolated step can overwrite the new chain.
        Interlocked.Increment(ref _rampGeneration);

        ApplyResult result;
        lock (_gate)
        {
            result = _orchestrator.Apply(State, _presets.Get, attribution, AmbienceIrResolver, VstPluginResolver);
        }

        SaveState();
        Applied?.Invoke(result);
        Changed?.Invoke();
        return result;
    }

    public void SaveState()
    {
        lock (_gate)
        {
            _stateStore.Save(State);
        }
    }

    private void OnEnvironmentChanged() => AdoptCurrentDevice();

    private static EffectSettings WithEffectEnabled(EffectSettings e, EffectKind kind, bool enabled) => kind switch
    {
        EffectKind.NightMode => e with { NightMode = e.NightMode with { Enabled = enabled } },
        EffectKind.Loudness => e with { Loudness = e.Loudness with { Enabled = enabled } },
        EffectKind.StereoWidth => e with { StereoWidth = e.StereoWidth with { Enabled = enabled } },
        EffectKind.Ambience => e with { Ambience = e.Ambience with { Enabled = enabled } },
        EffectKind.Fidelity => e with { Fidelity = e.Fidelity with { Enabled = enabled } },
        _ => e,
    };

    private static EffectSettings WithEffectIntensity(EffectSettings e, EffectKind kind, int intensity) => kind switch
    {
        EffectKind.NightMode => e with { NightMode = e.NightMode with { Intensity = intensity } },
        EffectKind.Loudness => e with { Loudness = e.Loudness with { Intensity = intensity } },
        EffectKind.StereoWidth => e with { StereoWidth = e.StereoWidth with { WidthPercent = intensity } },
        EffectKind.Ambience => e with { Ambience = e.Ambience with { Intensity = intensity } },
        EffectKind.Fidelity => e with { Fidelity = e.Fidelity with { Intensity = intensity } },
        _ => e,
    };

    public void Dispose()
    {
        _environment.Changed -= OnEnvironmentChanged;
        _coordinator.Dispose();
    }
}
