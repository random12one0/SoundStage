using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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
    private bool _nightUseVst;

    [ObservableProperty]
    private string? _nightVstPath;

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

    // Ambience
    [ObservableProperty]
    private bool _ambienceVisible;

    [ObservableProperty]
    private bool _ambienceEnabled;

    [ObservableProperty]
    private double _ambienceIntensity = 30;

    public bool ApoAvailable => _services.ApoAvailable;

    public bool WidthInDangerZone => WidthPercent > StereoWidthSettings.DangerThresholdPercent;

    public EffectsViewModel(AppServices services)
    {
        _services = services;
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
        NightBassCorner = e.NightMode.BassCornerHz;
        NightUseVst = e.NightMode.UseVstCompressor;
        NightVstPath = e.NightMode.VstLibraryPath;
        LoudnessEnabled = e.Loudness.Enabled;
        LoudnessIntensity = e.Loudness.Intensity;
        WidthEnabled = e.StereoWidth.Enabled;
        WidthPercent = e.StereoWidth.WidthPercent;
        AmbienceVisible = _services.Controller!.State.Settings.AmbienceFeatureEnabled;
        AmbienceEnabled = e.Ambience.Enabled;
        AmbienceIntensity = e.Ambience.Intensity;
        _syncing = false;
        OnPropertyChanged(nameof(WidthInDangerZone));
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
        // (BassCutDbOverride, VstRawArguments, ReferenceLevelOverride, …).
        _services.Controller?.UpdateEffects(e => e with
        {
            NightMode = e.NightMode with
            {
                Enabled = NightEnabled,
                Intensity = (int)Math.Round(NightIntensity),
                BassCornerHz = Math.Clamp(NightBassCorner, 40, 200),
                UseVstCompressor = NightUseVst,
                VstLibraryPath = NightVstPath,
            },
            Loudness = e.Loudness with { Enabled = LoudnessEnabled, Intensity = (int)Math.Round(LoudnessIntensity) },
            StereoWidth = e.StereoWidth with { Enabled = WidthEnabled, WidthPercent = (int)Math.Round(WidthPercent) },
            Ambience = e.Ambience with { Enabled = AmbienceEnabled, Intensity = (int)Math.Round(AmbienceIntensity) },
        });
    }

    partial void OnNightEnabledChanged(bool value) => Push(immediate: true);

    partial void OnNightIntensityChanged(double value) => Push();

    partial void OnNightBassCornerChanged(double value) => Push();

    partial void OnNightUseVstChanged(bool value) => Push(immediate: true);

    partial void OnLoudnessEnabledChanged(bool value) => Push(immediate: true);

    partial void OnLoudnessIntensityChanged(double value) => Push();

    partial void OnWidthEnabledChanged(bool value) => Push(immediate: true);

    partial void OnWidthPercentChanged(double value)
    {
        OnPropertyChanged(nameof(WidthInDangerZone));
        Push();
    }

    partial void OnAmbienceEnabledChanged(bool value) => Push(immediate: true);

    partial void OnAmbienceIntensityChanged(double value) => Push();

    [RelayCommand]
    private void PickVst()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a VST 2 compressor DLL",
            Filter = "VST 2 plugin (*.dll)|*.dll",
        };
        if (dialog.ShowDialog() == true)
        {
            NightVstPath = dialog.FileName;
            NightUseVst = true;
            Push(immediate: true);
        }
    }

    [RelayCommand]
    private void ClearVst()
    {
        NightVstPath = null;
        NightUseVst = false;
        Push(immediate: true);
    }

    private void OnApplied(Core.Configio.ApplyResult result)
    {
        var endpointId = _services.Controller?.State.ActiveEndpointId;
        var report = result.Compilation.Devices.FirstOrDefault(d => d.EndpointId == endpointId);
        var notes = report?.Notes ?? [];

        NightStatus = NightEnabled && notes.Any(n => n.Contains("Compression off", StringComparison.Ordinal))
            ? "Compression off — pick a compressor VST below for full night mode. The bass shelf still works."
            : NightEnabled && NightUseVst
                ? "Bass shelf + VST compressor active."
                : "";

        WidthStatus = notes.FirstOrDefault(n => n.Contains("stereo-only", StringComparison.Ordinal))
                      ?? (WidthEnabled && WidthInDangerZone
                          ? "Past 120% center vocals start sounding thin and distant."
                          : "");
    }
}
