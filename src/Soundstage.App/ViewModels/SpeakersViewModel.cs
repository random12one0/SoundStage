using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundstage.App.Services;
using Soundstage.Core.Configio;
using Soundstage.Core.Effects;
using Soundstage.Core.State;

namespace Soundstage.App.ViewModels;

/// <summary>One speaker's level row. <see cref="Cut"/> is attenuation in dB (0 = full level).</summary>
public partial class ChannelTrimViewModel : ObservableObject
{
    private readonly Action _changed;

    public ChannelTrimViewModel(string apo, string label, double cutDb, Action changed)
    {
        Apo = apo;
        Label = label;
        _cut = cutDb;
        _changed = changed;
    }

    public string Apo { get; }

    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CutLabel))]
    private double _cut;

    /// <summary>"0 dB" at full level, otherwise "-N dB".</summary>
    public string CutLabel => Cut < 0.05 ? "0 dB" : $"-{Cut:0.#} dB";

    partial void OnCutChanged(double value) => _changed();
}

/// <summary>
/// Speaker calibration: a level trim per output channel (turn the subwoofer down, balance a
/// too-loud centre, etc.). Attenuation only, so it's always safe. Reads the active device's channel
/// layout and writes per-channel cuts into the profile.
/// </summary>
public partial class SpeakersViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _syncing;
    private IDisposable? _debounce;

    [ObservableProperty]
    private ObservableCollection<ChannelTrimViewModel> _channels = [];

    [ObservableProperty]
    private string _deviceName = "—";

    [ObservableProperty]
    private string _layoutLabel = "";

    [ObservableProperty]
    private bool _hasChannels;

    public bool ApoAvailable => _services.ApoAvailable;

    public SpeakersViewModel(AppServices services)
    {
        _services = services;
        if (services.Controller is { } controller)
        {
            controller.Changed += () => UiDispatch.Post(Rebuild);
        }

        Rebuild();
    }

    private void Rebuild()
    {
        var profile = _services.Controller?.ActiveProfile;
        if (profile is null)
        {
            HasChannels = false;
            Channels = [];
            return;
        }

        _syncing = true;
        DeviceName = profile.FriendlyName;
        LayoutLabel = ChainCompiler.FormatChannels(profile.LastKnownChannels);
        var layout = SpeakerLayout.For(profile.LastKnownChannels);
        HasChannels = layout.Count > 0;
        var trims = profile.SpeakerTrims.ToDictionary(t => t.Channel, t => t.TrimDb);
        Channels = new ObservableCollection<ChannelTrimViewModel>(
            layout.Select(c => new ChannelTrimViewModel(
                c.Apo, c.Label,
                trims.TryGetValue(c.Apo, out var db) ? Math.Abs(db) : 0,
                OnChanged)));
        _syncing = false;
    }

    private void OnChanged()
    {
        if (_syncing)
        {
            return;
        }

        _debounce?.Dispose();
        _debounce = _services.Scheduler.Schedule(TimeSpan.FromMilliseconds(250), () => UiDispatch.Post(Commit));
    }

    private void Commit()
    {
        var profile = _services.Controller?.ActiveProfile;
        if (profile is null)
        {
            return;
        }

        profile.SpeakerTrims = Channels
            .Where(c => c.Cut > 0.05)
            .Select(c => new ChannelTrim(c.Apo, -Math.Round(c.Cut, 1)))
            .ToList();
        _services.Controller?.SaveState();
        _services.Controller?.Apply(ApplyAttribution.Manual());
    }

    [RelayCommand]
    private void ResetLevels()
    {
        _syncing = true;
        foreach (var channel in Channels)
        {
            channel.Cut = 0;
        }

        _syncing = false;
        Commit();
    }
}
