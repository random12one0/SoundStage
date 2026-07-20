using Soundstage.Core.Apo;
using Soundstage.Core.Configio;
using Soundstage.Core.Presets;
using Soundstage.Core.State;
using Soundstage.Core.Tests.Support;
using Xunit;

namespace Soundstage.Core.Tests.Configio;

public class ApplyOrchestratorTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly TestClock _clock = new();
    private readonly TestScheduler _scheduler = new();
    private readonly ConfigLayout _layout = new(@"C:\config");
    private readonly BackupService _backups;
    private readonly ApplyOrchestrator _orchestrator;
    private readonly SoundstageState _state;

    private static readonly EqPreset Boosty = new()
    {
        Id = "boosty",
        Name = "Boosty",
        Bands = [new EqBand(FilterType.Peaking, 1000, 6.0, 1.0)],
    };

    private static readonly EqPreset Mild = new()
    {
        Id = "mild",
        Name = "Mild",
        Bands = [new EqBand(FilterType.Peaking, 3000, 1.0, 1.0)],
    };

    public ApplyOrchestratorTests()
    {
        _backups = new BackupService(_fs, _layout, _clock);
        _orchestrator = new ApplyOrchestrator(_fs, _layout, _backups, new RevertGuard(_scheduler));

        _state = new SoundstageState();
        // These tests exercise the opt-in confirm-or-revert path.
        _state.Settings.ConfirmNewSounds = true;
        _state.Profiles.Add(new DeviceProfile
        {
            EndpointId = "{0.0.0.00000000}.{aaaa1111-2222-3333-4444-555566667777}",
            FriendlyName = "Speakers",
            ActivePresetId = "boosty",
        });

        // Simulate post-takeover state: chain + switch exist.
        _fs.WriteAllTextAtomic(_layout.ChainFilePath, _layout.BuildEmptyChainContent());
        _fs.WriteAllTextAtomic(_layout.SwitchFilePath, _layout.BuildSwitchContent(bypassed: false));
    }

    private static EqPreset? Resolve(string id) => id switch
    {
        "boosty" => Boosty,
        "mild" => Mild,
        _ => null,
    };

    [Fact]
    public void ManualApply_IsNotGuarded_ByDefault()
    {
        // Default settings: no confirm prompts — Undo is the safety net instead.
        _state.Settings.ConfirmNewSounds = false;
        var result = _orchestrator.Apply(_state, Resolve, ApplyAttribution.Manual());
        Assert.True(result.Changed);
        Assert.False(result.Guarded);
    }

    [Fact]
    public void ManualApply_OfNewChain_IsGuarded_AndWritesChain()
    {
        var result = _orchestrator.Apply(_state, Resolve, ApplyAttribution.Manual());

        Assert.True(result.Changed);
        Assert.True(result.Guarded);
        Assert.Contains("Filter 1: ON PK Fc 1000 Hz Gain 6.0 dB Q 1", _fs.ReadAllText(_layout.ChainFilePath));
        // Previous chain was backed up.
        Assert.Single(_backups.List(), b => b.Kind == BackupKind.Chain);
    }

    [Fact]
    public void GuardTimeout_RestoresPreviousChain()
    {
        var before = _fs.ReadAllText(_layout.ChainFilePath);
        _orchestrator.Apply(_state, Resolve, ApplyAttribution.Manual());

        _scheduler.FireAll();

        Assert.Equal(before, _fs.ReadAllText(_layout.ChainFilePath));
    }

    [Fact]
    public void ConfirmedHash_SkipsGuard_OnReapply()
    {
        var first = _orchestrator.Apply(_state, Resolve, ApplyAttribution.Manual());
        _orchestrator.Guard.Confirm();
        Assert.Contains(first.ChainHash, _state.ConfirmedChainHashes);

        // Switch away (different preset) and back.
        _state.Profiles[0].ActivePresetId = "mild";
        _orchestrator.Apply(_state, Resolve, ApplyAttribution.Manual());
        _orchestrator.Guard.Confirm();

        _state.Profiles[0].ActivePresetId = "boosty";
        var again = _orchestrator.Apply(_state, Resolve, ApplyAttribution.Manual());

        Assert.True(again.Changed);
        Assert.False(again.Guarded);
    }

    [Fact]
    public void SupersededApply_DoesNotGetItsHashConfirmed_WhenLaterApplyIsKept()
    {
        // Apply A (guarded), then B while A's countdown runs, then the user keeps B.
        var first = _orchestrator.Apply(_state, Resolve, ApplyAttribution.Manual());
        _state.Profiles[0].ActivePresetId = "mild";
        var second = _orchestrator.Apply(_state, Resolve, ApplyAttribution.Manual());

        _orchestrator.Guard.Confirm();

        // Only B — the sound the user actually heard and kept — is whitelisted.
        Assert.Contains(second.ChainHash, _state.ConfirmedChainHashes);
        Assert.DoesNotContain(first.ChainHash, _state.ConfirmedChainHashes);
    }

    [Fact]
    public void RuleApply_IsNeverGuarded()
    {
        var result = _orchestrator.Apply(_state, Resolve, ApplyAttribution.Rule("r1", "Night mode after 10pm"));
        Assert.True(result.Changed);
        Assert.False(result.Guarded);
        Assert.Equal(AttributionSource.Rule, _state.LastAttribution!.Source);
    }

    [Fact]
    public void UnchangedState_IsANoOp_ButUpdatesAttribution()
    {
        _orchestrator.Apply(_state, Resolve, ApplyAttribution.Manual());
        _orchestrator.Guard.Confirm();
        var writesBefore = _fs.WriteLog.Count;

        var again = _orchestrator.Apply(_state, Resolve, ApplyAttribution.Rule("r9", "Same thing"));

        Assert.False(again.Changed);
        Assert.Equal(writesBefore, _fs.WriteLog.Count);
        Assert.Equal("r9", _state.LastAttribution!.RuleId);
    }

    [Fact]
    public void SetBypass_TogglesOnlySwitchFile()
    {
        var chainBefore = _fs.ReadAllText(_layout.ChainFilePath);

        Assert.True(_orchestrator.SetBypass(_state, bypassed: true));
        Assert.True(ConfigLayout.IsSwitchBypassed(_fs.ReadAllText(_layout.SwitchFilePath)));
        Assert.Equal(chainBefore, _fs.ReadAllText(_layout.ChainFilePath));
        Assert.True(_state.BypassActive);

        // Idempotent.
        Assert.False(_orchestrator.SetBypass(_state, bypassed: true));

        Assert.True(_orchestrator.SetBypass(_state, bypassed: false));
        Assert.False(ConfigLayout.IsSwitchBypassed(_fs.ReadAllText(_layout.SwitchFilePath)));
    }

    [Fact]
    public void Apply_RepairsSwitchFile_IfSomethingClobberedIt()
    {
        _fs.WriteAllTextAtomic(_layout.SwitchFilePath, "garbage");
        _orchestrator.Apply(_state, Resolve, ApplyAttribution.Manual());
        Assert.Equal(_layout.BuildSwitchContent(bypassed: false), _fs.ReadAllText(_layout.SwitchFilePath));
    }

    [Fact]
    public void RestoreBackup_SwapsChain_AndBacksUpCurrent()
    {
        _orchestrator.Apply(_state, Resolve, ApplyAttribution.Rule("r1", "rule"));
        var entry = _backups.List().First(b => b.Kind == BackupKind.Chain);
        var backupContent = _backups.Read(entry);

        _orchestrator.RestoreBackup(entry);

        Assert.Equal(backupContent, _fs.ReadAllText(_layout.ChainFilePath));
    }
}
