using CommunityToolkit.Mvvm.ComponentModel;
using Soundstage.App.Services;
using Soundstage.Core.Automation;
using Soundstage.Core.Configio;
using Soundstage.Core.State;

namespace Soundstage.App.ViewModels;

/// <summary>
/// The always-visible readout: device • layout • format • spatial • preset • attribution •
/// headroom • clip light. Purely informational.
/// </summary>
public partial class StatusBarViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private string _deviceName = "—";

    [ObservableProperty]
    private string _channelText = "";

    [ObservableProperty]
    private string _sourceText = "";

    [ObservableProperty]
    private string _formatText = "";

    [ObservableProperty]
    private string _spatialText = "";

    [ObservableProperty]
    private string _nowPlayingText = "";

    [ObservableProperty]
    private string _presetText = "—";

    [ObservableProperty]
    private string _attributionText = "";

    [ObservableProperty]
    private string _headroomText = "";

    [ObservableProperty]
    private bool _isClipping;

    [ObservableProperty]
    private bool _isBypassed;

    [ObservableProperty]
    private bool _automationsPaused;

    private IDisposable? _clipResetHandle;

    public StatusBarViewModel(AppServices services)
    {
        _services = services;

        if (services.Controller is { } controller)
        {
            controller.Changed += () => UiDispatch.Post(Refresh);
            controller.Applied += result => UiDispatch.Post(() => OnApplied(result));
        }

        services.PeakMeter.ClippingDetected += () => UiDispatch.Post(OnClipping);
        Refresh();
    }

    public void Refresh()
    {
        var snapshot = _services.Environment.GetSnapshot();
        DeviceName = snapshot.DeviceName ?? "No output device";
        // "out": this is the device's configured speaker layout (what Windows mixes to),
        // not the content's channel count — stereo Spotify still shows 7.1 on a 7.1 rig.
        ChannelText = $"{ChainCompiler.FormatChannels(snapshot.Channels)} out";
        // What's coming IN from the app that's playing (2.0 vs 5.1 vs 7.1) — the counterpart to
        // the "out" layout above. Empty when nothing's playing or metering is unavailable.
        SourceText = snapshot.SourceChannels > 0
            ? $"{ChainCompiler.FormatChannels(snapshot.SourceChannels)} in"
            : "";
        FormatText = snapshot.BitDepth > 0
            ? $"{snapshot.SampleRateHz / 1000.0:0.#} kHz · {snapshot.BitDepth}-bit"
            : $"{snapshot.SampleRateHz / 1000.0:0.#} kHz";
        NowPlayingText = FormatNowPlaying(snapshot);
        SpatialText = snapshot.Spatial switch
        {
            SpatialAudioState.On => "Spatial audio on",
            SpatialAudioState.Off => "Spatial audio off",
            _ => "",
        };

        var controller = _services.Controller;
        if (controller is null)
        {
            PresetText = "Equalizer APO not found";
            AttributionText = "";
            return;
        }

        IsBypassed = controller.State.BypassActive;
        AutomationsPaused = !controller.State.AutomationsEnabled;

        var profile = controller.ActiveProfile;
        var preset = profile?.ActivePresetId is { } id ? controller.Presets.Get(id) : null;
        PresetText = IsBypassed ? "Bypassed" : preset?.Name ?? "No preset";
        AttributionText = !IsBypassed && preset is not null && controller.State.LastAttribution is { } attribution
            ? attribution.Description
            : "";
    }

    private void OnApplied(ApplyResult result)
    {
        var endpointId = _services.Controller?.State.ActiveEndpointId;
        var report = result.Compilation.Devices.FirstOrDefault(d => d.EndpointId == endpointId)
                     ?? result.Compilation.Devices.FirstOrDefault();
        HeadroomText = report is null ? "" : $"{report.Headroom.HeadroomDb:0.0} dB headroom";
        Refresh();
    }

    /// <summary>"♪ YouTube" / "♪ Spotify · YouTube" for what's currently playing audio.</summary>
    internal static string FormatNowPlaying(Core.Automation.AudioEnvironmentSnapshot snapshot)
    {
        // Prefer the source's enriched labels (a browser tab resolved to "YouTube"); fall back to
        // prettifying the raw process names for sources that didn't enrich them (tests, non-Windows).
        var labels = snapshot.NowPlaying.Count > 0
            ? snapshot.NowPlaying
            : snapshot.ActiveAudioProcesses
                .Where(p => !Core.Automation.AudioApps.IsNoise(p))
                .Select(Core.Automation.AudioApps.Pretty);

        var distinct = labels.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return "";
        }

        var shown = distinct.Take(2).ToList();
        var extra = distinct.Count - shown.Count;
        return "♪ " + string.Join(" · ", shown) + (extra > 0 ? $" +{extra}" : "");
    }

    private void OnClipping()
    {
        IsClipping = true;
        _clipResetHandle?.Dispose();
        _clipResetHandle = _services.Scheduler.Schedule(TimeSpan.FromSeconds(3), () => UiDispatch.Post(() => IsClipping = false));
    }
}
