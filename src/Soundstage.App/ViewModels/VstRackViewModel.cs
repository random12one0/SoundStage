using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundstage.App.Services;
using Soundstage.Core.Effects;
using Soundstage.Core.State;

namespace Soundstage.App.ViewModels;

/// <summary>One rack effect row: a toggle + intensity, disabled until its plugin is installed.</summary>
public partial class RackEffectViewModel : ObservableObject
{
    private readonly Action _changed;

    public RackEffectViewModel(VstRackEffect def, bool enabled, int intensity, bool installed, Action changed)
    {
        Def = def;
        _changed = changed;
        _enabled = enabled;
        _intensity = intensity;
        Installed = installed;
    }

    public VstRackEffect Def { get; }

    public string Name => Def.Name;

    public string Description => Def.Description;

    public bool Installed { get; }

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private double _intensity;

    partial void OnEnabledChanged(bool value) => _changed();

    partial void OnIntensityChanged(double value) => _changed();
}

/// <summary>
/// The VST effect rack: bundled Airwindows plugins (virtual bass, warmth, air, leveler, loudness)
/// shown as toggles + intensity. Handles the one-time install of the plugin DLLs and skips anything
/// not installed yet.
/// </summary>
public partial class VstRackViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _syncing;
    private IDisposable? _debounce;

    [ObservableProperty]
    private ObservableCollection<RackEffectViewModel> _effects = [];

    [ObservableProperty]
    private string _installStatus = "";

    [ObservableProperty]
    private bool _needsInstall;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private bool _canAutoInstall;

    public VstRackViewModel(AppServices services)
    {
        _services = services;
        CanAutoInstall = services.Vst.CanAutoInstall;
        if (services.Controller is { } controller)
        {
            controller.Changed += () => UiDispatch.Post(Rebuild);
        }

        Rebuild();
    }

    private void Rebuild()
    {
        _syncing = true;
        var state = _services.Controller?.ActiveProfile?.VstRack.ToDictionary(e => e.Id)
                    ?? new Dictionary<string, VstRackEntry>();

        Effects = new ObservableCollection<RackEffectViewModel>(
            VstCatalog.All.Select(def =>
            {
                var entry = state.GetValueOrDefault(def.Id);
                return new RackEffectViewModel(
                    def,
                    entry?.Enabled ?? false,
                    entry?.Intensity ?? 50,
                    _services.Vst.Resolve(def.DllFileName) is not null,
                    OnChanged);
            }));

        var installed = _services.Vst.InstalledCount;
        var total = _services.Vst.TotalCount;
        NeedsInstall = installed < total;
        InstallStatus = installed == total
            ? $"All {total} effects are built in and ready."
            : $"{installed} of {total} effects ready.";
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

        profile.VstRack = Effects
            .Select(e => new VstRackEntry(e.Def.Id, e.Enabled && e.Installed, (int)Math.Round(e.Intensity)))
            .ToList();
        _services.Controller?.SaveState();
        _services.Controller?.Apply(ApplyAttribution.Manual());
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        IsInstalling = true;
        var progress = new Progress<string>(s => InstallStatus = s);
        var ok = await _services.Vst.InstallAsync(progress);
        IsInstalling = false;
        Rebuild();
        if (!ok)
        {
            InstallStatus = _services.Vst.CanAutoInstall
                ? "Couldn't install automatically. Use “Get from Airwindows”, unzip, and drop the .dll files into the plugins folder."
                : "Click “Get from Airwindows”, unzip the pack, then “Open plugins folder” and drop the .dll files in.";
        }

        _services.Controller?.Apply(ApplyAttribution.Manual());
    }

    [RelayCommand]
    private void OpenFolder() => _services.Vst.OpenPluginFolder();

    /// <summary>Point at a folder the user already downloaded and auto-copy the rack's DLLs in.</summary>
    [RelayCommand]
    private void ImportFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pick the Airwindows folder you downloaded" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var count = _services.Vst.ImportFromFolder(dialog.FolderName);
        Rebuild();
        InstallStatus = count > 0
            ? $"Imported {count} effect{(count == 1 ? "" : "s")} — {InstallStatus}"
            : "No matching effect DLLs in that folder. Look for BassKit64.dll, PurestDrive64.dll, Air264.dll, Pressure464.dll and ADClip764.dll (the “64” builds).";
        _services.Controller?.Apply(ApplyAttribution.Manual());
    }

    [RelayCommand]
    private void GetFromAirwindows() => _services.Vst.OpenDownloadPage();

    /// <summary>Re-scans the plugin folder — used after a manual drop of the DLLs.</summary>
    [RelayCommand]
    private void Recheck()
    {
        Rebuild();
        _services.Controller?.Apply(ApplyAttribution.Manual());
    }
}
