using Soundstage.Core.Automation;
using Soundstage.Core.Configio;
using Soundstage.Core.Effects;
using Soundstage.Core.Presets;
using Soundstage.Core.State;
using Soundstage.Core.Tests.Support;
using Xunit;

namespace Soundstage.Core.Tests.State;

public class SoundstageControllerTests : IDisposable
{
    private sealed class FakeEnvironmentSource : IAudioEnvironmentSource
    {
        public AudioEnvironmentSnapshot Snapshot { get; set; } =
            new(DateTimeOffset.Parse("2026-07-20T20:00:00+02:00"),
                "{0.0.0.00000000}.{aaaa1111-0000-0000-0000-000000000001}",
                "Onkyo Receiver", 6, 48000, 24, SpatialAudioState.On, []);

        public event Action? Changed;

        public AudioEnvironmentSnapshot GetSnapshot() => Snapshot;

        public void Raise() => Changed?.Invoke();
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly TestClock _clock = new();
    private readonly TestScheduler _scheduler = new();
    private readonly ConfigLayout _layout = new(@"C:\config");
    private readonly FakeEnvironmentSource _environment = new();
    private readonly SoundstageController _controller;

    public SoundstageControllerTests()
    {
        var backups = new BackupService(_fs, _layout, _clock);
        var orchestrator = new ApplyOrchestrator(_fs, _layout, backups, new RevertGuard(_scheduler));
        var presets = new PresetStore(_fs, @"C:\AppData\Soundstage\presets", _clock);
        var coordinator = new AutomationCoordinator(_environment, _clock, _scheduler);
        _controller = new SoundstageController(
            new StateStore(_fs, @"C:\AppData\Soundstage\state.json"),
            presets,
            orchestrator,
            _environment,
            coordinator);

        new TakeoverService(_fs, _layout, backups).TakeOwnership();
    }

    public void Dispose() => _controller.Dispose();

    [Fact]
    public void Initialize_AdoptsDevice_CreatesEmptyProfile_AndApplies()
    {
        _controller.Initialize();

        var profile = Assert.Single(_controller.State.Profiles);
        Assert.Equal("Onkyo Receiver", profile.FriendlyName);
        Assert.Equal(6, profile.LastKnownChannels);
        // Zero automations by default — prebuilts are offered as quick-start cards to add.
        Assert.Empty(profile.Rules);
        Assert.True(_fs.FileExists(_layout.ChainFilePath));
        Assert.True(_fs.FileExists(@"C:\AppData\Soundstage\state.json"));
    }

    [Fact]
    public void ApplyPreset_WritesPresetIntoChain_GloballyForSingleDevice()
    {
        _controller.Initialize();
        _controller.ApplyPreset("music");

        var chain = _fs.ReadAllText(_layout.ChainFilePath);
        // One profile → no Device: scoping (APO device matching can silently no-match).
        Assert.DoesNotContain("Device:", chain);
        Assert.Contains("preset: Music", chain);
        Assert.Contains("Filter 1:", chain);
        Assert.Equal("music", _controller.ActiveProfile!.ActivePresetId);
    }

    [Fact]
    public void DeviceSwitch_SwapsProfile_AndBothSectionsStayInChain()
    {
        _controller.Initialize();
        _controller.ApplyPreset("music");

        // Plug in headphones: default device changes.
        _environment.Snapshot = _environment.Snapshot with
        {
            DeviceId = "{0.0.0.00000000}.{bbbb2222-0000-0000-0000-000000000002}",
            DeviceName = "USB Headphones",
            Channels = 2,
        };
        _environment.Raise();

        Assert.Equal(2, _controller.State.Profiles.Count);
        Assert.Equal("USB Headphones", _controller.ActiveProfile!.FriendlyName);
        Assert.Null(_controller.ActiveProfile.ActivePresetId); // fresh profile, own preset slot

        // Speakers keep their preset in their own section.
        var chain = _fs.ReadAllText(_layout.ChainFilePath);
        Assert.Contains("{aaaa1111-0000-0000-0000-000000000001}", chain);
        Assert.Contains("{bbbb2222-0000-0000-0000-000000000002}", chain);
        Assert.Contains("preset: Music", chain);

        // Switching back re-activates the speaker profile untouched.
        _environment.Snapshot = _environment.Snapshot with
        {
            DeviceId = "{0.0.0.00000000}.{aaaa1111-0000-0000-0000-000000000001}",
            DeviceName = "Onkyo Receiver",
            Channels = 6,
        };
        _environment.Raise();
        Assert.Equal("music", _controller.ActiveProfile!.ActivePresetId);
    }

    [Fact]
    public void UpdateEffects_OnActiveProfile_RecompilesChain()
    {
        _controller.Initialize();
        _controller.UpdateEffects(e => e with { Loudness = new LoudnessSettings(Enabled: true, Intensity: 100) });

        Assert.Contains($"LoudnessCorrection: State 1 ReferenceLevel {LoudnessSettings.MaxReferenceLevel:0.####}", _fs.ReadAllText(_layout.ChainFilePath));
    }

