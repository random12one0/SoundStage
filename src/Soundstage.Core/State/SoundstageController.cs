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
    private readonly object _gate = new();

    public SoundstageController(
        StateStore stateStore,
        PresetStore presets,
        ApplyOrchestrator orchestrator,
        IAudioEnvironmentSource environment,
        AutomationCoordinator coordinator)
    {
        _stateStore = stateStore;
        _presets = presets;
        _orchestrator = orchestrator;
        _environment = environment;
        _coordinator = coordinator;

        State = _stateStore.Load();

        _coordinator.StateProvider = () => State;
        _coordinator.SwitchPreset = (presetId, attribution) => ApplyPreset(presetId, attribution);
        _coordinator.SetEffectEnabled = (kind, enabled, attribution) =>
            UpdateEffects(e => WithEffectEnabled(e, kind, enabled), attribution);
        _coordinator.SetEffectIntensity = (kind, intensity, attribution) =>
            UpdateEffects(e => WithEffectIntensity(e, kind, intensity), attribution);

        _environment.Changed += OnEnvironmentChanged;
        _orchestrator.ChainHashConfirmed += _ => SaveState();
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

    /// <summary>Raised after any state mutation + apply cycle. UI refreshes off this.</summary>
    public event Action? Changed;

    /// <summary>Raised when an apply happened (carries headroom reports for the meter).</summary>
    public event Action<ApplyResult>? Applied;

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

            profile.ActivePresetId = presetId;
        }

        Apply(attribution ?? ApplyAttribution.Manual());
    }

    /// <summary>Re-applies after a preset's content changed (EQ editing).</summary>
    public void NotifyPresetContentChanged() => Apply(ApplyAttribution.Manual());

    // ---- Effects ----

    public void UpdateEffects(Func<EffectSettings, EffectSettings> mutate, ApplyAttribution? attribution = null)
    {
        lock (_gate)
        {
            var profile = ActiveProfile;
            if (profile is null)
            {
                return;
            }

            var updated = mutate(profile.Effects);
            if (updated == profile.Effects)
            {
                return;
            }

            profile.Effects = updated;
        }

        Apply(attribution ?? ApplyAttribution.Manual());
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
        ApplyResult result;
        lock (_gate)
        {
            result = _orchestrator.Apply(State, _presets.Get, attribution, AmbienceIrResolver);
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
        _ => e,
    };

    private static EffectSettings WithEffectIntensity(EffectSettings e, EffectKind kind, int intensity) => kind switch
    {
        EffectKind.NightMode => e with { NightMode = e.NightMode with { Intensity = intensity } },
        EffectKind.Loudness => e with { Loudness = e.Loudness with { Intensity = intensity } },
        EffectKind.StereoWidth => e with { StereoWidth = e.StereoWidth with { WidthPercent = intensity } },
        EffectKind.Ambience => e with { Ambience = e.Ambience with { Intensity = intensity } },
        _ => e,
    };

    public void Dispose()
    {
        _environment.Changed -= OnEnvironmentChanged;
        _coordinator.Dispose();
    }
}
