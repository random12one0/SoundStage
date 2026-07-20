using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundstage.App.Services;
using Soundstage.Core.Automation;
using Soundstage.Core.Configio;
using Soundstage.Core.Diagnostics;

namespace Soundstage.App.ViewModels;

/// <summary>
/// APO health + the Atmos audibility test (the one validation only human ears can do):
/// applies an unmistakable muffling EQ for 8 seconds so the user can confirm Equalizer APO
/// is really in the signal path while Dolby Atmos for Home Theater is active.
/// </summary>
public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private IDisposable? _sweepHandle;
    private IDisposable? _countdownHandle;

    [ObservableProperty]
    private bool _apoInstalled;

    [ObservableProperty]
    private string _configDirectory = "";

    [ObservableProperty]
    private string _ownershipText = "";

    [ObservableProperty]
    private bool _needsTakeover;

    [ObservableProperty]
    private string _spatialText = "";

    [ObservableProperty]
    private bool _sweepRunning;

    [ObservableProperty]
    private int _sweepSecondsLeft;

    [ObservableProperty]
    private string _statusMessage = "";

    public DiagnosticsViewModel(AppServices services)
    {
        _services = services;
        if (services.Controller is { } controller)
        {
            controller.Changed += () => UiDispatch.Post(Refresh);
        }

        Refresh();
    }

    public void Refresh()
    {
        ApoInstalled = _services.RegistryApo.IsApoInstalled || _services.Layout is not null;
        ConfigDirectory = _services.Layout?.ConfigDirectory ?? _services.RegistryApo.ConfigDirectory ?? "not found";

        var takeover = _services.Takeover;
        if (takeover is null)
        {
            OwnershipText = "Equalizer APO was not found. Install it, then restart Soundstage.";
            NeedsTakeover = false;
        }
        else
        {
            var state = takeover.GetOwnershipState();
            NeedsTakeover = state is not OwnershipState.Owned;
            OwnershipText = state switch
            {
                OwnershipState.Owned => "Soundstage manages config.txt. Everything is wired up.",
                OwnershipState.OwnedButModified => "config.txt was edited by hand or another tool since Soundstage set it up.",
                OwnershipState.NotOwned => "config.txt belongs to another tool (Peace or a manual setup). Taking control backs it up first.",
                _ => "config.txt does not exist yet — taking control will create it.",
            };
        }

        var snapshot = _services.Environment.GetSnapshot();
        SpatialText = snapshot.Spatial switch
        {
            SpatialAudioState.On => "Windows reports spatial audio (e.g. Dolby Atmos) is ACTIVE on the default output.",
            SpatialAudioState.Off => "Windows reports spatial audio is not active on the default output.",
            _ => "Spatial audio state could not be determined on this endpoint.",
        };
    }

    [RelayCommand]
    private void TakeOwnership()
    {
        var takeover = _services.Takeover;
        if (takeover is null)
        {
            return;
        }

        try
        {
            var report = takeover.TakeOwnership();
            StatusMessage = report.OriginalBackupPath is not null
                ? $"Took control. Your previous config was backed up ({report.PreviousKind})."
                : "Took control of config.txt.";
            _services.Controller?.Apply(Core.State.ApplyAttribution.System("took ownership"));
        }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "Windows blocked writing to the APO config folder. Restart Soundstage as administrator once, or grant your user write access to the EqualizerAPO\\config folder.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not take control: {ex.Message}";
        }

        Refresh();
    }

    [RelayCommand]
    private void Disown()
    {
        try
        {
            _services.Takeover?.Disown();
            StatusMessage = "Restored your original config.txt. Soundstage is no longer in the signal path.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not restore: {ex.Message}";
        }

        Refresh();
    }

    [RelayCommand]
    private void RunSweep() => StartSweep(DiagnosticSweep.BuildChainText());

    /// <summary>
    /// Same muffle test but inside a Device: section for the current output — verifies
    /// APO's device targeting works with the match text Soundstage generates (only used
    /// when two or more device profiles make scoping necessary).
    /// </summary>
    [RelayCommand]
    private void RunScopedSweep()
    {
        var matchSpec = _services.Controller?.ActiveProfile?.EffectiveMatchSpec;
        if (matchSpec is null)
        {
            StatusMessage = "No active device profile to target.";
            return;
        }

        StartSweep(DiagnosticSweep.BuildScopedChainText(matchSpec));
    }

    private void StartSweep(string chainText)
    {
        var orchestrator = _services.Orchestrator;
        if (orchestrator is null || SweepRunning)
        {
            return;
        }

        _sweepHandle = orchestrator.ApplyTemporary(chainText, DiagnosticSweep.DefaultDuration, _services.Scheduler);
        SweepRunning = true;
        SweepSecondsLeft = (int)DiagnosticSweep.DefaultDuration.TotalSeconds;
        StatusMessage = "";
        TickSweep();
    }

    [RelayCommand]
    private void StopSweep()
    {
        _sweepHandle?.Dispose();
        _sweepHandle = null;
        _countdownHandle?.Dispose();
        SweepRunning = false;
    }

    private void TickSweep()
    {
        if (!SweepRunning)
        {
            return;
        }

        if (SweepSecondsLeft <= 0)
        {
            SweepRunning = false;
            return;
        }

        _countdownHandle = _services.Scheduler.Schedule(TimeSpan.FromSeconds(1), () => UiDispatch.Post(() =>
        {
            SweepSecondsLeft--;
            TickSweep();
        }));
    }
}