    [Fact]
    public void WidthEffect_AppliesOnSurroundToFrontLR_SurroundSafe()
    {
        _controller.Initialize(); // active device is 5.1
        _controller.UpdateEffects(e => e with { StereoWidth = new StereoWidthSettings(Enabled: true, WidthPercent: 140) });

        var chain = _fs.ReadAllText(_layout.ChainFilePath);
        // Now applies on 5.1 — mid/side scratch matrix, but only rebuilds the front L/R pair.
        Assert.Contains("Copy: SS_MID=0.5*L+0.5*R SS_SIDE=0.5*L-0.5*R", chain);
        Assert.Contains("Copy: L=SS_MID+1.4*SS_SIDE R=SS_MID-1.4*SS_SIDE", chain);
        Assert.DoesNotContain("C=", chain);
        Assert.DoesNotContain("SL=", chain);
    }

    [Fact]
    public void Bypass_TogglesSwitchFileOnly_AndPersists()
    {
        _controller.Initialize();
        var chainBefore = _fs.ReadAllText(_layout.ChainFilePath);

        _controller.ToggleBypass();

        Assert.True(_controller.State.BypassActive);
        Assert.True(ConfigLayout.IsSwitchBypassed(_fs.ReadAllText(_layout.SwitchFilePath)));
        Assert.Equal(chainBefore, _fs.ReadAllText(_layout.ChainFilePath));

        var reloaded = new StateStore(_fs, @"C:\AppData\Soundstage\state.json").Load();
        Assert.True(reloaded.BypassActive);
    }

    [Fact]
    public void AutomationRule_FiresThroughController_WithRuleAttribution()
    {
        _controller.Initialize();
        var profile = _controller.ActiveProfile!;
        profile.Rules.Clear();
        profile.Rules.Add(new AutomationRule
        {
            Id = "spotify",
            Name = "Music when Spotify plays",
            Trigger = new AudioAppTrigger(["Spotify"]),
            Actions = [new SwitchPresetAction("music")],
        });

        _environment.Snapshot = _environment.Snapshot with { ActiveAudioProcesses = ["Spotify"] };
        _controller.Coordinator.Reevaluate();

        Assert.Equal("music", _controller.ActiveProfile!.ActivePresetId);
        Assert.Equal(AttributionSource.Rule, _controller.State.LastAttribution!.Source);
        Assert.Equal("Music when Spotify plays", _controller.State.LastAttribution.RuleName);
    }

    [Fact]
    public void MasterKillSwitch_PersistsAndBlocksRules()
    {
        _controller.Initialize();
        _controller.ActiveProfile!.Rules.ForEach(r => r.Enabled = true);
        _controller.SetAutomationsEnabled(false);

        _environment.Snapshot = _environment.Snapshot with { ActiveAudioProcesses = ["Spotify"] };
        _controller.Coordinator.Reevaluate();

        Assert.Null(_controller.ActiveProfile.ActivePresetId);
        Assert.False(new StateStore(_fs, @"C:\AppData\Soundstage\state.json").Load().AutomationsEnabled);
    }

    [Fact]
    public void Undo_RestoresEffectsChange()
    {
        _controller.Initialize();
        Assert.False(_controller.CanUndo);

        _controller.UpdateEffects(e => e with { NightMode = e.NightMode with { Enabled = true, Intensity = 80 } });
        Assert.True(_controller.ActiveProfile!.Effects.NightMode.Enabled);
        Assert.True(_controller.CanUndo);
        Assert.Contains("Night mode", _fs.ReadAllText(_layout.ChainFilePath));

        Assert.True(_controller.Undo());

        Assert.False(_controller.ActiveProfile!.Effects.NightMode.Enabled);
        Assert.DoesNotContain("Night mode", _fs.ReadAllText(_layout.ChainFilePath));
    }

    [Fact]
    public void Undo_RestoresPresetSwitch_StepByStep()
    {
        _controller.Initialize();
        _controller.ApplyPreset("music");
        _controller.ApplyPreset("gaming");
        Assert.Equal("gaming", _controller.ActiveProfile!.ActivePresetId);

        Assert.True(_controller.Undo());
        Assert.Equal("music", _controller.ActiveProfile!.ActivePresetId);
        Assert.Contains("preset: Music", _fs.ReadAllText(_layout.ChainFilePath));
        Assert.Contains("Fc 80 Hz", _fs.ReadAllText(_layout.ChainFilePath));

        Assert.True(_controller.Undo());
        Assert.Null(_controller.ActiveProfile!.ActivePresetId);
        Assert.False(_controller.Undo()); // stack empty
    }

    [Fact]
    public void AutomationApplies_DoNotPolluteTheUndoStack()
    {
        _controller.Initialize();
        _controller.ApplyPreset("mild", ApplyAttribution.Rule("r1", "some rule"));
        _controller.UpdateEffects(
            e => e with { Loudness = e.Loudness with { Enabled = true } },
            ApplyAttribution.Rule("r1", "some rule"));

        Assert.False(_controller.CanUndo);
    }

    [Fact]
    public void ManualApply_GetsGuarded_ConfirmPersistsHash()
    {
        _controller.Initialize();
        _controller.State.Settings.ConfirmNewSounds = true;
        _controller.ApplyPreset("music");
        Assert.Equal(RevertGuardStatus.Pending, _controller.Orchestrator.Guard.Status);

        _controller.ConfirmPendingApply();

        Assert.Equal(RevertGuardStatus.Idle, _controller.Orchestrator.Guard.Status);
        var persisted = new StateStore(_fs, @"C:\AppData\Soundstage\state.json").Load();
        Assert.NotEmpty(persisted.ConfirmedChainHashes);
    }
}
