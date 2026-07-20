using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundstage.App.Services;
using Soundstage.Core.Configio;
using Soundstage.Core.State;

namespace Soundstage.App.ViewModels;

public partial class DeviceProfileViewModel : ObservableObject
{
    private readonly DevicesViewModel _owner;

    public DeviceProfileViewModel(DeviceProfile profile, bool isActive, string? presetName, DevicesViewModel owner)
    {
        Profile = profile;
        IsActive = isActive;
        PresetName = presetName ?? "No preset";
        _owner = owner;
        _enabled = profile.Enabled;
    }

    public DeviceProfile Profile { get; }

    public string FriendlyName => string.IsNullOrEmpty(Profile.FriendlyName) ? Profile.EndpointId : Profile.FriendlyName;

    public string FormatText => $"{ChainCompiler.FormatChannels(Profile.LastKnownChannels)} · {Profile.LastKnownSampleRateHz / 1000.0:0.#} kHz";

    public string RulesText => $"{Profile.Rules.Count(r => r.Enabled)} of {Profile.Rules.Count} automations on";

    public bool IsActive { get; }

    public string PresetName { get; }

    [ObservableProperty]
    private bool _enabled;

    partial void OnEnabledChanged(bool value)
    {
        Profile.Enabled = value;
        _owner.ProfileMutated();
    }
}

/// <summary>
/// Per-device profiles: each output device keeps its own preset, effects, and automations;
/// Soundstage follows the Windows default device automatically.
/// </summary>
public partial class DevicesViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private ObservableCollection<DeviceProfileViewModel> _profiles = [];

    public bool ApoAvailable => _services.ApoAvailable;

    public DevicesViewModel(AppServices services)
    {
        _services = services;
        if (services.Controller is { } controller)
        {
            controller.Changed += () => UiDispatch.Post(Reload);
        }

        Reload();
    }

    public void Reload()
    {
        var controller = _services.Controller;
        if (controller is null)
        {
            return;
        }

        Profiles = new ObservableCollection<DeviceProfileViewModel>(
            controller.State.Profiles.Select(p => new DeviceProfileViewModel(
                p,
                p.EndpointId == controller.State.ActiveEndpointId,
                p.ActivePresetId is { } id ? controller.Presets.Get(id)?.Name : null,
                this)));
    }

    internal void ProfileMutated() => _services.Controller?.Apply(ApplyAttribution.System("device profile changed"));

    [RelayCommand]
    private void RemoveProfile(DeviceProfileViewModel? row)
    {
        var controller = _services.Controller;
        if (row is null || controller is null || row.IsActive)
        {
            return;
        }

        controller.State.Profiles.Remove(row.Profile);
        controller.Apply(ApplyAttribution.System("device profile removed"));
        Reload();
    }
}
