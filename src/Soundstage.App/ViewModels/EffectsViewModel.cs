using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundstage.App.Services;
using Soundstage.Core.Effects;

namespace Soundstage.App.ViewModels;

/// <summary>
/// The four effects, each a card with one toggle and one slider; everything deeper lives
/// in an advanced expander. Slider drags are debounced so APO isn't rewritten per pixel.
/// </summary>
public partial class EffectsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _syncing;
    private IDisposable? _debounce;

    // Night mode
    [ObservableProperty]
    private bool _nightEnabled;

    [ObservableProperty]
    private double _nightIntensity = 50;

    [ObservableProperty]
    private double _nightBassCorner = 90;

    [ObservableProperty]
    private bool _nightCornerOverridden;

    [ObservableProperty]
    private string _nightStatus = "";

    // Loudness
    [ObservableProperty]
    private bool _loudnessEnabled;

    [ObservableProperty]
    private double _loudnessIntensity = 50;

    // Width
    [ObservableProperty]
    private bool _widthEnabled;

    [ObservableProperty]
    private double _widthPercent = 100;

    [ObservableProperty]
    private string _widthStatus = "";

    // Fidelity
    [ObservableProperty]
    private bool _fidelityEnabled;

    [ObservableProperty]
    private double _fidelityIntensity = 50;

    // Ambience
    [ObservableProperty]
    private bool _ambienceVisible;

    [ObservableProperty]
    private bool _ambienceEnabled;

    [ObservableProperty]
    private double _ambienceIntensity = 30;

    public bool ApoAvailable => _services.ApoAvailable;

    /// <summary>The bundled VST effect rack (virtual bass, warmth, air, leveler, loudness).</summary>
    public VstRackViewModel Rack { get; }

    public EffectsViewModel(AppServices services)
    {
        _services = services;
        Rack = new VstRackViewModel(services);
        if (services.Controller is { } controller)
        {
            controller.Changed += () => UiDispatch.Post(SyncFromState);
            controller.Applied += result => UiDispatch.Post(() => OnApplied(result));
        }

        SyncFromState();
    }

    private void SyncFromState()
    {
        var profile = _services.Controller?.ActiveProfile;
        if (profile is null)
        {
            return;
        }

        _syncing = true;
        var e = profile.Effects;
        NightEnabled = e.NightMode.Enabled;
        NightIntensity = e.NightMode.Intensity;
        NightBassCorner = e.NightMode.EffectiveCornerHz;
        NightCornerOverridden = e.NightMode.BassCornerOverrideHz is not null;
        LoudnessEnabled = e.Loudness.Enabled;
        LoudnessIntensity = e.Loudness.Intensity;
        WidthEnabled = e.StereoWidth.Enabled;
        WidthPercent = e.StereoWidth.WidthPercent;
        AmbienceVisible = true;
        AmbienceEnabled = e.Ambience.Enabled;
        AmbienceIntensity = e.Ambience.Intensity;
        FidelityEnabled = e.Fidelity.Enabled;
        FidelityIntensity = e.Fidelity.Intensity;
        _syncing = false;
    }

    private void Push(bool immediate = false)
    {
        if (_syncing)
        {
            return;
        }

        _debounce?.Dispose();
        if (immediate)
        {
            Commit();
            return;
        }

        _debounce = _services.Scheduler.Schedule(TimeSpan.FromMilliseconds(250), () => UiDispatch.Post(Commit));
    }

    private void Commit()
    {
        // `with`-mutation preserves advanced overrides the page doesn't surface
        // (BassCutDbOverride, ReferenceLevelOverride, …).
        _services.Controller?.UpdateEffects(e => e with
        {
            NightMode = e.NightMode with
            {
                Enabled = NightEnabled,
                Intensity = (int)Math.Round(NightIntensity),
                BassCornerOverrideHz = NightCornerOverridden ? Math.Clamp(NightBassCorner, 40, 400) : null,
            },
            Loudness = e.Loudness with { Enabled = LoudnessEnabled, Intensity = (int)Math.Round(LoudnessIntensity) },
            StereoWidth = e.StereoWidth with { Enabled = WidthEnabled, WidthPercent = (int)Math.Round(WidthPercent) },
            Ambience = e.Ambience with { Enabled = AmbienceEnabled, Intensity = (int)Math.Round(AmbienceIntensity) },
            Fidelity = e.Fidelity with { Enabled = FidelityEnabled, Intensity = (int)Math.Round(FidelityIntensity) },
        });
    }

    partial void OnNightEnabledChanged(bool value) => Push(immediate: true);

    partial void OnNightIntensityChanged(double value)
    {
        // While the corner rides the slider, keep the advanced display in step so the user
        // can see intensity opening up the bass band.
        if (!_syncing && !NightCornerOverridden)
        {
            _syncing = true;
            NightBassCorner = NightModeSettings.MinCornerHz
                + (NightModeSettings.MaxCornerHz - NightModeSettings.MinCornerHz) * IntensityCurve.Fraction((int)Math.Round(value));
            _syncing = false;
        }

        Push();
    }

    partial void OnNightBassCornerChanged(double value)
    {
        if (!_syncing)
        {
            NightCornerOverridden = true; // dragging the corner pins it manually
            Push();
        }
    }

    [RelayCommand]
    private void ResetBassCorner()
    {
        NightCornerOverridden = false;
        _syncing = true;
        NightBassCorner = NightModeSettings.MinCornerHz
            + (NightModeSettings.MaxCornerHz - NightModeSettings.MinCornerHz) * IntensityCurve.Fraction((int)Math.Round(NightIntensity));
        _syncing = false;
        Push(immediate: true);
    }

    partial void OnLoudnessEnabledChanged(bool value) => Push(immediate: true);

    partial void OnLoudnessIntensityChanged(double value) => Push();

    partial void OnWidthEnabledChanged(bool value) => Push(immediate: true);

    partial void OnWidthPercentChanged(double value) => Push();

    partial void OnAmbienceEnabledChanged(bool value) => Push(immediate: true);

    partial void OnAmbienceIntensityChanged(double value) => Push();

    partial void OnFidelityEnabledChanged(bool value) => Push(immediate: true);

    partial void OnFidelityIntensityChanged(double value) => Push();

    [RelayCommand]
    private void ResetNight()
    {
        NightCornerOverridden = false;
        NightEnabled = false;
        NightIntensity = 50;
        Push(immediate: true);
    }

    [RelayCommand]
    private void ResetLoudness()
    {
        LoudnessEnabled = false;
        LoudnessIntensity = 50;
        Push(immediate: true);
    }

    [RelayCommand]
    private void ResetWidth()
    {
        WidthEnabled = false;
        WidthPercent = 100;
        Push(immediate: true);
    }

    [RelayCommand]
    private void ResetAmbience()
    {
        AmbienceEnabled = false;
        AmbienceIntensity = 30;
        Push(immediate: true);
    }

    [RelayCommand]
    private void ResetFidelity()
    {
        FidelityEnabled = false;
        FidelityIntensity = 50;
        Push(immediate: true);
    }

    private void OnApplied(Core.Configio.ApplyResult result)
    {
        WidthStatus = WidthEnabled
            ? "Amplifies the left/right separation already in your music — front L/R only, so your centre and surround channels are untouched."
            : "";
    }
}
